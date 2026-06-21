using System.CommandLine;
using StyloExtract.Cli.Commands;

var root = new RootCommand("StyloExtract CLI");
root.Add(ExtractCommand.Build());
root.Add(InstallBrowsersCommand.Build());
root.Add(ExportCommand.Build());
root.Add(ImportCommand.Build());
root.Add(MonitorCommand.Build());
return await root.Parse(args).InvokeAsync();
