using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StyloExtract.Abstractions;

namespace StyloExtract.AspNetCore.Markdown;

/// <summary>
/// Per-action or per-controller result filter that converts an HTML response to Markdown
/// when the client sends <c>Accept: text/markdown</c>. Works without the global middleware.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class NegotiateMarkdownAttribute : Attribute, IAsyncResultFilter
{
    private ExtractionProfile? _profile;

    /// <summary>
    /// Default constructor: uses the profile from <see cref="MarkdownNegotiationOptions.DefaultProfile"/>.
    /// </summary>
    public NegotiateMarkdownAttribute() { }

    /// <summary>
    /// Constructor that pins a specific extraction profile for this endpoint.
    /// </summary>
    public NegotiateMarkdownAttribute(ExtractionProfile profile)
    {
        _profile = profile;
    }

    /// <summary>
    /// Override the extraction profile for this endpoint.
    /// When not set (or left as the default), the profile from
    /// <see cref="MarkdownNegotiationOptions.DefaultProfile"/> is used.
    /// </summary>
    public ExtractionProfile Profile
    {
        get => _profile ?? ExtractionProfile.RagFull;
        set => _profile = value;
    }

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var accept = context.HttpContext.Request.Headers.Accept.ToString();

        if (AcceptHeaderParser.GetQuality(accept, "text/markdown") <= 0.0)
        {
            await next();
            return;
        }

        var sp = context.HttpContext.RequestServices;
        var extractor = sp.GetRequiredService<ILayoutExtractor>();
        var opts = sp.GetRequiredService<IOptions<MarkdownNegotiationOptions>>().Value;
        var logger = sp.GetService<ILogger<NegotiateMarkdownAttribute>>();

        var originalBody = context.HttpContext.Response.Body;
        var buffer = new MemoryStream();
        context.HttpContext.Response.Body = buffer;

        try
        {
            await next();

            var contentType = context.HttpContext.Response.ContentType ?? string.Empty;
            var bufferLen = buffer.Length;

            if (contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
                && bufferLen > 0
                && bufferLen <= opts.MaxBodyBytes)
            {
                buffer.Seek(0, SeekOrigin.Begin);
                var encoding = DetectEncoding(contentType);
                var html = await new StreamReader(buffer, encoding).ReadToEndAsync(context.HttpContext.RequestAborted);

                var profile = _profile ?? opts.DefaultProfile;
                var sourceUri = new Uri(context.HttpContext.Request.GetDisplayUrl());

                try
                {
                    var result = await extractor.ExtractAsync(
                        html,
                        sourceUri,
                        new ExtractionOptions { Profile = profile },
                        context.HttpContext.RequestAborted);

                    var markdownBytes = Encoding.UTF8.GetBytes(result.Markdown);

                    context.HttpContext.Response.ContentType = "text/markdown; charset=utf-8";
                    context.HttpContext.Response.ContentLength = markdownBytes.Length;

                    if (opts.EmitVaryHeader)
                        context.HttpContext.Response.Headers.Vary = "Accept";

                    await originalBody.WriteAsync(markdownBytes, context.HttpContext.RequestAborted);
                    return;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "NegotiateMarkdown: extraction failed; returning original HTML.");
                }
            }

            buffer.Seek(0, SeekOrigin.Begin);
            await buffer.CopyToAsync(originalBody, context.HttpContext.RequestAborted);
        }
        finally
        {
            context.HttpContext.Response.Body = originalBody;
            await buffer.DisposeAsync();
        }
    }

    private static Encoding DetectEncoding(string contentType)
    {
        var parts = contentType.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (!part.StartsWith("charset", StringComparison.OrdinalIgnoreCase))
                continue;

            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
                continue;

            var charsetName = part[(eq + 1)..].Trim().Trim('"');
            try
            {
                return Encoding.GetEncoding(charsetName);
            }
            catch (ArgumentException)
            {
                // Unknown charset; fall through to UTF-8.
            }
        }

        return Encoding.UTF8;
    }
}
