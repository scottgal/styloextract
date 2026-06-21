using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StyloExtract.Abstractions;

namespace StyloExtract.AspNetCore.Markdown;

public static class MinimalApiMarkdownExtensions
{
    /// <summary>
    /// Adds an endpoint filter that intercepts HTML results and converts them to Markdown
    /// when the client sends <c>Accept: text/markdown</c>. Equivalent to the
    /// <see cref="NegotiateMarkdownAttribute"/> for Minimal API routes.
    /// </summary>
    public static RouteHandlerBuilder WithMarkdownNegotiation(
        this RouteHandlerBuilder builder,
        ExtractionProfile? profile = null)
    {
        return builder.AddEndpointFilter(new MarkdownNegotiationEndpointFilter(profile));
    }
}

/// <summary>
/// Endpoint filter that rewrites HTML responses to Markdown on <c>Accept: text/markdown</c>.
/// When markdown is accepted, the result is intercepted before it executes and rendered as markdown instead.
/// </summary>
internal sealed class MarkdownNegotiationEndpointFilter : IEndpointFilter
{
    private readonly ExtractionProfile? _profile;

    public MarkdownNegotiationEndpointFilter(ExtractionProfile? profile)
    {
        _profile = profile;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var accept = ctx.HttpContext.Request.Headers.Accept.ToString();

        if (AcceptHeaderParser.GetQuality(accept, "text/markdown") <= 0.0)
            return await next(ctx);

        var sp = ctx.HttpContext.RequestServices;
        var extractor = sp.GetRequiredService<ILayoutExtractor>();
        var opts = sp.GetRequiredService<IOptions<MarkdownNegotiationOptions>>().Value;
        var logger = sp.GetService<ILogger<MarkdownNegotiationEndpointFilter>>();

        var profile = _profile ?? opts.DefaultProfile;
        var sourceUri = new Uri(ctx.HttpContext.Request.GetDisplayUrl());

        // Get the result from the handler without executing it yet.
        var result = await next(ctx);

        // Only intercept IResult that produce HTML.
        if (result is IResult htmlResult)
        {
            // Execute into a buffer so we can inspect the content type and body.
            using var buffer = new MemoryStream();
            var savedBody = ctx.HttpContext.Response.Body;
            ctx.HttpContext.Response.Body = buffer;

            try
            {
                await htmlResult.ExecuteAsync(ctx.HttpContext);
            }
            finally
            {
                ctx.HttpContext.Response.Body = savedBody;
            }

            var capturedContentType = ctx.HttpContext.Response.ContentType ?? string.Empty;

            if (capturedContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) && buffer.Length > 0)
            {
                buffer.Seek(0, SeekOrigin.Begin);
                var html = new StreamReader(buffer, Encoding.UTF8).ReadToEnd();

                try
                {
                    var extracted = await extractor.ExtractAsync(
                        html, sourceUri, new ExtractionOptions { Profile = profile },
                        ctx.HttpContext.RequestAborted);

                    if (opts.EmitVaryHeader)
                        ctx.HttpContext.Response.Headers.Vary = "Accept";

                    return new RawMarkdownResult(extracted.Markdown, opts.EmitVaryHeader);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "WithMarkdownNegotiation: extraction failed; returning original HTML.");
                }
            }

            // Not HTML or extraction failed: replay the buffered bytes as-is.
            buffer.Seek(0, SeekOrigin.Begin);
            return new BufferedBytesResult(buffer.ToArray(), capturedContentType);
        }

        return result;
    }
}

/// <summary>Writes pre-rendered Markdown bytes as the HTTP response.</summary>
internal sealed class RawMarkdownResult : IResult
{
    private readonly string _markdown;
    private readonly bool _emitVary;

    public RawMarkdownResult(string markdown, bool emitVary)
    {
        _markdown = markdown;
        _emitVary = emitVary;
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var bytes = Encoding.UTF8.GetBytes(_markdown);
        httpContext.Response.ContentType = "text/markdown; charset=utf-8";
        httpContext.Response.ContentLength = bytes.Length;
        if (_emitVary)
            httpContext.Response.Headers.Vary = "Accept";
        await httpContext.Response.Body.WriteAsync(bytes, httpContext.RequestAborted);
    }
}

/// <summary>Replays a pre-captured response body with its original content type.</summary>
internal sealed class BufferedBytesResult : IResult
{
    private readonly byte[] _bytes;
    private readonly string _contentType;

    public BufferedBytesResult(byte[] bytes, string contentType)
    {
        _bytes = bytes;
        _contentType = contentType;
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        if (!string.IsNullOrEmpty(_contentType))
            httpContext.Response.ContentType = _contentType;
        httpContext.Response.ContentLength = _bytes.Length;
        await httpContext.Response.Body.WriteAsync(_bytes, httpContext.RequestAborted);
    }
}

/// <summary>
/// Factory for IResult instances that serve HTML to standard clients and Markdown to
/// clients that send <c>Accept: text/markdown</c>.
/// </summary>
public static class StyloExtractResults
{
    /// <summary>
    /// Inspects the current request's Accept header. Returns a Markdown result when
    /// <c>text/markdown</c> is preferred; returns an HTML result otherwise.
    /// Extraction is performed via the registered <see cref="ILayoutExtractor"/>.
    /// </summary>
    public static IResult HtmlOrMarkdown(
        string html,
        Uri? sourceUri = null,
        ExtractionProfile? profile = null)
    {
        return new HtmlOrMarkdownResult(html, sourceUri, profile);
    }
}

internal sealed class HtmlOrMarkdownResult : IResult
{
    private readonly string _html;
    private readonly Uri? _sourceUri;
    private readonly ExtractionProfile? _profile;

    public HtmlOrMarkdownResult(string html, Uri? sourceUri, ExtractionProfile? profile)
    {
        _html = html;
        _sourceUri = sourceUri;
        _profile = profile;
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var accept = httpContext.Request.Headers.Accept.ToString();

        if (AcceptHeaderParser.GetQuality(accept, "text/markdown") <= 0.0)
        {
            await Results.Content(_html, "text/html; charset=utf-8").ExecuteAsync(httpContext);
            return;
        }

        var sp = httpContext.RequestServices;
        var extractor = sp.GetRequiredService<ILayoutExtractor>();
        var opts = sp.GetRequiredService<IOptions<MarkdownNegotiationOptions>>().Value;
        var logger = sp.GetService<ILogger<HtmlOrMarkdownResult>>();

        var profile = _profile ?? opts.DefaultProfile;
        var sourceUri = _sourceUri ?? new Uri(httpContext.Request.GetDisplayUrl());

        try
        {
            var result = await extractor.ExtractAsync(
                _html,
                sourceUri,
                new ExtractionOptions { Profile = profile },
                httpContext.RequestAborted);

            var markdownBytes = Encoding.UTF8.GetBytes(result.Markdown);

            if (opts.EmitVaryHeader)
                httpContext.Response.Headers.Vary = "Accept";

            httpContext.Response.ContentType = "text/markdown; charset=utf-8";
            httpContext.Response.ContentLength = markdownBytes.Length;
            await httpContext.Response.Body.WriteAsync(markdownBytes, httpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "HtmlOrMarkdown: extraction failed; returning original HTML.");
            await Results.Content(_html, "text/html; charset=utf-8").ExecuteAsync(httpContext);
        }
    }
}
