using StyloExtract.Abstractions;
using Xunit;

namespace StyloExtract.Playwright.Tests;

/// <summary>
/// Pure record-init checks for <see cref="RenderOptions.WaitUntil"/> (alpha.15).
/// The actual Goto behaviour is verified by Playwright itself; we're not
/// re-testing its implementation — only that consumers can pick a wait
/// strategy without taking a transitive Microsoft.Playwright dependency.
/// </summary>
public class RenderOptionsTests
{
    [Fact]
    public void RenderOptions_DefaultsToNetworkIdle()
    {
        var opts = new RenderOptions();
        Assert.Equal(PlaywrightWaitUntil.NetworkIdle, opts.WaitUntil);
    }

    [Fact]
    public void RenderOptions_AcceptsLoadStrategy()
    {
        var opts = new RenderOptions { WaitUntil = PlaywrightWaitUntil.Load };
        Assert.Equal(PlaywrightWaitUntil.Load, opts.WaitUntil);
    }
}
