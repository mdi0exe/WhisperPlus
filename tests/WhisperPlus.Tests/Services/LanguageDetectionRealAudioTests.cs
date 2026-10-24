using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using WhisperPlus.Models;
using WhisperPlus.Services;
using WhisperPlus.Tests.Services.Fakes;
using Xunit;
using Xunit.Abstractions;

namespace WhisperPlus.Tests.Services;

/// <summary>
/// Runs the real language-identification pipeline — Silero, the onset detector, the schedule and the
/// accumulator — against real recordings, and reports what every window actually said.
///
/// <para>Everything else in this suite tests the decision rule against hand-written observations,
/// which proves the arithmetic and proves nothing about the model. These tests close that gap: they
/// are the only place a claim like "it detects Persian" can be checked rather than asserted.</para>
///
/// <para>They skip themselves when the recordings or the Silero model are missing, so a checkout
/// without either still runs green. The paths are read from environment variables so a different
/// machine can point them somewhere else:</para>
/// <list type="bullet">
///   <item><c>WHISPERPLUS_TEST_AUDIO</c> — folder holding <c>english.wav</c> and <c>farsi.wav</c>.</item>
///   <item><c>WHISPERPLUS_MODELS</c> — the models directory, the one holding <c>lid/</c>.</item>
/// </list>
/// </summary>
public class LanguageDetectionRealAudioTests
{
    private const int SampleRate = 16000;

    private readonly ITestOutputHelper _output;

    public LanguageDetectionRealAudioTests(ITestOutputHelper output) => _output = output;

    private static string AudioDirectory =>
        Environment.GetEnvironmentVariable("WHISPERPLUS_TEST_AUDIO")
        ?? @"C:\Users\mahdi\Desktop\wisperplus";

    private static string ModelsDirectory =>
        Environment.GetEnvironmentVariable("WHISPERPLUS_MODELS")
        ?? @"D:\Mdi\Programs\WhisperModels";

    private static bool CanRun(string fileName, out string path)
    {
        path = Path.Combine(AudioDirectory, fileName);
        if (!File.Exists(path)) return false;

        var model = Path.Combine(ModelsDirectory, "lid", LanguageIdModelCatalog.FileName);
        return File.Exists(model);
    }

    private static SileroLanguageIdentifier BuildIdentifier()
    {
        var settings = new FakeSettingsService(new AppSettings
        {
            WhisperModelsDirectory = ModelsDirectory,
        });
        return new SileroLanguageIdentifier(new LanguageIdModelManager(settings));
    }

    /// <summary>
    /// Reads a wav file as 16 kHz mono float32 — the format every engine in the app works in, and
    /// what the capture pipeline produces. These recordings are 44.1 kHz, so this also exercises the
    /// resampling a caller would have to do.
    /// </summary>
    private static float[] ReadAsCaptureAudio(string path)
    {
        using var reader = new AudioFileReader(path);

        ISampleProvider provider = reader;
        if (provider.WaveFormat.Channels > 1) provider = provider.ToMono();
        if (provider.WaveFormat.SampleRate != SampleRate)
            provider = new WdlResamplingSampleProvider(provider, SampleRate);

        var samples = new List<float>((int)(reader.Length / 2));
        var buffer = new float[SampleRate];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            for (var i = 0; i < read; i++) samples.Add(buffer[i]);

        return samples.ToArray();
    }

