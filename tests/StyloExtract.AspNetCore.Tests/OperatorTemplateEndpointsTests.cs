using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using IPAddress = System.Net.IPAddress;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StyloExtract.AspNetCore;
using Xunit;

namespace StyloExtract.AspNetCore.Tests;

/// <summary>
/// Spins a minimal in-process test server that wires the operator-template
/// REST surface over a temp directory, then exercises every endpoint
/// end-to-end.
/// </summary>
public sealed class OperatorTemplateEndpointsTests : IDisposable
{
    private readonly string _root;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public OperatorTemplateEndpointsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "styloextract-rest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddStyloExtract(o => o.StorePath = ":memory:");
                    services.AddStyloExtractOperatorTemplates(_root);
                    services.AddRouting();
                    // Wire the LLM stack with a stub provider so /induce works
                    // in tests without needing a live Ollama.
                    services.AddSingleton<StyloExtract.Abstractions.ILlmTextProvider>(_ => new StubInducerLlm());
                    services.AddStyloExtractLlmInducer(_root);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapOperatorTemplateEndpoints(_root);
                    });
                });
            })
            .Start();
        _client = _host.GetTestClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task List_Is_Empty_On_A_Fresh_Root()
    {
        var resp = await _client.GetAsync("/api/styloextract/templates/");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Be("[]");
    }

    [Fact]
    public async Task Put_Then_Get_Roundtrips_Yaml_Through_The_Parser()
    {
        const string yaml = """
            host: rest.example
            description: rest test fixture
            rules:
              - role: MainContent
                selectors:
                  - main.docs-body
                confidence: 0.95
            """;
        var put = await _client.PutAsync(
            "/api/styloextract/templates/rest.example",
            new StringContent(yaml, Encoding.UTF8, "text/yaml"));
        put.StatusCode.Should().Be(HttpStatusCode.Created);

        var get = await _client.GetAsync("/api/styloextract/templates/rest.example");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await get.Content.ReadAsStringAsync();
        body.Should().Contain("host: rest.example");
        body.Should().Contain("- role: MainContent");
        body.Should().Contain("- main.docs-body");
        body.Should().Contain("confidence: 0.95");
    }

    [Fact]
    public async Task Put_Mismatched_Host_Returns_400()
    {
        const string yaml = """
            host: wrong.example
            rules:
              - role: MainContent
                selectors:
                  - main
            """;
        var put = await _client.PutAsync(
            "/api/styloextract/templates/different.example",
            new StringContent(yaml, Encoding.UTF8, "text/yaml"));
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_Invalid_Yaml_Returns_400_With_Error()
    {
        const string yaml = """
            host: bad.example
            rules:
              - role: NotARealRole
                selectors:
                  - main
            """;
        var put = await _client.PutAsync(
            "/api/styloextract/templates/bad.example",
            new StringContent(yaml, Encoding.UTF8, "text/yaml"));
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await put.Content.ReadAsStringAsync();
        body.Should().Contain("unknown role");
    }

    [Fact]
    public async Task Delete_Removes_The_File()
    {
        const string yaml = """
            host: tomb.example
            rules:
              - role: MainContent
                selectors:
                  - main
            """;
        await _client.PutAsync(
            "/api/styloextract/templates/tomb.example",
            new StringContent(yaml, Encoding.UTF8, "text/yaml"));

        var del = await _client.DeleteAsync("/api/styloextract/templates/tomb.example");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var get = await _client.GetAsync("/api/styloextract/templates/tomb.example");
        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_Unknown_Host_Returns_404()
    {
        var del = await _client.DeleteAsync("/api/styloextract/templates/ghost.example");
        del.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Test_Endpoint_Runs_Extraction_Against_Inline_Html()
    {
        const string yaml = """
            host: test.example
            rules:
              - role: MainContent
                selectors:
                  - main.docs-body
                confidence: 0.95
            """;
        await _client.PutAsync(
            "/api/styloextract/templates/test.example",
            new StringContent(yaml, Encoding.UTF8, "text/yaml"));

        const string html = """
            <html><body>
              <header><nav><a href="/">Home</a></nav></header>
              <main class="docs-body">
                <h1>Override target heading</h1>
                <p>The body content the operator's selector should pull out and the inline link <a href="/x">stays.</a> Long enough to clear the renderer's quality gate.</p>
                <p>A second paragraph keeping the article above the textual mass gate.</p>
              </main>
              <footer>copyright</footer>
            </body></html>
            """;
        var resp = await _client.PostAsJsonAsync(
            "/api/styloextract/templates/test.example/test",
            new { html });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<TestResponseDto>();
        body!.status.Should().Be("OperatorOverride");
        body.blockCount.Should().BeGreaterThan(0);
        body.markdown.Should().Contain("# Override target heading");
    }

    // ----- Security regression tests -----

    [Theory]
    [InlineData("..%2fetc%2fpasswd")]
    [InlineData("..")]
    [InlineData("..%2f..%2fetc%2fpasswd")]
    [InlineData("foo%2fbar")]
    [InlineData("foo%5cbar")]
    [InlineData("a..b")]
    [InlineData("FOO.com")]      // mixed case — strict hostname is lowercase
    [InlineData("space domain")]
    public async Task Put_Rejects_Path_Traversal_And_Invalid_Host(string maliciousHost)
    {
        const string yaml = """
            host: foo.example
            rules:
              - role: MainContent
                selectors:
                  - main
            """;
        var resp = await _client.PutAsync(
            "/api/styloextract/templates/" + maliciousHost,
            new StringContent(yaml, Encoding.UTF8, "text/yaml"));
        // Must be a 4xx, never a 2xx — and the file system MUST NOT have grown
        // a file at the resolved bad path.
        ((int)resp.StatusCode).Should().BeInRange(400, 499);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("..%2fetc%2fpasswd")]
    [InlineData("foo%2fbar")]
    [InlineData("FOO.com")]
    public async Task Delete_Rejects_Path_Traversal_And_Invalid_Host(string maliciousHost)
    {
        var resp = await _client.DeleteAsync("/api/styloextract/templates/" + maliciousHost);
        ((int)resp.StatusCode).Should().BeInRange(400, 499);
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://localhost/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://172.17.0.1/")]              // Docker bridge
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://169.254.169.254/latest/")]  // AWS / GCP metadata
    [InlineData("http://0.0.0.0/")]
    [InlineData("ftp://example.com/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://example.com/")]
    [InlineData("http://[::1]/")]
    public async Task Test_Endpoint_Rejects_Non_Public_Or_Non_Http_Urls(string url)
    {
        // No template configured for this host; if SSRF guard fails first, we
        // get a 400 before the extractor runs.
        var resp = await _client.PostAsJsonAsync(
            "/api/styloextract/templates/test.example/test",
            new { url });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Test_Endpoint_Rejects_Numeric_Host_That_Resolves_To_Private_Space()
    {
        // Decimal-form IP: 2130706433 == 127.0.0.1. Past SSRF defences have
        // missed this. Our validation runs on the parsed URI's HostNameType
        // which the framework decodes to its canonical form first, so this
        // either fails URI parsing or fails the IP check.
        var resp = await _client.PostAsJsonAsync(
            "/api/styloextract/templates/test.example/test",
            new { url = "http://2130706433/" });
        ((int)resp.StatusCode).Should().BeInRange(400, 499);
    }

    [Fact]
    public void IsPublicUnicast_Accepts_Public_IPv4_Addresses()
    {
        // Sanity check the allowlist isn't broken by overly aggressive denylist.
        OperatorTemplateEndpoints.IsPublicUnicast(IPAddress.Parse("8.8.8.8")).Should().BeTrue();
        OperatorTemplateEndpoints.IsPublicUnicast(IPAddress.Parse("142.250.72.46")).Should().BeTrue();
        OperatorTemplateEndpoints.IsPublicUnicast(IPAddress.Parse("1.1.1.1")).Should().BeTrue();
    }

    [Fact]
    public void IsValidHostname_Accepts_Real_Hostnames_And_Rejects_Garbage()
    {
        OperatorTemplateEndpoints.IsValidHostname("example.com").Should().BeTrue();
        OperatorTemplateEndpoints.IsValidHostname("docs.example.com").Should().BeTrue();
        OperatorTemplateEndpoints.IsValidHostname("a-b-c.example.io").Should().BeTrue();
        OperatorTemplateEndpoints.IsValidHostname("").Should().BeFalse();
        OperatorTemplateEndpoints.IsValidHostname("..").Should().BeFalse();
        OperatorTemplateEndpoints.IsValidHostname("foo/bar").Should().BeFalse();
        OperatorTemplateEndpoints.IsValidHostname("foo\\bar").Should().BeFalse();
        OperatorTemplateEndpoints.IsValidHostname("foo.com.").Should().BeFalse(); // trailing dot
        OperatorTemplateEndpoints.IsValidHostname(new string('a', 254)).Should().BeFalse();
    }

    [Fact]
    public async Task List_Returns_Summary_Entries_After_Upsert()
    {
        const string yaml = """
            host: list.example
            description: a template
            rules:
              - role: MainContent
                selectors:
                  - main
            """;
        await _client.PutAsync(
            "/api/styloextract/templates/list.example",
            new StringContent(yaml, Encoding.UTF8, "text/yaml"));

        var resp = await _client.GetFromJsonAsync<List<SummaryDto>>("/api/styloextract/templates/");
        resp.Should().NotBeNull();
        resp!.Should().Contain(s => s.host == "list.example" && s.ruleCount == 1);
    }

    [Fact]
    public async Task Induce_Endpoint_Returns_Yaml_From_Llm_Stub()
    {
        const string html = """
            <html><body>
              <header><nav><a href="/">Home</a></nav></header>
              <main class="acme-content"><h1>Heading</h1><p>Body content with enough text for the renderer to pick it up.</p></main>
              <footer>©</footer>
            </body></html>
            """;
        var resp = await _client.PostAsJsonAsync(
            "/api/styloextract/templates/induce.example/induce",
            new { html });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/yaml");
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("host: induce.example");
        body.Should().Contain("- role: MainContent");
        body.Should().Contain("main.acme-content");
    }

    [Fact]
    public async Task Induce_Endpoint_Validates_Hostname()
    {
        var resp = await _client.PostAsJsonAsync(
            "/api/styloextract/templates/..%2fetc%2fpasswd/induce",
            new { html = "<html><body><main>x</main></body></html>" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Induce_Endpoint_Requires_Body_With_Html_Or_Url()
    {
        var resp = await _client.PostAsJsonAsync(
            "/api/styloextract/templates/induce.example/induce",
            new { });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed class StubInducerLlm : StyloExtract.Abstractions.ILlmTextProvider
    {
        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        {
            // The stub mirrors a well-behaved Gemma response: a fenced YAML
            // block matching the operator-template schema. The skeleton
            // appears in the user prompt and we extract the .acme-content
            // class from it so the assertion against `main.acme-content`
            // succeeds without hard-coding host strings.
            var host = ExtractHostFromPrompt(userPrompt) ?? "induced.example";
            var yaml = $$"""
                ```yaml
                host: {{host}}
                rules:
                  - role: MainContent
                    selectors:
                      - main.acme-content
                    confidence: 0.95
                ```
                """;
            return Task.FromResult(yaml);
        }

        private static string? ExtractHostFromPrompt(string userPrompt)
        {
            const string marker = "Host: ";
            var idx = userPrompt.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;
            var rest = userPrompt[(idx + marker.Length)..];
            var newline = rest.IndexOf('\n');
            return newline < 0 ? rest.Trim() : rest[..newline].Trim();
        }
    }

    private sealed record TestResponseDto(string status, int blockCount, string markdown);
    private sealed record SummaryDto(string host, string description, int version, int ruleCount);
}
