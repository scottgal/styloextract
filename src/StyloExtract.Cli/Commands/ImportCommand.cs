using System.CommandLine;
using Microsoft.Data.Sqlite;
using StyloExtract.Templates;

namespace StyloExtract.Cli.Commands;

public static class ImportCommand
{
    public static Command Build()
    {
        var storeOpt = new Option<string>("--store") { Required = true, Description = "Path to the SQLite store." };
        var hostOpt = new Option<string>("--host") { Required = true, Description = "Host display name (e.g. example.com)." };
        var inOpt = new Option<string>("--in") { Required = true, Description = "Input JSON file path." };
        var keyOpt = new Option<string?>("--host-hash-key") { DefaultValueFactory = _ => null, Description = "Base64 HMAC key for host hashing. Must match the key used during export for templates to resolve correctly." };

        var cmd = new Command("import", "Import a JSON template bundle into a host.");
        cmd.Add(storeOpt);
        cmd.Add(hostOpt);
        cmd.Add(inOpt);
        cmd.Add(keyOpt);

        cmd.SetAction(async (ParseResult pr) =>
        {
            var store = pr.GetValue(storeOpt)!;
            var host = pr.GetValue(hostOpt)!;
            var inFile = pr.GetValue(inOpt)!;
            var key = pr.GetValue(keyOpt);

            using var conn = new SqliteConnection($"Data Source={store}");
            conn.Open();
            SqliteSchema.EnsureCreated(conn);

            var hasher = HostHasher.FromConfiguredKeyOrRandom(key);
            var hostHash = hasher.Hash(host);

            await using var fs = File.OpenRead(inFile);
            var result = await TemplateImporter.ImportAsync(conn, hostHash, fs, default);
            await Console.Error.WriteLineAsync($"Imported {result.ImportedCount}, replaced {result.ReplacedCount}");
            return 0;
        });

        return cmd;
    }
}
