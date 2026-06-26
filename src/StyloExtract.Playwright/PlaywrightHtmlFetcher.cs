using System.Diagnostics;
using Microsoft.Playwright;
using StyloExtract.Abstractions;

namespace StyloExtract.Playwright;

/// <summary>
/// Fetches client-side-rendered HTML via a lazy-launched headless Chromium browser.
/// The single <see cref="IBrowser"/> instance is reused across calls; each call gets
/// its own isolated <see cref="IBrowserContext"/> and <see cref="IPage"/>.
/// </summary>
public sealed class PlaywrightHtmlFetcher : IRenderedHtmlFetcher, IAsyncDisposable, IDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly SemaphoreSlim _launchLock = new(1, 1);

    public async Task<RenderedHtmlResult> FetchAsync(Uri uri, RenderOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new RenderOptions();
        await EnsureLaunchedAsync().ConfigureAwait(false);

        var contextOptions = new BrowserNewContextOptions();
        if (options.UserAgent is not null)
            contextOptions.UserAgent = options.UserAgent;
        if (options.ViewportWidth is { } w && options.ViewportHeight is { } h)
            contextOptions.ViewportSize = new ViewportSize { Width = w, Height = h };
        if (options.EmulateMobile)
            contextOptions.IsMobile = true;

        var sw = Stopwatch.StartNew();
        await using var context = await _browser!.NewContextAsync(contextOptions).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);

        var response = await page.GotoAsync(uri.ToString(), new PageGotoOptions
        {
            Timeout = (float)options.NavigationTimeout.TotalMilliseconds,
            WaitUntil = options.WaitUntil switch
            {
                PlaywrightWaitUntil.Load => WaitUntilState.Load,
                PlaywrightWaitUntil.DOMContentLoaded => WaitUntilState.DOMContentLoaded,
                PlaywrightWaitUntil.Commit => WaitUntilState.Commit,
                _ => WaitUntilState.NetworkIdle,
            },
        }).ConfigureAwait(false);

        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions
            {
                Timeout = (float)options.WaitForNetworkIdleTimeout.TotalMilliseconds
            }).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Content loaded but network never went fully idle — proceed with what we have.
        }

        var html = await page.ContentAsync().ConfigureAwait(false);
        var title = await page.TitleAsync().ConfigureAwait(false);
        sw.Stop();

        return new RenderedHtmlResult
        {
            Html = html,
            FinalUri = new Uri(page.Url),
            StatusCode = response?.Status ?? 0,
            FetchTime = sw.Elapsed,
            Title = string.IsNullOrEmpty(title) ? null : title
        };
    }

    private async Task EnsureLaunchedAsync()
    {
        if (_browser is not null) return;
        await _launchLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_browser is not null) return;
            _playwright = await Microsoft.Playwright.Playwright.CreateAsync().ConfigureAwait(false);
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true }).ConfigureAwait(false);
        }
        finally
        {
            _launchLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
            await _browser.DisposeAsync().ConfigureAwait(false);
        _playwright?.Dispose();
        _launchLock.Dispose();
    }

    /// <summary>
    /// Sync dispose for the singleton-in-ServiceProvider case: when a sync
    /// ServiceProvider scope is disposed (e.g. <c>using var sp = ...</c>
    /// rather than <c>await using</c>), the DI container calls Dispose, not
    /// DisposeAsync. Without an <see cref="IDisposable"/> implementation
    /// that throws — visible in consumer smoke tests. Block-wait on the
    /// async path; safe here because container disposal happens off the
    /// request hot path.
    /// </summary>
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
