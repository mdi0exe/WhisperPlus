using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using WhisperPlus.Services;

namespace WhisperPlus.Views.Controls;

/// <summary>
/// Animated audio-level visualizer. Renders a row of rounded bars, mirrored around the
/// centerline, whose heights track a <see cref="WaveformAggregator"/>. A
/// <see cref="DispatcherTimer"/> ticks at ~60fps to sample the level, scroll the history left,
/// and ease each bar toward its target so the meter moves like a physical thing instead of
/// snapping between frames.
///
/// <para>The control is dependency-property driven: set <see cref="Aggregator"/> and call
/// <see cref="Start"/>/<see cref="Stop"/>. Bars are painted with <see cref="BarBrush"/> —
/// typically the accent gradient — and the newest bars carry a soft glow, which is what makes
/// the widget read as "live" at a glance.</para>
/// </summary>
public partial class WaveformControl : UserControl
{
    private const int BarCount = 34;
    private const double Fps = 60;
    private const double BarWidth = 3.0;
    private const double MinBarHeight = 3.0;

    /// <summary>
    /// Per-frame easing toward the sampled level. Rises fast so a hard consonant is not
    /// swallowed, falls slower so the meter doesn't flicker between syllables.
    /// </summary>
    private const double AttackPerFrame = 0.55;
    private const double ReleasePerFrame = 0.16;

    private readonly DispatcherTimer _timer;
    private readonly List<Rectangle> _bars = new(BarCount);

    /// <summary>Sampled level history, oldest first — the shape being drawn.</summary>
    private readonly float[] _barLevels = new float[BarCount];

    /// <summary>What is actually on screen, chasing <see cref="_barLevels"/>.</summary>
    private readonly double[] _rendered = new double[BarCount];

    private Random? _idleJitter; // tiny noise so idle bars breathe slightly

