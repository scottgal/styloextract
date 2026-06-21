namespace StyloExtract.Abstractions;

public sealed record RenderOptions
{
    public TimeSpan NavigationTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan WaitForNetworkIdleTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public string? UserAgent { get; init; }
    public int? ViewportWidth { get; init; }
    public int? ViewportHeight { get; init; }
    public bool EmulateMobile { get; init; }
}
