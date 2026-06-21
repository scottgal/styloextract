using System.CommandLine;
using StyloExtract.Cli.Commands;
using SharedExport = StyloExtract.Cli.Shared.Commands.ExportCommand;
using SharedImport = StyloExtract.Cli.Shared.Commands.ImportCommand;
using SharedMonitor = StyloExtract.Cli.Shared.Commands.MonitorCommand;

var root = new RootCommand("StyloExtract CLI (Playwright edition)");
root.Add(ExtractCommandPlaywrightExtensions.Build());
root.Add(InstallBrowsersCommand.Build());
root.Add(SharedExport.Build());
root.Add(SharedImport.Build());
root.Add(SharedMonitor.Build());
return await root.Parse(args).InvokeAsync();
