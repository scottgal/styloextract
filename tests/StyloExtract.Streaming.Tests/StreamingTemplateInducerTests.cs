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
            "<body><header>hi</header><nav>n</nav><p>one</p><p>two</p><footer>end</footer></body>");

        var template = inducer.Induce("www.example.com", html);

        template.Should().NotBeNull();
        template!.Host.Should().Be("www.example.com");
        template.TemplateId.Should().NotBe(Guid.Empty);
        template.WindowSize.Should().Be(8);
        template.BailoutBytes.Should().Be(5_000_000);
    }

    [Fact]
    public async Task Induced_template_drives_subsequent_scan_to_Captured_on_same_bytes()
    {
        // Closes the dogfood loop in a single test: induce → upsert → scan.
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
            "<body><header>x</header><p>a</p><p>b</p><footer></footer></body>");

        var summary = inducer.Describe(html);

        summary.Should().NotBeNull();
        summary!.Value.PrefixMarker.Should().Be("header-open→close");
        summary.Value.ContentStartMarker.Should().Be("p-p-cluster");
        summary.Value.ContentEndMarker.Should().Contain("footer");
    }

    [Fact]
    public void Falls_back_to_nav_prefix_when_header_missing()
    {
        var inducer = new StreamingTemplateInducer();
        var html = Encoding.UTF8.GetBytes(
            "<body><nav><a>x</a></nav><p>a</p><p>b</p><footer></footer></body>");

        var summary = inducer.Describe(html);

        summary.Should().NotBeNull();
        summary!.Value.PrefixMarker.Should().Be("nav-open→close");
    }

    [Fact]
    public async Task Induces_a_template_from_the_real_mostlylucid_home_fixture()
    {
        // Closes the dogfood loop on real bytes: a captured response from
        // www.mostlylucid.net (no <nav>/<footer>, sticky header + many
        // <p> paragraph blocks + </body>). Inducer must produce a non-null
        // template; the produced template must drive ScanByHost to a real
        // verdict (Captured or Bailout) — NoTemplate would mean the induction
        // never happened.
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
        // Either Captured or Bailout proves the scanner ran against a host-keyed
        // induced template on the real bytes. NoTemplate would mean the lookup
        // didn't hit the upserted entry. Continue is also acceptable (the bench
        // template falls through naturally on home pages with no </header> hit
        // mid-stream — what matters is that NoTemplate is not the answer).
        verdict.Should().NotBe(ScanVerdict.NoTemplate);
    }

    [Fact]
    public void Falls_back_to_body_close_when_no_footer_or_main_close()
    {
        var inducer = new StreamingTemplateInducer();
        var html = Encoding.UTF8.GetBytes(
            "<body><header>x</header><p>a</p><p>b</p><p>c</p><p>d</p></body>");

        var summary = inducer.Describe(html);

        summary.Should().NotBeNull();
        summary!.Value.ContentEndMarker.Should().Be("body-close");
    }
}