    /// <summary>
    /// The session's detection logic, minus the session: onset, then the same schedule and
    /// accumulator over the same windows, so what this reports is what dictation would decide.
    /// </summary>
    /// <param name="firstWindowMs">
    /// Length of the opening window, which is the one thing that differs between deciding the language
    /// while the user speaks and deciding it when they stop. Live, it is short because the model preload
    /// is waiting on it; deferred, nothing is waiting, so it is as long as the backend can use.
    /// </param>
    private (string? Code, LanguageEvidenceAccumulator Evidence) Detect(
        float[] audio, IReadOnlyList<string> candidates, ILanguageIdentifier identifier,
        int firstWindowMs = 1200)
    {
        var onset = new SpeechOnsetDetector(SampleRate);
        onset.Feed(audio);

        var origin = (int)(onset.OnsetSample ?? onset.BestGuessOnsetSample);
        _output.WriteLine(
            $"{audio.Length / (double)SampleRate:0.0} s of audio; onset at {origin * 1000 / SampleRate} ms, " +
            $"peak {onset.PeakLevel:0.0000}, speech threshold {onset.SpeechThreshold:0.0000}, " +
            $"pause threshold {onset.PauseThreshold:0.0000}");

        var evidence = new LanguageEvidenceAccumulator(
            candidates, null, new LanguageEvidenceOptions { ReferenceLengthMs = identifier.MaxWindowMs });

        var probes = new LanguageProbeScheduler(
            firstWindowMs: firstWindowMs,
            windowMs: identifier.MaxWindowMs,
            minWindowMs: identifier.MinWindowMs,
            maxWindows: 10);

        while (probes.TryNext((audio.Length - origin) * 1000 / SampleRate, allowOverlap: true, out var probe))
        {
            probes.MarkExamined(probe);

            var from = origin + probe.StartMs * SampleRate / 1000;
            var count = Math.Min(probe.LengthMs * SampleRate / 1000, audio.Length - from);
            var window = new float[count];
            Array.Copy(audio, from, window, 0, count);

            var voiced = onset.VoicedRatioOf(window);
            if (voiced < 0.35)
            {
                evidence.AddExamined(probe.StartMs, probe.LengthMs);
                _output.WriteLine($"  +{probe.StartMs,6} ms ({probe.LengthMs,5} ms)  skipped, {voiced:P0} voiced");
                continue;
            }

            var observation = identifier.Observe(
                window, candidates, new ObservationContext(probe.StartMs, voiced));

            if (observation is null)
            {
                evidence.AddExamined(probe.StartMs, probe.LengthMs);
                _output.WriteLine($"  +{probe.StartMs,6} ms ({probe.LengthMs,5} ms)  declined");
                continue;
            }

            var weight = evidence.Add(observation);
            var distribution = string.Join(" ", Distribution(observation));
            _output.WriteLine(
                $"  +{probe.StartMs,6} ms ({probe.LengthMs,5} ms)  {distribution}  " +
                $"mass {observation.InDomainMass:P1}  voiced {voiced:P0}  weight {weight:0.00}");

            if (evidence.Evaluate(null, false, 0).IsCommitted) break;
        }

        var verdict = evidence.Concede(null, false, 0);
        _output.WriteLine($"  => {evidence.Describe()}  |  verdict: {verdict.Code ?? "none"} ({verdict.State})");
        return (verdict.Code, evidence);
    }

    private static IEnumerable<string> Distribution(LanguageObservation observation)
    {
        for (var i = 0; i < observation.Codes.Count; i++)
            yield return $"{observation.Codes[i]}={observation.Probabilities[i]:0.000}";
    }

    /// <summary>
    /// What the identifier sees when the audio reaching it is not at the rate it believes.
    ///
    /// <para>Diagnostic rather than a guard: it pins the <em>signature</em> of a sample-rate mismatch —
    /// the in-domain mass collapsing to near zero while the audio is plainly speech — so that
    /// signature can be recognized in a log instead of being mistaken for a bad classifier or a bad
    /// decision rule. Both are the obvious suspects when detection fails, and both would be the wrong
    /// thing to go and fix.</para>
    /// </summary>
    [Fact]
    public void Audio_At_The_Wrong_Rate_Reads_As_No_Language_At_All()
    {
        if (!CanRun("farsi.wav", out var path)) return;

        using var identifier = BuildIdentifier();
        Assert.True(identifier.EnsureReadyAsync().GetAwaiter().GetResult(), "the Silero model failed to load");

        // The same speech twice: once resampled properly, once with its 44.1 kHz samples handed over
        // as though they were 16 kHz — which stretches every word to 2.76 times its length.
        var correct = ReadAsCaptureAudio(path);
        var mistimed = ReadRawSamples(path);

        // Anchored to the speech, not to an arbitrary offset: a slice that lands in a pause is
        // declined whatever its rate, which would prove nothing either way.
        var onset = new SpeechOnsetDetector(SampleRate);
        onset.Feed(correct);
        var startSeconds = (onset.OnsetSample ?? 0) / (double)SampleRate + 1.2;

        var window = 4 * SampleRate;
        var good = identifier.Observe(Slice(correct, (int)(startSeconds * SampleRate), window),
            new[] { "fa", "en" }, ObservationContext.Unknown);
        var bad = identifier.Observe(Slice(mistimed, (int)(startSeconds * 44100), window),
            new[] { "fa", "en" }, ObservationContext.Unknown);

        _output.WriteLine($"correct rate: {(good is null ? "declined" : $"{good.Best.Code} mass {good.InDomainMass:P2}")}");
        _output.WriteLine($"wrong rate:   {(bad is null ? "declined" : $"{bad.Best.Code} mass {bad.InDomainMass:P2}")}");

        Assert.NotNull(good);
        Assert.Equal("fa", good!.Best.Code);
        Assert.True(good.InDomainMass > 0.2, $"expected real speech to be in-domain, got {good.InDomainMass:P2}");

        // Declining outright, or holding a sliver of the mass, are the same finding: at the wrong rate
        // there is no language here to find.
        Assert.True(bad is null || bad.InDomainMass < 0.05,
            $"expected mistimed audio to look like nothing, got {bad?.InDomainMass:P2}");
    }

