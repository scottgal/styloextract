using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
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

    private sealed record TestResponseDto(string status, int blockCount, string markdown);
    private sealed record SummaryDto(string host, string description, int version, int ruleCount);
}
