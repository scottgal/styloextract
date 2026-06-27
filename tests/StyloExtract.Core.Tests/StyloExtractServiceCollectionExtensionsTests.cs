using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.Core.Tests;

public class StyloExtractServiceCollectionExtensionsTests
{
    [Fact]
    public void DefaultOptions_DoNotRegister_CorpusMiningCoordinator()
    {
        var services = new ServiceCollection();
        services.AddStyloExtract(o => o.StorePath = ":memory:");
        var sp = services.BuildServiceProvider();

        var hostedServices = sp.GetServices<IHostedService>().ToList();
        hostedServices.Should().NotContain(s => s is CorpusMiningCoordinator,
            because: "EnableCorpusMining defaults to false");
    }

    [Fact]
    public void EnableCorpusMining_True_RegistersCoordinatorAsHostedService()
    {
        var services = new ServiceCollection();
        services.AddStyloExtract(o =>
        {
            o.StorePath = ":memory:";
            o.EnableCorpusMining = true;
        });
        var sp = services.BuildServiceProvider();

        var hostedServices = sp.GetServices<IHostedService>().ToList();
        hostedServices.Should().Contain(s => s is CorpusMiningCoordinator,
            because: "EnableCorpusMining = true must register the coordinator");
    }

    [Fact]
    public void SubMinuteInterval_IsClampedTo_OneMinute()
    {
        var services = new ServiceCollection();
        services.AddStyloExtract(o =>
        {
            o.StorePath = ":memory:";
            o.EnableCorpusMining = true;
            o.CorpusMiningInterval = TimeSpan.FromSeconds(30);
        });
        var sp = services.BuildServiceProvider();

        var coordinator = sp.GetServices<IHostedService>()
            .OfType<CorpusMiningCoordinator>()
            .Single();

        // Interval is private — reach it via reflection. The clamp lives
        // in the DI registration; this test guards the floor explicitly.
        var field = typeof(CorpusMiningCoordinator).GetField(
            "_interval",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field.Should().NotBeNull("the coordinator stores the interval on _interval");
        var actual = (TimeSpan)field!.GetValue(coordinator)!;
        actual.Should().Be(TimeSpan.FromMinutes(1),
            because: "30s requests must clamp up to the 1-minute floor");
    }

    [Fact]
    public void EnableCorpusMining_True_AlsoRegistersEmitterAndMiner()
    {
        var services = new ServiceCollection();
        services.AddStyloExtract(o =>
        {
            o.StorePath = ":memory:";
            o.EnableCorpusMining = true;
        });
        var sp = services.BuildServiceProvider();

        sp.GetService<CorpusMiner>().Should().NotBeNull();
        sp.GetService<EvolvedSelectorEmitter>().Should().NotBeNull();
    }
}
