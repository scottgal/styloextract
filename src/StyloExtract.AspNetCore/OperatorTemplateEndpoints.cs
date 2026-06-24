using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
            if (!IsValidHostname(host))
                return Results.BadRequest(new { error = "host must be a valid hostname" });
            if (!TryResolveContainedPath(root, host, out var path, out var pathError))
                return Results.BadRequest(new { error = pathError });

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
            await File.WriteAllTextAsync(path, EmitYaml(parsed));
            // Force the file-watcher to re-scan immediately rather than waiting on
            // the OS event (which can lag on network mounts or some Linux distros).
            (store as YamlFileOperatorTemplateStore)?.Reload();
            return Results.Created(basePath + "/" + host, new { host = parsed.Host, rules = parsed.Rules.Count });
        });

        group.MapDelete("/{host}", (string host, IOperatorTemplateStore store) =>
        {
            if (!IsValidHostname(host))
                return Results.BadRequest(new { error = "host must be a valid hostname" });
            if (!TryResolveContainedPath(root, host, out var path, out var pathError))
                return Results.BadRequest(new { error = pathError });
            if (!File.Exists(path))
                return Results.NotFound(new { error = $"no template for {host}" });
            File.Delete(path);
            (store as YamlFileOperatorTemplateStore)?.Reload();
            return Results.NoContent();
        });

        group.MapPost("/{host}/test", async (string host, TestRequest body, IOperatorTemplateStore store, ILayoutExtractor extractor) =>
        {
            if (!IsValidHostname(host))
                return Results.BadRequest(new { error = "host must be a valid hostname" });

            string html;
            Uri? sourceUri = null;
            if (!string.IsNullOrEmpty(body.Html))
            {
                html = body.Html;
                if (!string.IsNullOrEmpty(body.Url) && Uri.TryCreate(body.Url, UriKind.Absolute, out var u)) sourceUri = u;
            }
            else if (!string.IsNullOrEmpty(body.Url))
            {
                var validation = await TryResolvePublicUrlAsync(body.Url);
                if (validation.Error is not null)
                    return Results.BadRequest(new { error = validation.Error });
                using var handler = new HttpClientHandler { AllowAutoRedirect = false };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("stylo-extract/operator-template-test");
                using var resp = await http.GetAsync(validation.Uri!);
                if ((int)resp.StatusCode >= 300 && (int)resp.StatusCode < 400)
                {
                    return Results.BadRequest(new
                    {
                        error = "redirects are not followed by the test endpoint; re-issue with the resolved URL"
                    });
                }
                resp.EnsureSuccessStatusCode();
                html = await resp.Content.ReadAsStringAsync();
                sourceUri = validation.Uri;
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

    // ------------------- Security helpers -------------------

    // Strict RFC1123-ish hostname pattern: labels of [a-z0-9] (with optional internal
    // hyphens), separated by dots. Lowercase only — we don't allow Unicode IDNs here
    // because punycode hostnames going through the filesystem would need a separate
    // round of validation. Operators with IDN hosts can use the puny-encoded form.
    private static readonly Regex HostnamePattern = new(
        @"^[a-z0-9]([a-z0-9\-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9\-]*[a-z0-9])?)*$",
        RegexOptions.Compiled);

    internal static bool IsValidHostname(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        if (host.Length > 253) return false;
        // Defend against directory-separator or dotdot abuse in addition to the regex.
        if (host.IndexOfAny(InvalidHostChars) >= 0) return false;
        if (host.Contains("..", StringComparison.Ordinal)) return false;
        return HostnamePattern.IsMatch(host);
    }
    private static readonly char[] InvalidHostChars = { '/', '\\', ':', '\0' };

    // Resolve the candidate template path and assert containment inside `root`.
    // Defends against any host value that survives IsValidHostname but still
    // somehow produces a path outside root (case-insensitive filesystems,
    // symbolic links, etc.). Both branches that touch disk MUST call this.
    internal static bool TryResolveContainedPath(string root, string host, out string path, out string? error)
    {
        var canonicalRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(canonicalRoot, host + ".yaml"));
        var expectedPrefix = canonicalRoot.EndsWith(Path.DirectorySeparatorChar)
            ? canonicalRoot
            : canonicalRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            path = "";
            error = "host resolves outside the template root";
            return false;
        }
        path = candidate;
        error = null;
        return true;
    }

    internal readonly record struct UrlValidation(Uri? Uri, string? Error);

    // SSRF guard for the /test endpoint. Requires http/https, resolves the hostname
    // to IP addresses, and rejects any non-public destination: loopback (127/8, ::1),
    // RFC1918 private space (10/8, 172.16/12, 192.168/16, fc00::/7), link-local
    // (169.254/16, fe80::/10), wildcard (0.0.0.0/8, ::), and IPv4 multicast (224/4).
    // Also rejects bare-IP URLs that target any of the above ranges directly.
    internal static async Task<UrlValidation> TryResolvePublicUrlAsync(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return new(null, "url must be an absolute http(s) URL");
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return new(null, "url must use http or https scheme");

        IPAddress[] addresses;
        try
        {
            // HostNameType=IPv4/IPv6 means the URL embeds a literal IP; treat that
            // as a single-element address list. Otherwise resolve via DNS.
            if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            {
                addresses = new[] { IPAddress.Parse(uri.Host) };
            }
            else
            {
                addresses = await Dns.GetHostAddressesAsync(uri.Host);
            }
        }
        catch (SocketException)
        {
            return new(null, "url host did not resolve");
        }
        if (addresses.Length == 0) return new(null, "url host did not resolve");

        foreach (var addr in addresses)
        {
            if (!IsPublicUnicast(addr))
                return new(null, $"url resolves to a non-public address ({addr})");
        }
        return new(uri, null);
    }

    internal static bool IsPublicUnicast(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            // 0.0.0.0/8 (wildcard / "this host")
            if (b[0] == 0) return false;
            // 10.0.0.0/8
            if (b[0] == 10) return false;
            // 172.16.0.0/12
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
            // 192.168.0.0/16
            if (b[0] == 192 && b[1] == 168) return false;
            // 169.254.0.0/16 link-local + AWS/GCP metadata
            if (b[0] == 169 && b[1] == 254) return false;
            // 127.0.0.0/8 catch-all (IPAddress.IsLoopback covers most of this)
            if (b[0] == 127) return false;
            // 100.64.0.0/10 CGNAT
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;
            // 224.0.0.0/4 multicast (and everything above)
            if (b[0] >= 224) return false;
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal) return false;
            if (address.IsIPv6SiteLocal) return false;
            if (address.IsIPv6Multicast) return false;
            // fc00::/7 unique local addresses
            var b = address.GetAddressBytes();
            if ((b[0] & 0xfe) == 0xfc) return false;
            // IPv4-mapped IPv6: recurse on the inner v4.
            if (address.IsIPv4MappedToIPv6) return IsPublicUnicast(address.MapToIPv4());
            return true;
        }

        // Unknown address family — refuse to fetch.
        return false;
    }

    private static string EmitYaml(OperatorTemplate t) => OperatorTemplateYamlEmitter.Emit(t);
}

public sealed record OperatorTemplateSummary(string Host, string Description, int Version, int RuleCount);
public sealed record TestRequest(string? Html = null, string? Url = null);
public sealed record TestResponse(string Status, int BlockCount, string Markdown);

[JsonSerializable(typeof(List<OperatorTemplateSummary>))]
[JsonSerializable(typeof(TestRequest))]
[JsonSerializable(typeof(TestResponse))]
internal partial class OperatorTemplateJsonContext : JsonSerializerContext { }
