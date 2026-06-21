namespace StyloExtract.Playwright;

/// <summary>
/// Helpers for installing and probing the Playwright browser binaries.
/// Call <see cref="EnsureBrowsersInstalled"/> once at application startup
/// (or from a setup script) before constructing <see cref="PlaywrightHtmlFetcher"/>.
/// </summary>
public static class PlaywrightInstaller
{
    /// <summary>
    /// Downloads and installs the specified browser binaries Playwright needs.
    /// Idempotent — safe to call repeatedly. Returns exit code 0 on success.
    /// </summary>
    /// <param name="browser">Browser to install; defaults to "chromium".</param>
    public static int EnsureBrowsersInstalled(string browser = "chromium")
    {
        return Microsoft.Playwright.Program.Main(["install", browser]);
    }

    /// <summary>
    /// Returns <see langword="true"/> when the Chromium executable is already present
    /// on disk. Does not trigger a download.
    /// </summary>
    public static async Task<bool> BrowsersAvailableAsync()
    {
        try
        {
            using var pw = await Microsoft.Playwright.Playwright.CreateAsync();
            var path = pw.Chromium.ExecutablePath;
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }
        catch
        {
            return false;
        }
    }
}
