using System.CommandLine;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using StyloExtract.Abstractions;
using StyloExtract.AspNetCore;
using StyloExtract.Core.OperatorTemplates;

namespace StyloExtract.Cli.Shared.Commands;

/// <summary>
/// Operator-template management. Operators author per-host YAML files at the
/// configured root (default <c>config/templates/</c>) and the runtime hard-
/// overrides the induced extraction pipeline for any matching host.
///
/// <para>
/// The CLI is the source-of-truth surface: it edits the on-disk YAML directly.
/// The REST endpoints in <c>MapOperatorTemplateEndpoints</c> wrap the same
/// operations for tooling that prefers HTTP.
/// </para>
/// </summary>
public static class TemplateCommand
{
    public static Command Build()
    {
        var rootOpt = new Option<string>("--root")
        {
            Description = "Directory containing per-host operator template YAML files.",
            DefaultValueFactory = _ => "config/templates",
        };

        var cmd = new Command("template", "Manage operator-authored host extraction templates.");

        cmd.Add(BuildList(rootOpt));
        cmd.Add(BuildShow(rootOpt));
        cmd.Add(BuildAdd(rootOpt));
        cmd.Add(BuildRemove(rootOpt));
        cmd.Add(BuildTest(rootOpt));
        cmd.Options.Add(rootOpt);
        return cmd;
    }

    private static Command BuildList(Option<string> rootOpt)
    {
        var c = new Command("list", "List every operator template under --root.");
        c.SetAction(pr =>
        {
            var root = pr.GetValue(rootOpt)!;
            using var store = new YamlFileOperatorTemplateStore(root, watch: false);
            var all = store.List();
            if (all.Count == 0)
            {
                Console.WriteLine($"No operator templates in {root}.");
                return 0;
            }
            Console.WriteLine($"{all.Count} operator template(s) in {root}:");
            foreach (var t in all)
            {
                Console.WriteLine($"  {t.Host}  ({t.Rules.Count} rule(s), v{t.Version})  {t.Description}");
            }
            return 0;
        });
        return c;
    }