    /// <summary>Reads a file's samples as they are, ignoring its declared rate.</summary>
    private static float[] ReadRawSamples(string path)
    {
        using var reader = new AudioFileReader(path);

        var samples = new List<float>((int)(reader.Length / 2));
        var buffer = new float[65536];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            for (var i = 0; i < read; i++) samples.Add(buffer[i]);

        return samples.ToArray();
    }

    private static float[] Slice(float[] source, int start, int count)
    {
        count = Math.Min(count, source.Length - start);
        var slice = new float[count];
        Array.Copy(source, start, slice, 0, count);
        return slice;
    }

    /// <summary>
    /// Reports what detection makes of whatever the app itself has recorded from the real microphone.
    /// Not an assertion — a microphone is not a fixture — but the only way to tell a capture problem
    /// apart from a detection problem, since both look identical from the outside.
    /// </summary>
    [Fact]
    public void Report_On_The_Apps_Own_Microphone_Recordings()
    {
        var folder = Environment.GetEnvironmentVariable("WHISPERPLUS_RECORDINGS")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "WhisperPlus Recordings");

        if (!Directory.Exists(folder)) return;

        var model = Path.Combine(ModelsDirectory, "lid", LanguageIdModelCatalog.FileName);
        if (!File.Exists(model)) return;

        using var identifier = BuildIdentifier();
        if (!identifier.EnsureReadyAsync().GetAwaiter().GetResult()) return;

