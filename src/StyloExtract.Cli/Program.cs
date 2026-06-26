using System.CommandLine;
using SQLitePCL;
using StyloExtract.Cli.Commands;
using SharedExport = StyloExtract.Cli.Shared.Commands.ExportCommand;
using SharedImport = StyloExtract.Cli.Shared.Commands.ImportCommand;
using SharedMonitor = StyloExtract.Cli.Shared.Commands.MonitorCommand;
using SharedTemplate = StyloExtract.Cli.Shared.Commands.TemplateCommand;
using SharedFeatures = StyloExtract.Cli.Shared.Commands.ExtractFeaturesCommand;
using SharedSitemap = StyloExtract.Cli.Shared.Commands.SitemapCommand;

// Bind the bundled native SQLite provider before any Microsoft.Data.Sqlite call. Required
// for self-contained / single-file publish (especially on macOS) where the default dynamic
// provider lookup fails. Matches the stylobot Console binary's pattern.
Batteries.Init();

var root = new RootCommand("StyloExtract CLI (Playwright edition)");
root.Add(ExtractCommandPlaywrightExtensions.Build());
root.Add(InstallBrowsersCommand.Build());
root.Add(SharedExport.Build());
root.Add(SharedImport.Build());
root.Add(SharedMonitor.Build());
root.Add(SharedTemplate.Build());
root.Add(SharedFeatures.Build());
root.Add(SharedSitemap.Build());
return await root.Parse(args).InvokeAsync();
