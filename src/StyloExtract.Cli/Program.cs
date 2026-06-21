using System.CommandLine;
using StyloExtract.Cli.Commands;

var root = new RootCommand("StyloExtract CLI");
root.Add(ExtractCommand.Build());
return await root.Parse(args).InvokeAsync();