        foreach (var path in Directory.GetFiles(folder, "*.wav"))
        {
            _output.WriteLine($"--- {Path.GetFileName(path)}");
            try { Detect(ReadAsCaptureAudio(path), new[] { "fa", "en" }, identifier); }
            catch (Exception ex) { _output.WriteLine($"  unreadable: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Asks the classifier what it hears in the app's own recordings without restricting it to the
    /// user's languages — the question "is this speech at all, and if so what?" rather than "which of
    /// these two?".
    ///
    /// <para>When detection fails because the candidates hold none of the mass, that number alone
    /// cannot say whether the audio is unintelligible or merely being heard as something else. This
    /// separates the two, and the answer decides what there is to fix.</para>
    /// </summary>
    [Fact]
    public void Report_What_The_Classifier_Hears_Unrestricted()
    {
        var folder = Environment.GetEnvironmentVariable("WHISPERPLUS_RECORDINGS")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "WhisperPlus Recordings");

        var model = Path.Combine(ModelsDirectory, "lid", LanguageIdModelCatalog.FileName);
        if (!Directory.Exists(folder) || !File.Exists(model)) return;

        using var identifier = BuildIdentifier();
        if (!identifier.EnsureReadyAsync().GetAwaiter().GetResult()) return;

        // Every language the model knows, so nothing is renormalized away.
        var everything = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in LanguageIdModelCatalog.ClassCodes)
        {
            var baseCode = code.Length >= 2 ? code[..2].ToLowerInvariant() : code;
            if (LanguageIdModelCatalog.Supports(baseCode) && seen.Add(baseCode)) everything.Add(baseCode);
        }

        var paths = new List<string>(Directory.GetFiles(folder, "*.wav"));
        foreach (var name in new[] { "english.wav", "farsi.wav" })
        {
            var reference = Path.Combine(AudioDirectory, name);
            if (File.Exists(reference)) paths.Add(reference);
        }

        foreach (var path in paths)
        {
            float[] audio;
            try { audio = ReadAsCaptureAudio(path); }
            catch (Exception ex) { _output.WriteLine($"--- {Path.GetFileName(path)}: unreadable: {ex.Message}"); continue; }

            // The loudest four seconds — wherever the speech actually is, whatever the gating thought.
            var window = LoudestWindow(audio, 4 * SampleRate);
            var observation = identifier.Observe(window, everything, ObservationContext.Unknown);

            if (observation is null)
            {
                _output.WriteLine($"--- {Path.GetFileName(path),-46} declined outright");
                continue;
            }

            var ranked = new List<(string Code, double P)>();
            for (var i = 0; i < observation.Codes.Count; i++)
                ranked.Add((observation.Codes[i], observation.Probabilities[i]));
            ranked.Sort((a, b) => b.P.CompareTo(a.P));

            var top = new List<string>();
            for (var i = 0; i < Math.Min(6, ranked.Count); i++) top.Add($"{ranked[i].Code}={ranked[i].P:0.000}");

            _output.WriteLine(
                $"--- {Path.GetFileName(path),-46} rms={SpeechOnsetDetector.LevelOf(window):0.0000}  " +
                $"fa+en={observation.ProbabilityOf("fa") + observation.ProbabilityOf("en"):P2}  {string.Join(" ", top)}");
        }
    }

    /// <summary>The window of <paramref name="length"/> samples with the most energy in it.</summary>
    private static float[] LoudestWindow(float[] audio, int length)
    {
        length = Math.Min(length, audio.Length);
        var step = SampleRate / 10;
        var bestStart = 0;
        var bestLevel = -1f;

        for (var start = 0; start + length <= audio.Length; start += step)
        {
            var level = SpeechOnsetDetector.LevelOf(audio.AsSpan(start, length));
            if (level <= bestLevel) continue;
            bestLevel = level;
            bestStart = start;
        }

        return Slice(audio, bestStart, length);
    }

    [Fact]
    public void Detects_English_In_A_Real_Recording()
    {
        if (!CanRun("english.wav", out var path)) return;

        using var identifier = BuildIdentifier();
        Assert.True(identifier.EnsureReadyAsync().GetAwaiter().GetResult(), "the Silero model failed to load");

        var (code, _) = Detect(ReadAsCaptureAudio(path), new[] { "fa", "en" }, identifier);

        Assert.Equal("en", code);
    }

    [Fact]
    public void Detects_Persian_In_A_Real_Recording()
    {
        if (!CanRun("farsi.wav", out var path)) return;

        using var identifier = BuildIdentifier();
        Assert.True(identifier.EnsureReadyAsync().GetAwaiter().GetResult(), "the Silero model failed to load");

        var (code, _) = Detect(ReadAsCaptureAudio(path), new[] { "fa", "en" }, identifier);

        Assert.Equal("fa", code);
    }

    // ---- Deciding the language only once recording stops ----

    /// <summary>
    /// The deferred configuration, where the language is decided when the user presses the shortcut
    /// again: one pass over the whole recording with no short opening window, because nothing is
    /// waiting on an early answer any more.
    ///
    /// <para>Worth testing separately rather than assuming it follows from the live case. It is the
    /// same schedule and the same accumulator, but it is fed differently — full-length windows from the
    /// first one — and it is the only pass there is, so anything it gets wrong is what the user gets.
    /// These two tests are the only place that claim is checked against real speech.</para>
    /// </summary>
    [Fact]
    public void Detects_Persian_When_The_Decision_Waits_For_The_End()
    {
        if (!CanRun("farsi.wav", out var path)) return;

        using var identifier = BuildIdentifier();
        Assert.True(identifier.EnsureReadyAsync().GetAwaiter().GetResult(), "the Silero model failed to load");

        var (code, _) = Detect(ReadAsCaptureAudio(path), new[] { "fa", "en" }, identifier,
            firstWindowMs: identifier.MaxWindowMs);

        Assert.Equal("fa", code);
    }

    [Fact]
    public void Detects_English_When_The_Decision_Waits_For_The_End()
    {
        if (!CanRun("english.wav", out var path)) return;

        using var identifier = BuildIdentifier();
        Assert.True(identifier.EnsureReadyAsync().GetAwaiter().GetResult(), "the Silero model failed to load");

        var (code, _) = Detect(ReadAsCaptureAudio(path), new[] { "fa", "en" }, identifier,
            firstWindowMs: identifier.MaxWindowMs);

        Assert.Equal("en", code);
    }
}
