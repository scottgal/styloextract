using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using StyloExtract.AspNetCore.Tests.TestWebApp;
using Xunit;

namespace StyloExtract.AspNetCore.Tests;

/// <summary>
/// Validates NegotiateMarkdownAttribute behaviour via MVC controllers, without global middleware.
/// </summary>
public sealed class NegotiateMarkdownAttributeTests : IDisposable
{
    private readonly MarkdownAttributeFactory _factory;
    private readonly HttpClient _client;

    public NegotiateMarkdownAttributeTests()
    {
        _factory = new MarkdownAttributeFactory();
        _client = _factory.Client;
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Get_WithHtmlAccept_AttributeEndpoint_ReturnsHtml()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/html-attribute");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
    }

    [Fact]
    public async Task Get_WithMarkdownAccept_AttributeEndpoint_ReturnsMarkdown()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/html-attribute");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Get_WithMarkdownAccept_AttributeEndpoint_HasVaryHeader()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/html-attribute");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));

        var response = await _client.SendAsync(request);

        response.Headers.Vary.Should().Contain("Accept");
    }

    [Fact]
    public async Task Get_WithMarkdownAccept_NoAttribute_ReturnsHtml()
    {
        // No [NegotiateMarkdown] on this action: HTML is returned as-is.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/plain-html");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
    }

    [Fact]
    public async Task Get_WithMarkdownAccept_ProfileAttribute_ReturnsMarkdown()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/html-attribute-profile");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");
    }
}
