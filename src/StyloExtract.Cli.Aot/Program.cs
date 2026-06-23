using System.CommandLine;
using SQLitePCL;
using StyloExtract.Cli.Shared.Commands;

// Bind the bundled native SQLite provider before any Microsoft.Data.Sqlite call. Required
// for AOT-published binaries (especially on macOS) where the default dynamic provider lookup
// fails. Matches the stylobot Console binary's pattern.
Batteries.Init();

var root = new RootCommand("StyloExtract CLI");
root.Add(ExtractCommand.Build());
root.Add(ExportCommand.Build());
root.Add(ImportCommand.Build());
root.Add(MonitorCommand.Build());
root.Add(TemplateCommand.Build());
root.Add(ExtractFeaturesCommand.Build());
return await root.Parse(args).InvokeAsync();
