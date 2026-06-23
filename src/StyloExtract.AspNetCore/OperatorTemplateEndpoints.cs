using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using StyloExtract.Abstractions;
using StyloExtract.Core.OperatorTemplates;

namespace StyloExtract.AspNetCore;

/// <summary>
/// HTTP surface for operator-template management. Mirrors the CLI surface
/// (<c>stylo-extract template ...</c>) for tooling and dashboards that
/// prefer HTTP. Off by default: opt in via <see cref="MapOperatorTemplateEndpoints"/>.
///
/// <para>Endpoints (all under the configured base path, default <c>/api/styloextract/templates</c>):</para>
/// <list type="bullet">
///   <item><c>GET /</c>            list every loaded template (Host, Description, rule count).</item>
///   <item><c>GET /{host}</c>      return the YAML for one host (text/yaml).</item>
///   <item><c>PUT /{host}</c>      upsert: body is YAML, parsed-and-validated then written to disk.</item>
///   <item><c>DELETE /{host}</c>   delete the on-disk YAML file for the host.</item>
///   <item><c>POST /{host}/test</c> run extraction with the operator template; body is {html, url?}.</item>
///   <item><c>POST /reload</c>     force a re-scan of the root directory.</item>
/// </list>
///
/// <para>Auth is the host app's concern. Operators typically pair this with
/// <c>RequireAuthorization(...)</c> or an admin scope on the route group.</para>
/// </summary>
public static class OperatorTemplateEndpoints
{
    /// <summary>
    /// Map operator-template endpoints under <paramref name="basePath"/>.
    /// The store must already be registered via
    /// <see cref="StyloExtractOperatorTemplatesExtensions.AddStyloExtractOperatorTemplates"/>,
    /// and the disk root passed in <paramref name="root"/> must match the
    /// root the store is watching (the endpoints are the writer; the store
    /// is the reader).
    /// </summary>
    [RequiresUnreferencedCode("Minimal-API endpoint delegate binding uses reflection on parameter types. Use AOT-friendly request types or annotate the host app with [DynamicDependency].")]
    [RequiresDynamicCode("Minimal-API endpoint delegate binding emits runtime code for body deserialisation. Set RequiresDynamicCode-aware JSON contexts before calling.")]
    public static IEndpointRouteBuilder MapOperatorTemplateEndpoints(
        this IEndpointRouteBuilder app,
        string root,
        string basePath = "/api/styloextract/templates")
    {
        var group = app.MapGroup(basePath);

        group.MapGet("/", (IOperatorTemplateStore store) =>
        {
            var entries = store.List().Select(t => new OperatorTemplateSummary(
                Host: t.Host,
                Description: t.Description,
                Version: t.Version,
                RuleCount: t.Rules.Count)).ToList();
            return Results.Json(entries, OperatorTemplateJsonContext.Default.ListOperatorTemplateSummary);
        });

        group.MapGet("/{host}", (string host, IOperatorTemplateStore store) =>
        {
            if (!store.TryGet(host, out var t))
                return Results.NotFound(new { error = $"no template for {host}" });
            // Round-trip through the emitter so what callers see is always the
            // canonical post-parse YAML (selectors quoted/escaped consistently).
            return Results.Text(EmitYaml(t), "text/yaml; charset=utf-8");
        });

        group.MapPut("/{host}", async (string host, HttpRequest req, IOperatorTemplateStore store) =>
        {
            string yaml;
            using (var reader = new StreamReader(req.Body, Encoding.UTF8))
                yaml = await reader.ReadToEndAsync();
            OperatorTemplate parsed;
            try { parsed = YamlOperatorTemplateLoader.Parse(yaml); }
            catch (OperatorTemplateParseException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            if (!string.Equals(parsed.Host, host, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"YAML 'host: {parsed.Host}' must match URL host '{host}'" });

            Directory.CreateDirectory(root);
            var path = Path.Combine(root, host + ".yaml");
            await File.WriteAllTextAsync(path, EmitYaml(parsed));
            // Force the file-watcher to re-scan immediately rather than waiting on
            // the OS event (which can lag on network mounts or some Linux distros).
            (store as YamlFileOperatorTemplateStore)?.Reload();
            return Results.Created(basePath + "/" + host, new { host = parsed.Host, rules = parsed.Rules.Count });
        });

        group.MapDelete("/{host}", (string host, IOperatorTemplateStore store) =>
        {
            var path = Path.Combine(root, host + ".yaml");
            if (!File.Exists(path))
                return Results.NotFound(new { error = $"no template for {host}" });
            File.Delete(path);
            (store as YamlFileOperatorTemplateStore)?.Reload();
            return Results.NoContent();
        });

        group.MapPost("/{host}/test", async (string host, TestRequest body, IOperatorTemplateStore store, ILayoutExtractor extractor) =>
        {
            string html;
            Uri? sourceUri = null;
            if (!string.IsNullOrEmpty(body.Html))
            {
                html = body.Html;
                if (!string.IsNullOrEmpty(body.Url) && Uri.TryCreate(body.Url, UriKind.Absolute, out var u)) sourceUri = u;
            }
            else if (!string.IsNullOrEmpty(body.Url))
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("stylo-extract/operator-template-test");
                html = await http.GetStringAsync(body.Url);
                sourceUri = new Uri(body.Url);
            }
            else
            {
                return Results.BadRequest(new { error = "body must include 'html' or 'url'" });
            }

            var result = await extractor.ExtractAsync(html, sourceUri, new ExtractionOptions
            {
                HostOverride = host,
            });
            return Results.Json(new TestResponse(
                Status: result.Match.Status.ToString(),
                BlockCount: result.Blocks.Count,
                Markdown: result.Markdown), OperatorTemplateJsonContext.Default.TestResponse);
        });

        group.MapPost("/reload", (IOperatorTemplateStore store) =>
        {
            (store as YamlFileOperatorTemplateStore)?.Reload();
            return Results.NoContent();
        });

        return app;
    }

    private static string EmitYaml(OperatorTemplate t)
    {
        // Mirrors the tiny emitter on TemplateCommand. Kept private here so the
        // AspNetCore package doesn't take a project reference on Cli.Shared.
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
                sb.Append("      - ").Append(sel).Append('\n');
            if (rule.Confidence != 1.0)
                sb.Append("    confidence: ")
                    .Append(rule.Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append('\n');
        }
        return sb.ToString();
    }
}

public sealed record OperatorTemplateSummary(string Host, string Description, int Version, int RuleCount);
public sealed record TestRequest(string? Html = null, string? Url = null);
public sealed record TestResponse(string Status, int BlockCount, string Markdown);

[JsonSerializable(typeof(List<OperatorTemplateSummary>))]
[JsonSerializable(typeof(TestRequest))]
[JsonSerializable(typeof(TestResponse))]
internal partial class OperatorTemplateJsonContext : JsonSerializerContext { }
