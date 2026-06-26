namespace StyloExtract.Abstractions;

public sealed record RenderOptions
{
    public TimeSpan NavigationTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan WaitForNetworkIdleTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public string? UserAgent { get; init; }
    public int? ViewportWidth { get; init; }
    public int? ViewportHeight { get; init; }
    public bool EmulateMobile { get; init; }

    /// <summary>
    /// Which page event to wait for before considering the navigation complete.
    /// Default is NetworkIdle (matches alpha.13 and earlier behaviour: wait for
    /// 500ms of network silence). Consumers fetching sites with aggressive
    /// client-side routing (BBC News, Twitter/X, many SPAs that auto-navigate
    /// in the post-load JS phase) should prefer <see cref="PlaywrightWaitUntil.Load"/> or
    /// <see cref="PlaywrightWaitUntil.DOMContentLoaded"/> to capture the initial DOM
    /// before the router fires.
    /// </summary>
    public PlaywrightWaitUntil WaitUntil { get; init; } = PlaywrightWaitUntil.NetworkIdle;
}

/// <summary>
/// Mirrors Microsoft.Playwright.WaitUntilState. Wrapper here so consumers
/// don't have to take a direct dependency on Microsoft.Playwright just to
/// pick a wait strategy.
/// </summary>
public enum PlaywrightWaitUntil
{
    /// <summary>Wait until the 'load' event fires (default DOM + sub-resources loaded). Fastest for SPAs that route post-load.</summary>
    Load,
    /// <summary>Wait until the 'DOMContentLoaded' event. Earliest; HTML parsed but sub-resources may not be loaded.</summary>
    DOMContentLoaded,
    /// <summary>Wait until 500ms of network silence (Playwright's default). Catches most XHR-driven content but lets aggressive client-side routers navigate away.</summary>
    NetworkIdle,
    /// <summary>Wait until the 'commit' event (network response received, before DOM parse). Rarely useful.</summary>
    Commit,
}
