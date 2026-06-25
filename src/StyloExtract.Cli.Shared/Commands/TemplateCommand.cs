using System.CommandLine;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using StyloExtract.Abstractions;
using StyloExtract.AspNetCore;
using StyloExtract.Core.Llm;
using StyloExtract.Core.OperatorTemplates;
using StyloExtract.Core.Skeleton;
using StyloExtract.Html;
using StyloExtract.Llm.Ollama;

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
        cmd.Add(BuildInduce(rootOpt));
        cmd.Add(BuildRepair(rootOpt));
        cmd.Add(BuildTrain(rootOpt));
        cmd.Add(BuildDumpSkeleton());
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

    internal static string EmitYaml(OperatorTemplate t) => OperatorTemplateYamlEmitter.Emit(t);

    // ----- LLM-driven template induction (phase 3d) -----

    private static Command BuildInduce(Option<string> rootOpt)
    {
        var urlOpt = new Option<string?>("--url") { Description = "URL to fetch and induce a template for." };
        var fileOpt = new Option<string?>("--file") { Description = "Local HTML file to induce against instead of fetching." };
        var hostOpt = new Option<string?>("--host") { Description = "Override host used for the YAML output (default: derived from --url or filename)." };
        var writeOpt = new Option<bool>("--write") { Description = "After inducing, write the YAML to <--root>/<host>.yaml." };
        var ollamaUrlOpt = new Option<string>("--ollama-url")
        {
            Description = "Ollama base URL.",
            DefaultValueFactory = _ => "http://localhost:11434",
        };
        var modelOpt = new Option<string>("--model")
        {
            Description = "Ollama model tag.",
            DefaultValueFactory = _ => "gemma4:e4b-it-qat",
        };

        var c = new Command("induce",
            "Run the LLM template inducer on one page and print (or write) the resulting YAML.");
        c.Options.Add(urlOpt);
        c.Options.Add(fileOpt);
        c.Options.Add(hostOpt);
        c.Options.Add(writeOpt);
        c.Options.Add(ollamaUrlOpt);
        c.Options.Add(modelOpt);
        c.SetAction(async pr =>
        {
            var url = pr.GetValue(urlOpt);
            var file = pr.GetValue(fileOpt);
            if (string.IsNullOrEmpty(url) && string.IsNullOrEmpty(file))
            {
                Console.Error.WriteLine("--url or --file is required");
                return 2;
            }

            var (html, host) = await LoadHtmlAsync(url, file, pr.GetValue(hostOpt));
            var doc = LoadAndCleanDocument(html);
            var skeleton = new DomSkeletonRenderer().Render(doc);
            if (string.IsNullOrEmpty(skeleton))
            {
                Console.Error.WriteLine("page produced an empty skeleton (no body or no candidates)");
                return 1;
            }

            var services = new ServiceCollection();
            services.AddOptions<OllamaTextProviderOptions>().Configure(o =>
            {
                o.OllamaUrl = pr.GetValue(ollamaUrlOpt)!;
                o.Model = pr.GetValue(modelOpt)!;
            });
            services.AddOllamaTextProvider();
            using var sp = services.BuildServiceProvider();
            var provider = sp.GetRequiredService<ILlmTextProvider>();
            var inducer = new LlmTemplateInducer(provider);

            Console.Error.WriteLine($"# inducing template for host={host} via {pr.GetValue(ollamaUrlOpt)} model={pr.GetValue(modelOpt)}");
            var template = await inducer.InduceFromSkeletonAsync(skeleton, host);
            if (template is null)
            {
                Console.Error.WriteLine("induction returned no template (LLM error, malformed response, or validation failure)");
                return 1;
            }

            var yaml = OperatorTemplateYamlEmitter.Emit(template);
            if (pr.GetValue(writeOpt))
            {
                var root = pr.GetValue(rootOpt)!;
                Directory.CreateDirectory(root);
                var path = Path.Combine(root, host + ".yaml");
                await File.WriteAllTextAsync(path, yaml);
                Console.Error.WriteLine($"# wrote {path}");
            }
            Console.Write(yaml);
            return 0;
        });
        return c;
    }

    private static Command BuildRepair(Option<string> rootOpt)
    {
        var urlOpt = new Option<string?>("--url") { Description = "URL to fetch and repair a template against." };
        var fileOpt = new Option<string?>("--file") { Description = "Local HTML file to repair against instead of fetching." };
        var hostOpt = new Option<string?>("--host") { Description = "Override host used for the YAML output (default: derived from --url or filename)." };
        var templateOpt = new Option<string?>("--template")
        {
            Description = "Path to the existing (failing) template YAML to repair. Defaults to <--root>/<host>.yaml.",
        };
        var writeOpt = new Option<bool>("--write") { Description = "After repair, overwrite the template at <--root>/<host>.yaml." };
        var ollamaUrlOpt = new Option<string>("--ollama-url")
        {
            Description = "Ollama base URL.",
            DefaultValueFactory = _ => "http://localhost:11434",
        };
        var modelOpt = new Option<string>("--model")
        {
            Description = "Ollama model tag.",
            DefaultValueFactory = _ => "gemma4:e4b-it-qat",
        };

        var c = new Command("repair",
            "Ask the LLM to repair an existing (failing) template using the current page and the broken YAML.");
        c.Options.Add(urlOpt);
        c.Options.Add(fileOpt);
        c.Options.Add(hostOpt);
        c.Options.Add(templateOpt);
        c.Options.Add(writeOpt);
        c.Options.Add(ollamaUrlOpt);
        c.Options.Add(modelOpt);
        c.SetAction(async pr =>
        {
            var url = pr.GetValue(urlOpt);
            var file = pr.GetValue(fileOpt);
            if (string.IsNullOrEmpty(url) && string.IsNullOrEmpty(file))
            {
                Console.Error.WriteLine("--url or --file is required");
                return 2;
            }

            var (html, host) = await LoadHtmlAsync(url, file, pr.GetValue(hostOpt));
            var doc = LoadAndCleanDocument(html);
            var skeleton = new DomSkeletonRenderer().Render(doc);
            if (string.IsNullOrEmpty(skeleton))
            {
                Console.Error.WriteLine("page produced an empty skeleton (no body or no candidates)");
                return 1;
            }

            // Locate the broken template.
            var templatePath = pr.GetValue(templateOpt) ?? Path.Combine(pr.GetValue(rootOpt)!, host + ".yaml");
            if (!File.Exists(templatePath))
            {
                Console.Error.WriteLine($"existing template not found at {templatePath}");
                Console.Error.WriteLine("hint: pass --template explicitly, or run `template induce --write` first.");
                return 1;
            }
            var existingYaml = await File.ReadAllTextAsync(templatePath);

            var services = new ServiceCollection();
            services.AddOptions<OllamaTextProviderOptions>().Configure(o =>
            {
                o.OllamaUrl = pr.GetValue(ollamaUrlOpt)!;
                o.Model = pr.GetValue(modelOpt)!;
            });
            services.AddOllamaTextProvider();
            using var sp = services.BuildServiceProvider();
            var provider = sp.GetRequiredService<ILlmTextProvider>();
            var inducer = new LlmTemplateInducer(provider);

            Console.Error.WriteLine($"# repairing template for host={host} via {pr.GetValue(ollamaUrlOpt)} model={pr.GetValue(modelOpt)}");
            var template = await inducer.RepairFromSkeletonAsync(skeleton, host, existingYaml);
            if (template is null)
            {
                Console.Error.WriteLine("repair returned no template (LLM error, malformed response, or validation failure)");
                return 1;
            }

            var yaml = OperatorTemplateYamlEmitter.Emit(template);
            if (pr.GetValue(writeOpt))
            {
                await File.WriteAllTextAsync(templatePath, yaml);
                Console.Error.WriteLine($"# wrote {templatePath}");
            }
            Console.Write(yaml);
            return 0;
        });
        return c;
    }

    private static Command BuildTrain(Option<string> rootOpt)
    {
        var urlOpt = new Option<string?>("--url") { Description = "URL to fetch and train against." };
        var fileOpt = new Option<string?>("--file") { Description = "Local HTML file to train against instead of fetching." };
        var hostOpt = new Option<string?>("--host") { Description = "Override host used for the YAML output (default: derived from --url or filename)." };
        var ollamaUrlOpt = new Option<string>("--ollama-url")
        {
            Description = "Ollama base URL.",
            DefaultValueFactory = _ => "http://localhost:11434",
        };
        var modelOpt = new Option<string>("--model")
        {
            Description = "Ollama model tag.",
            DefaultValueFactory = _ => "gemma4:e4b-it-qat",
        };
        var minOutputOpt = new Option<int>("--min-output")
        {
            Description = "Minimum acceptable Markdown length (chars). Below this, train fires the LLM.",
            DefaultValueFactory = _ => 200,
        };
        var verboseOpt = new Option<bool>("--verbose")
        {
            Description = "Print the catalog sent to the LLM and the raw LLM response on stderr.",
        };

        var c = new Command("train",
            "Specialise the template for one page via the LLM, inline. Routes between induce / repair " +
            "based on whether a template already exists. Writes the result to <--root>/<host>.yaml.");
        c.Options.Add(urlOpt);
        c.Options.Add(fileOpt);
        c.Options.Add(hostOpt);
        c.Options.Add(ollamaUrlOpt);
        c.Options.Add(modelOpt);
        c.Options.Add(minOutputOpt);
        c.Options.Add(verboseOpt);
        c.SetAction(async pr =>
        {
            var url = pr.GetValue(urlOpt);
            var file = pr.GetValue(fileOpt);
            if (string.IsNullOrEmpty(url) && string.IsNullOrEmpty(file))
            {
                Console.Error.WriteLine("--url or --file is required");
                return 2;
            }

            var (html, host) = await LoadHtmlAsync(url, file, pr.GetValue(hostOpt));
            var doc = LoadAndCleanDocument(html);
            var skeleton = new DomSkeletonRenderer().Render(doc);
            if (string.IsNullOrEmpty(skeleton))
            {
                Console.Error.WriteLine("page produced an empty skeleton (no body or no candidates)");
                return 1;
            }
            var catalog = new DocumentSelectorCatalog().Render(doc);
            if (pr.GetValue(verboseOpt))
            {
                Console.Error.WriteLine("# === DOM skeleton ===");
                Console.Error.WriteLine(skeleton);
                Console.Error.WriteLine("# === Selector catalog ===");
                Console.Error.WriteLine(catalog);
                Console.Error.WriteLine("# ===");
            }

            // Decide route: if a template file already exists for this host, run repair;
            // otherwise induce from scratch.
            var root = pr.GetValue(rootOpt)!;
            Directory.CreateDirectory(root);
            var templatePath = Path.Combine(root, host + ".yaml");
            var hasExisting = File.Exists(templatePath);

            var services = new ServiceCollection();
            services.AddOptions<OllamaTextProviderOptions>().Configure(o =>
            {
                o.OllamaUrl = pr.GetValue(ollamaUrlOpt)!;
                o.Model = pr.GetValue(modelOpt)!;
                o.Timeout = TimeSpan.FromMinutes(5); // train is operator-driven; let big models finish
            });
            services.AddOllamaTextProvider();
            using var sp = services.BuildServiceProvider();
            var rawProvider = sp.GetRequiredService<ILlmTextProvider>();
            var provider = pr.GetValue(verboseOpt) ? (ILlmTextProvider)new VerboseLlmProvider(rawProvider) : rawProvider;
            var inducer = new LlmTemplateInducer(provider);

            OperatorTemplate? template;
            if (hasExisting)
            {
                var existingYaml = await File.ReadAllTextAsync(templatePath);
                Console.Error.WriteLine($"# training (repair) for host={host} via {pr.GetValue(ollamaUrlOpt)} model={pr.GetValue(modelOpt)}");
                template = await inducer.RepairFromSkeletonAsync(
                    skeleton, host, existingYaml,
                    badMarkdownSample: null,
                    availableSelectors: catalog, document: doc);
            }
            else
            {
                Console.Error.WriteLine($"# training (induce) for host={host} via {pr.GetValue(ollamaUrlOpt)} model={pr.GetValue(modelOpt)}");
                template = await inducer.InduceFromSkeletonAsync(
                    skeleton, host,
                    availableSelectors: catalog, document: doc);
            }

            if (template is null)
            {
                Console.Error.WriteLine($"# {(hasExisting ? "repair" : "induce")} returned no template " +
                                        "(LLM error, malformed response, or validation failure)");
                return 1;
            }

            var yaml = OperatorTemplateYamlEmitter.Emit(template);
            await File.WriteAllTextAsync(templatePath, yaml);
            Console.Error.WriteLine($"# wrote {templatePath} ({template.Rules.Count} rule(s))");
            Console.Write(yaml);
            return 0;
        });
        return c;
    }

    private static Command BuildDumpSkeleton()
    {
        var urlOpt = new Option<string?>("--url") { Description = "URL to fetch and skeletonise." };
        var fileOpt = new Option<string?>("--file") { Description = "Local HTML file to skeletonise instead." };

        var c = new Command("dump-skeleton",
            "Dump the slim DOM skeleton the LLM inducer would see for one page.");
        c.Options.Add(urlOpt);
        c.Options.Add(fileOpt);
        c.SetAction(async pr =>
        {
            var url = pr.GetValue(urlOpt);
            var file = pr.GetValue(fileOpt);
            if (string.IsNullOrEmpty(url) && string.IsNullOrEmpty(file))
            {
                Console.Error.WriteLine("--url or --file is required");
                return 2;
            }
            var (html, _) = await LoadHtmlAsync(url, file, null);
            var doc = LoadAndCleanDocument(html);
            var skeleton = new DomSkeletonRenderer().Render(doc);
            Console.Write(skeleton);
            return 0;
        });
        return c;
    }

    private static async Task<(string Html, string Host)> LoadHtmlAsync(string? url, string? file, string? hostOverride)
    {
        if (!string.IsNullOrEmpty(url))
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 stylo-extract/template-induce");
            var html = await http.GetStringAsync(url);
            var host = hostOverride ?? new Uri(url).Host;
            return (html, host);
        }
        var content = await File.ReadAllTextAsync(file!);
        return (content, hostOverride ?? "file:" + Path.GetFileNameWithoutExtension(file));
    }

    private sealed class VerboseLlmProvider : ILlmTextProvider
    {
        private readonly ILlmTextProvider _inner;
        public VerboseLlmProvider(ILlmTextProvider inner) => _inner = inner;
        public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        {
            Console.Error.WriteLine("# === User prompt sent to LLM ===");
            Console.Error.WriteLine(userPrompt);
            Console.Error.WriteLine($"# === Awaiting LLM response (system={systemPrompt.Length}ch, user={userPrompt.Length}ch) ===");
            try
            {
                var response = await _inner.CompleteAsync(systemPrompt, userPrompt, cancellationToken);
                Console.Error.WriteLine("# === Raw LLM response ===");
                Console.Error.WriteLine(response);
                Console.Error.WriteLine("# === end response ===");
                return response;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"# === LLM call threw {ex.GetType().Name}: {ex.Message} ===");
                throw;
            }
        }
    }

    private static AngleSharp.Dom.IDocument LoadAndCleanDocument(string html)
    {
        var doc = new AngleSharpHtmlDomParser().Parse(html);
        new DomCleaner().Clean(doc);
        return doc;
    }
}
