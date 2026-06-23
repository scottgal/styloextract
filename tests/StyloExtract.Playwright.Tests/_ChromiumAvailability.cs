using Microsoft.Playwright;

namespace StyloExtract.Playwright.Tests;

/// <summary>
/// Single source of truth for "is Chromium installed?" — the gate every
/// SkippableFact in this project checks before driving Playwright.
///
/// <para>
/// Earlier this lived as three near-identical inline try/catch blocks, one
/// per test class. The variants drifted: one matched on Playwright's
/// "Executable doesn't exist" message specifically; two swallowed every
/// exception. If Playwright changed the error message the message-matching
/// version would silently false-positive (skip too few tests), and the
/// catch-all versions would silently false-negative (skip on unrelated
/// failures like OOM). Lifting to one helper closes that drift surface.
/// </para>
///
/// <para>
/// The check is cached for the lifetime of the test process. Chromium
/// install state doesn't change mid-run; probing once on first ask and
/// reusing the answer keeps test startup snappy.
/// </para>
/// </summary>
internal static class ChromiumAvailability
{
    private static readonly Lazy<Task<bool>> _check = new(ProbeAsync);

    public static Task<bool> CheckAsync() => _check.Value;

    private static async Task<bool> ProbeAsync()
    {
        try
        {
            using var p = await Microsoft.Playwright.Playwright.CreateAsync();
            await using var b = await p.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            return true;
        }
        catch (PlaywrightException ex)
            when (ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
        {
            // The canonical "browser binaries not provisioned" path. The
            // operator runs `stylo-extract-playwright install-browsers` (or
            // the equivalent `playwright install chromium`) to fix it.
            return false;
        }
        catch
        {
            // Any other reason Playwright won't launch — missing system deps,
            // sandbox refused by the OS, etc. Treat as unavailable so the
            // test skips rather than crashes with a confusing stack trace.
            return false;
        }
    }
}
