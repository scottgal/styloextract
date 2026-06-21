namespace StyloExtract.Abstractions;

public interface IRenderedHtmlFetcher
{
    Task<RenderedHtmlResult> FetchAsync(Uri uri, RenderOptions? options = null, CancellationToken cancellationToken = default);
}