    private static Command BuildShow(Option<string> rootOpt)
    {
        var host = new Argument<string>("host") { Description = "Host whose template to print." };
        var c = new Command("show", "Print the YAML of one template.");
        c.Add(host);
        c.SetAction(pr =>
        {
            var root = pr.GetValue(rootOpt)!;
            var h = pr.GetValue(host)!;
            var path = Path.Combine(root, h + ".yaml");
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"No template for {h} at {path}.");
                return 1;
            }
            Console.Write(File.ReadAllText(path));
            return 0;
        });
        return c;
    }

    private static Command BuildAdd(Option<string> rootOpt)
    {
        var host = new Argument<string>("host") { Description = "Host this rule applies to." };
        var roleOpt = new Option<BlockRole>("--role") { Description = "Block role to bind the selectors to." };
        var selectorOpt = new Option<string[]>("--selector")
        {
            Description = "One or more CSS selectors for this role. Pass --selector multiple times for multiple selectors.",
        };
        var confidenceOpt = new Option<double>("--confidence")
        {
            Description = "Confidence to stamp on emitted blocks (0.0-1.0).",
            DefaultValueFactory = _ => 0.95,
        };
        var descOpt = new Option<string?>("--description")
        {
            Description = "Free-form description for the YAML (only set if creating a new file).",
        };
        var c = new Command("add", "Add or extend a per-host operator template.");
        c.Add(host);
        c.Options.Add(roleOpt);
        c.Options.Add(selectorOpt);
        c.Options.Add(confidenceOpt);
        c.Options.Add(descOpt);
        c.SetAction(pr =>
        {
            var root = pr.GetValue(rootOpt)!;
            var h = pr.GetValue(host)!;
            var role = pr.GetValue(roleOpt);
            var selectors = pr.GetValue(selectorOpt) ?? Array.Empty<string>();
            var confidence = pr.GetValue(confidenceOpt);
            var desc = pr.GetValue(descOpt);

            if (selectors.Length == 0)
            {
                Console.Error.WriteLine("at least one --selector is required");
                return 2;
            }
            if (confidence < 0 || confidence > 1)
            {
                Console.Error.WriteLine("--confidence must be between 0.0 and 1.0");
                return 2;
            }

            Directory.CreateDirectory(root);
            var path = Path.Combine(root, h + ".yaml");

            // Load existing rules if any; append the new rule. Round-tripping
            // edits through the parser then back to YAML avoids any drift from
            // hand-rolled string concatenation. The YAML emitter is the inverse
            // of the parser; it knows the same schema and produces the same shape.
            List<OperatorTemplateRule> rules;
            string description;
            int version;
            if (File.Exists(path))
            {
                var existing = YamlOperatorTemplateLoader.Parse(File.ReadAllText(path));
                rules = new List<OperatorTemplateRule>(existing.Rules);
                description = existing.Description;
                version = existing.Version;
            }
            else
            {
                rules = new List<OperatorTemplateRule>();
                description = desc ?? "";
                version = 1;
            }
            rules.Add(new OperatorTemplateRule
            {
                Role = role,
                Selectors = selectors,
                Confidence = confidence,
            });
            File.WriteAllText(path, EmitYaml(new OperatorTemplate
            {
                Host = h,
                Description = description,
                Version = version,
                Rules = rules,
            }));
            Console.WriteLine($"Added rule to {path}.");
            return 0;
        });
        return c;
    }

    private static Command BuildRemove(Option<string> rootOpt)
    {
        var host = new Argument<string>("host");
        var ruleIndex = new Option<int?>("--rule-index")
        {
            Description = "Remove only the rule at this 0-based index (omit to delete the whole template).",
        };
        var c = new Command("remove", "Remove a rule or the entire per-host template.");
        c.Add(host);
        c.Options.Add(ruleIndex);
        c.SetAction(pr =>
        {
            var root = pr.GetValue(rootOpt)!;
            var h = pr.GetValue(host)!;
            var idx = pr.GetValue(ruleIndex);
            var path = Path.Combine(root, h + ".yaml");
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"No template at {path}.");
                return 1;
            }
            if (idx is null)
            {
                File.Delete(path);
                Console.WriteLine($"Deleted {path}.");
                return 0;
            }
            var existing = YamlOperatorTemplateLoader.Parse(File.ReadAllText(path));
            if (idx < 0 || idx >= existing.Rules.Count)
            {
                Console.Error.WriteLine($"--rule-index {idx} out of range (template has {existing.Rules.Count} rules).");
                return 2;
            }
            var rules = new List<OperatorTemplateRule>(existing.Rules);
            rules.RemoveAt(idx.Value);
            if (rules.Count == 0)
            {
                File.Delete(path);
                Console.WriteLine($"Removed last rule; deleted {path}.");
                return 0;
            }
            File.WriteAllText(path, EmitYaml(existing with { Rules = rules }));
            Console.WriteLine($"Removed rule {idx} from {path}.");
            return 0;
        });
        return c;
    }

    private static Command BuildTest(Option<string> rootOpt)
    {
        var urlOpt = new Option<string>("--url") { Description = "URL to fetch and run extraction against." };
        var fileOpt = new Option<string?>("--file") { Description = "Local HTML file to run extraction against instead of fetching." };
        var hostOpt = new Option<string?>("--host") { Description = "Override the host used for template lookup (default: derived from --url or filename)." };
        var c = new Command("test", "Fetch a page (or read a file), run extraction, dump the markdown.");
        c.Options.Add(urlOpt);
        c.Options.Add(fileOpt);
        c.Options.Add(hostOpt);
        c.SetAction(async pr =>
        {
            var root = pr.GetValue(rootOpt)!;
            var url = pr.GetValue(urlOpt);
            var file = pr.GetValue(fileOpt);
            var hostOverride = pr.GetValue(hostOpt);

            if (string.IsNullOrEmpty(url) && string.IsNullOrEmpty(file))
            {
                Console.Error.WriteLine("--url or --file is required");
                return 2;
            }

            string html;
            Uri? sourceUri = null;
            string resolvedHost;
            if (!string.IsNullOrEmpty(url))
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 stylo-extract/template-test");
                html = await http.GetStringAsync(url);
                sourceUri = new Uri(url);
                resolvedHost = hostOverride ?? sourceUri.Host;
            }
            else
            {
                html = await File.ReadAllTextAsync(file!);
                resolvedHost = hostOverride ?? "file:" + Path.GetFileNameWithoutExtension(file);
            }

            using var store = new YamlFileOperatorTemplateStore(root, watch: false);
            var services = new ServiceCollection();
            services.AddStyloExtract((Action<StyloExtract.AspNetCore.StyloExtractOptions>?)null);
            services.AddSingleton<IOperatorTemplateStore>(store);
            var sp = services.BuildServiceProvider();
            var extractor = sp.GetRequiredService<ILayoutExtractor>();
            var result = await extractor.ExtractAsync(html, sourceUri, new ExtractionOptions
            {
                HostOverride = resolvedHost,
            });
            Console.WriteLine($"# match: {result.Match.Status}  blocks: {result.Blocks.Count}  host: {resolvedHost}");
            Console.WriteLine();
            Console.WriteLine(result.Markdown);
            return 0;
        });
        return c;
    }

    // Tiny YAML emitter that matches what YamlOperatorTemplateLoader accepts.
    // Stable indentation (2-space per nesting level) so output stays diff-friendly.
    internal static string EmitYaml(OperatorTemplate t)
    {
        var sb = new StringBuilder();
        sb.Append("host: ").Append(t.Host).Append('\n');
        if (!string.IsNullOrEmpty(t.Description))
            sb.Append("description: ").Append(t.Description).Append('\n');
        if (t.Version != 1)
            sb.Append("version: ").Append(t.Version).Append('\n');
        sb.Append("rules:\n");
        foreach (var rule in t.Rules)
        {
            sb.Append("  - role: ").Append(rule.Role).Append('\n');
            sb.Append("    selectors:\n");
            foreach (var sel in rule.Selectors)
            {
                sb.Append("      - ").Append(sel).Append('\n');
            }
            if (rule.Confidence != 1.0)
            {
                sb.Append("    confidence: ")
                    .Append(rule.Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append('\n');
            }
        }
        return sb.ToString();
    }
}