    public WaveformControl()
    {
        InitializeComponent();
        BuildBars();

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / Fps),
        };
        _timer.Tick += OnTick;
    }

    /// <summary>The aggregator whose level drives the bars. Set this before <see cref="Start"/>.</summary>
    public static readonly DependencyProperty AggregatorProperty =
        DependencyProperty.Register(nameof(Aggregator), typeof(WaveformAggregator), typeof(WaveformControl),
            new PropertyMetadata(null));

    public WaveformAggregator? Aggregator
    {
        get => (WaveformAggregator?)GetValue(AggregatorProperty);
        set => SetValue(AggregatorProperty, value);
    }

    /// <summary>Brush used to paint the bars. Defaults to white with some translucency.</summary>
    public static readonly DependencyProperty BarBrushProperty =
        DependencyProperty.Register(nameof(BarBrush), typeof(Brush), typeof(WaveformControl),
            new PropertyMetadata(null, OnBarBrushChanged));

    public Brush? BarBrush
    {
        get => (Brush?)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    /// <summary>Color of the bloom behind the leading bars. Set to match the accent.</summary>
    public static readonly DependencyProperty GlowColorProperty =
        DependencyProperty.Register(nameof(GlowColor), typeof(Color), typeof(WaveformControl),
            new PropertyMetadata(Color.FromRgb(0x7C, 0xE7, 0xFF), OnGlowColorChanged));

    public Color GlowColor
    {
        get => (Color)GetValue(GlowColorProperty);
        set => SetValue(GlowColorProperty, value);
    }

    /// <summary>Whether bars animate with a subtle idle shimmer even when silent.</summary>
    public bool ShowIdleAnimation { get; set; } = true;

    /// <summary>Starts the animation timer. Cheap to call repeatedly.</summary>
    public void Start()
    {
        if (!_timer.IsEnabled) _timer.Start();
    }

    /// <summary>Stops the timer and flattens all bars (call when the widget hides).</summary>
    public void Stop()
    {
        _timer.Stop();
        Array.Clear(_barLevels);
        Array.Clear(_rendered);
        foreach (var b in _bars)
        {
            b.Height = MinBarHeight;
            Canvas.SetTop(b, ActualHeight / 2 - MinBarHeight / 2);
        }
    }

    private void BuildBars()
    {
        BarsCanvas.Children.Clear();
        _bars.Clear();
        var brush = BarBrush ?? new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));

        for (var i = 0; i < BarCount; i++)
        {
            var bar = new Rectangle
            {
                Width = BarWidth,
                Height = MinBarHeight,
                RadiusX = BarWidth / 2,
                RadiusY = BarWidth / 2,
                Fill = brush,
                // Older samples fade out towards the left, so the meter reads as motion
                // rather than as a static equalizer.
                Opacity = 0.35 + 0.65 * (i / (double)(BarCount - 1)),
            };
            _bars.Add(bar);
            BarsCanvas.Children.Add(bar);
        }

        ApplyGlow();
        SizeChanged += (_, _) => LayoutBars();
    }

    private static void OnBarBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WaveformControl control || e.NewValue is not Brush brush) return;
        foreach (var bar in control._bars) bar.Fill = brush;
    }

    private static void OnGlowColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WaveformControl control) control.ApplyGlow();
    }

    /// <summary>
    /// Blooms only the newest few bars. One shared effect over the whole canvas would blur the
    /// entire history and cost far more per frame; a handful of small effects reads better and
    /// keeps the widget cheap enough to run at 60fps.
    /// </summary>
    private void ApplyGlow()
    {
        const int glowTail = 6;
        for (var i = 0; i < _bars.Count; i++)
        {
            var distanceFromHead = _bars.Count - 1 - i;
            _bars[i].Effect = distanceFromHead < glowTail
                ? new DropShadowEffect
                {
                    Color = GlowColor,
                    BlurRadius = 9,
                    ShadowDepth = 0,
                    Opacity = 0.75 - 0.1 * distanceFromHead,
                }
                : null;
        }
    }

    private void LayoutBars()
    {
        if (ActualWidth <= 0 || _bars.Count == 0) return;
        var slot = ActualWidth / _bars.Count;
        for (var i = 0; i < _bars.Count; i++)
        {
            var x = i * slot + (slot - _bars[i].Width) / 2;
            Canvas.SetLeft(_bars[i], x);
            // Vertically center: the bar grows from the middle outward.
            Canvas.SetTop(_bars[i], ActualHeight / 2 - _bars[i].Height / 2);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var (level, _) = Aggregator?.Read() ?? (0f, 0f);
        Aggregator?.ApplyIdleDecay();

        // Shift the history left so the waveform reads right-to-left like a live meter.
        for (var i = 0; i < _barLevels.Length - 1; i++)
            _barLevels[i] = _barLevels[i + 1];
        _barLevels[^1] = level;

        _idleJitter ??= new Random();
        var span = Math.Max(0, ActualHeight - MinBarHeight - 2);

        for (var i = 0; i < _bars.Count; i++)
        {
            var h = _barLevels[i];
            if (h < 0.02f && ShowIdleAnimation)
            {
                // Subtle idle shimmer so the meter isn't perfectly flat.
                h = (float)(_idleJitter.NextDouble() * 0.04 + 0.01);
            }

            // Microphone RMS is commonly 0.01-0.08. Apply a square-root curve so
            // normal speaking produces a readable waveform instead of a row of dots.
            var target = Math.Clamp(MathF.Sqrt(Math.Max(0, h) * 12f), 0f, 1f);

            // Ease toward the target rather than jumping to it.
            var current = _rendered[i];
            var rate = target > current ? AttackPerFrame : ReleasePerFrame;
            current += (target - current) * rate;
            _rendered[i] = current;

            var height = Math.Max(MinBarHeight, MinBarHeight + current * span);
            _bars[i].Height = height;
            Canvas.SetTop(_bars[i], ActualHeight / 2 - height / 2);
        }
    }
}
