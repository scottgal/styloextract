using FluentAssertions;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Core;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using StyloExtract.Markdown;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.IntegrationTests;

public class ExportImportRoundtripTests
{
    [Fact]
    public async Task Export_Import_PreservesMatch()
    {
        var (e1, conn1) = BuildExtractor();
        try
        {
            var html = await File.ReadAllTextAsync("Fixtures/example/article.html");
            var uri = new Uri("https://example.com/post");

            await e1.ExtractAsync(html, uri); // Novel → registers

            // Export host
            var host = new HostHasher(new byte[32]).Hash("example.com");
            using var ms = new MemoryStream();
            await TemplateExporter.ExportHostAsync(conn1, host, "example.com", ms, default);
            ms.Position = 0;

            // Import into a fresh DB-backed extractor
            var (e2, conn2) = BuildExtractor();
            try
            {
                var importResult = await TemplateImporter.ImportAsync(conn2, host, ms, default);
                importResult.ImportedCount.Should().Be(1);

                var second = await e2.ExtractAsync(html, uri);
                second.Match.Status.Should().BeOneOf(MatchStatus.FastPathHit, MatchStatus.SlowPathMatch);
            }
            finally { conn2.Dispose(); }
        }
        finally { conn1.Dispose(); }
    }

    private static (ILayoutExtractor, SqliteConnection) BuildExtractor()
        => LayoutExtractorTestBuilder.Build();
}
