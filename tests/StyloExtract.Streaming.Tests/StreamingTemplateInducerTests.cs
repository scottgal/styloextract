using System.Text;
using FluentAssertions;
using Xunit;

namespace StyloExtract.Streaming.Tests;

public sealed class StreamingTemplateInducerTests
{
    [Fact]
    public void Returns_null_for_plain_text_with_no_tags()
    {
        var inducer = new StreamingTemplateInducer();

        var result = inducer.Induce("plain.example", "this is just plain text with no html"u8);

        // AngleSharp will wrap text in <html><body>, but no header/article/p-cluster.
        result.Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_empty_input()
    {
        var inducer = new StreamingTemplateInducer();

        var result = inducer.Induce("empty.example", ReadOnlySpan<byte>.Empty);

        result.Should().BeNull();
    }

    [Fact]
    public void Returns_null_for_empty_host()
    {
        var inducer = new StreamingTemplateInducer();

        var html = "<body><header>x</header><p>a</p><p>b</p><footer></footer></body>"u8;
        var result = inducer.Induce("", html);

        result.Should().BeNull();
    }

    [Fact]
    public void Induces_a_template_with_correct_host_field_set()
    {
        var inducer = new StreamingTemplateInducer();
        var html = Encoding.UTF8.GetBytes(
            "<body><header>hi</header><nav>n</nav><article><p>one</p><p>two</p></article><footer>end</footer></body>");

        var template = inducer.Induce("www.example.com", html);

        template.Should().NotBeNull();
        template!.Host.Should().Be("www.example.com");
        template.TemplateId.Should().NotBe(Guid.Empty);
        template.BailoutBytes.Should().Be(5_000_000);
        // Tripwire shape: tag-only claim covers the basic case.
        template.PrefixTripwire.Tag.Should().Be("header");
        template.ContentStartTripwire.Tag.Should().Be("article");
    }

    [Fact]
    public async Task Induced_template_drives_subsequent_scan_to_Captured_on_same_bytes()
    {
        var inducer = new StreamingTemplateInducer();
        var html = Encoding.UTF8.GetBytes(
            "<html><body>" +
            "<header><nav><a>logo</a><a>about</a></nav></header>" +
            "<main>" +
            "<article>" +
            "<h1>Title</h1>" +
            "<p>First paragraph.</p>" +
            "<p>Second paragraph.</p>" +
            "<p>Third paragraph.</p>" +
            "<p>Fourth paragraph.</p>" +
            "</article>" +
            "</main>" +
            "<footer><p>(c) 2026</p></footer>" +
            "</body></html>");

        var template = inducer.Induce("close-loop.example", html);
        template.Should().NotBeNull();

        var store = new InMemoryStreamingTemplateStore();
        await store.UpsertAsync(template!);

        var selector = new StreamingPathSelector(store);
        var verdict = selector.ScanByHost("close-loop.example", html);

        verdict.Should().Be(ScanVerdict.Captured);
    }

    [Fact]
    public void Returns_summary_describing_chosen_markers()
    {
        var inducer = new StreamingTemplateInducer();
        var html = Encoding.UTF8.GetBytes(
            "<body><header>x</header><article><p>a</p><p>b</p></article><footer></footer></body>");

        var summary = inducer.Describe(html);

        summary.Should().NotBeNull();
        summary!.Value.PrefixMarker.Should().StartWith("header");
        summary.Value.ContentStartMarker.Should().StartWith("article");
    }

    [Fact]
    public void Falls_back_to_nav_prefix_when_header_missing()
    {
        var inducer = new StreamingTemplateInducer();
        var html = Encoding.UTF8.GetBytes(
            "<body><nav><a>x</a></nav><article><p>a</p><p>b</p></article><footer></footer></body>");

        var summary = inducer.Describe(html);

        summary.Should().NotBeNull();
        summary!.Value.PrefixMarker.Should().StartWith("nav");
    }

    [Fact]
    public async Task Induces_a_template_from_the_real_mostlylucid_home_fixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "mostlylucid-home.html");
        File.Exists(path).Should().BeTrue("mostlylucid fixture should be copied to output");
        var html = File.ReadAllBytes(path);

        var inducer = new StreamingTemplateInducer();
        var template = inducer.Induce("www.mostlylucid.net", html);

        template.Should().NotBeNull();
        template!.Host.Should().Be("www.mostlylucid.net");

        var summary = inducer.Describe(html);
        summary.Should().NotBeNull();

        var store = new InMemoryStreamingTemplateStore();
        await store.UpsertAsync(template);
        var selector = new StreamingPathSelector(store);

        var verdict = selector.ScanByHost("www.mostlylucid.net", html);
        verdict.Should().NotBe(ScanVerdict.NoTemplate);
    }
}
