using BenchmarkDotNet.Running;

namespace StyloExtract.Performance.Benchmarks;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--dump")
        {
            await SmokeRunner.RunAsync();
            return;
        }
        if (args.Length > 0 && args[0] == "--realworld")
        {
            await SmokeRunner.RunAsync(realworld: true);
            return;
        }
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
