using System.CommandLine;
using StyloExtract.Cli.Commands;

var root = new RootCommand("StyloExtract CLI");
root.Add(ExtractCommand.Build());
root.Add(InstallBrowsersCommand.Build());
return await root.Parse(args).InvokeAsync();
