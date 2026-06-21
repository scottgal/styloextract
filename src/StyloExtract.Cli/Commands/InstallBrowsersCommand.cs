using System.CommandLine;
using StyloExtract.Playwright;

namespace StyloExtract.Cli.Commands;

public static class InstallBrowsersCommand
{
    public static Command Build()
    {
        var browserOpt = new Option<string>("--browser") { DefaultValueFactory = _ => "chromium", Description = "Browser to install (chromium, firefox, webkit)." };

        var cmd = new Command("install-browsers", "Download and install Playwright browser binaries.");
        cmd.Add(browserOpt);
        cmd.SetAction((ParseResult pr) =>
        {
            var browser = pr.GetValue(browserOpt) ?? "chromium";
            Console.Error.WriteLine($"Installing {browser}... this may take a minute.");
            var exit = PlaywrightInstaller.EnsureBrowsersInstalled(browser);
            if (exit == 0)
            {
                Console.Error.WriteLine($"{browser} installed.");
            }
            else
            {
                Console.Error.WriteLine($"Install failed with exit code {exit}.");
            }
            return exit;
        });
        return cmd;
    }
}
