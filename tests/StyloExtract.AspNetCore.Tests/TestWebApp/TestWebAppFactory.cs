using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StyloExtract.AspNetCore;
using StyloExtract.AspNetCore.Markdown;

namespace StyloExtract.AspNetCore.Tests.TestWebApp;

/// <summary>
/// Creates an in-process test server with the negotiation middleware registered globally.
/// </summary>
public sealed class MarkdownMiddlewareFactory : IDisposable
{
    private readonly IHost _host;
    public HttpClient Client { get; }

    public MarkdownMiddlewareFactory()
    {
        _host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddStyloExtract(o => o.StorePath = ":memory:");
                    services.AddStyloExtractMarkdownNegotiation();
                    services.AddControllers();
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseStyloExtractMarkdownNegotiation();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/html", () => Results.Content(SampleHtml, "text/html"));
                        endpoints.MapGet("/json", () => Results.Json(new { hello = "world" }));
                        endpoints.MapGet("/notfound", context =>
                        {
                            context.Response.StatusCode = 404;
                            context.Response.ContentType = "text/html";
                            return context.Response.WriteAsync(SampleHtml);
                        });
                        endpoints.MapControllers();
                    });
                });
            })
            .Build();

        _host.Start();
        Client = _host.GetTestClient();
    }

    public void Dispose()
    {
        Client.Dispose();
        _host.Dispose();
    }

    internal const string SampleHtml =
        """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Test Article</title></head>
        <body>
          <header><nav><a href="/">Home</a></nav></header>
          <main>
            <article>
              <h1>Hello World</h1>
              <p>This is the primary test content paragraph used by the negotiation suite.</p>
              <p>A second paragraph with more detail so the extractor has enough signal to produce markdown.</p>
              <ul><li>Item one</li><li>Item two</li><li>Item three</li></ul>
            </article>
          </main>
          <footer>Footer text</footer>
        </body>
        </html>
        """;
}

/// <summary>
/// Creates an in-process test server using the <see cref="NegotiateMarkdownAttribute"/> (no middleware).
/// </summary>
public sealed class MarkdownAttributeFactory : IDisposable
{
    private readonly IHost _host;
    public HttpClient Client { get; }

    public MarkdownAttributeFactory()
    {
        _host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddStyloExtract(o => o.StorePath = ":memory:");
                    services.AddStyloExtractMarkdownNegotiation();
                    services.AddControllers();
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    // No UseStyloExtractMarkdownNegotiation: only the attribute is active.
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .Build();

        _host.Start();
        Client = _host.GetTestClient();
    }

    public void Dispose()
    {
        Client.Dispose();
        _host.Dispose();
    }
}

/// <summary>
/// Creates an in-process test server using Minimal API + WithMarkdownNegotiation.
/// </summary>
public sealed class MarkdownMinimalApiFactory : IDisposable
{
    private readonly IHost _host;
    public HttpClient Client { get; }

    public MarkdownMinimalApiFactory()
    {
        _host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddStyloExtract(o => o.StorePath = ":memory:");
                    services.AddStyloExtractMarkdownNegotiation();
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/html", () => Results.Content(MarkdownMiddlewareFactory.SampleHtml, "text/html"))
                            .WithMarkdownNegotiation();

                        endpoints.MapGet("/htmlormarkdown", () =>
                            StyloExtractResults.HtmlOrMarkdown(MarkdownMiddlewareFactory.SampleHtml));
                    });
                });
            })
            .Build();

        _host.Start();
        Client = _host.GetTestClient();
    }

    public void Dispose()
    {
        Client.Dispose();
        _host.Dispose();
    }
}
