namespace WhisperPlus.Services;

/// <summary>
/// A page whose contents are derived from state that can change while the page is off screen —
/// files on disk, settings edited on another page, a download that finished in the background.
///
/// <para><see cref="DiPageProvider"/> caches page instances so they keep their state across
/// navigations, which is what a settings UI wants. The cost is that a page built before a model was
/// downloaded would keep showing the pre-download state until the app restarted, so the shell calls
/// <see cref="Refresh"/> every time a page is navigated to.</para>
/// </summary>
public interface IRefreshablePage
{
    /// <summary>Rebuilds the page from the current settings and whatever is on disk.</summary>
    void Refresh();
}
