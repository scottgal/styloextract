using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Core.OperatorTemplates;
using Xunit;

namespace StyloExtract.Core.Tests;

public class YamlFileOperatorTemplateStoreTests : IDisposable
{
    private readonly string _root;

    public YamlFileOperatorTemplateStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "styloextract-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* ignore */ }
    }

    private void Write(string fileName, string yaml)
        => File.WriteAllText(Path.Combine(_root, fileName), yaml);

    [Fact]
    public void Loads_All_Yaml_Files_From_Root_On_Construction()
    {
        Write("example.com.yaml", """
            host: example.com
            rules:
              - role: MainContent
                selectors:
                  - main
            """);
        Write("other.example.yaml", """
            host: other.example
            rules:
              - role: MainContent
                selectors:
                  - article
            """);

        using var store = new YamlFileOperatorTemplateStore(_root, watch: false);
        store.List().Should().HaveCount(2);
        store.TryGet("example.com", out var a).Should().BeTrue();
        a.Rules[0].Selectors[0].Should().Be("main");
        store.TryGet("other.example", out var b).Should().BeTrue();
        b.Rules[0].Selectors[0].Should().Be("article");
    }

    [Fact]
    public void TryGet_Is_Case_Insensitive_Over_The_Host_Key()
    {
        Write("example.com.yaml", """
            host: example.com
            rules:
              - role: MainContent
                selectors:
                  - main
            """);
        using var store = new YamlFileOperatorTemplateStore(_root, watch: false);
        store.TryGet("EXAMPLE.COM", out var t).Should().BeTrue();
        t.Host.Should().Be("example.com");
    }

    [Fact]
    public void TryGet_Returns_False_For_Unknown_Host()
    {
        using var store = new YamlFileOperatorTemplateStore(_root, watch: false);
        store.TryGet("missing.example", out _).Should().BeFalse();
    }

    [Fact]
    public void Reload_Picks_Up_Newly_Written_Files()
    {
        using var store = new YamlFileOperatorTemplateStore(_root, watch: false);
        store.List().Should().BeEmpty();

        Write("late.example.yaml", """
            host: late.example
            rules:
              - role: MainContent
                selectors:
                  - main
            """);
        store.Reload();
        store.TryGet("late.example", out _).Should().BeTrue();
    }

    [Fact]
    public void Parse_Failure_Keeps_Prior_Entry_Alive()
    {
        const string good = """
            host: example.com
            rules:
              - role: MainContent
                selectors:
                  - main.good
            """;
        Write("example.com.yaml", good);
        using var store = new YamlFileOperatorTemplateStore(_root, watch: false);
        store.TryGet("example.com", out var initial).Should().BeTrue();
        initial.Rules[0].Selectors[0].Should().Be("main.good");

        // Now break the file with an invalid schema (unknown role) and reload.
        Write("example.com.yaml", """
            host: example.com
            rules:
              - role: NotARealRole
                selectors:
                  - whatever
            """);
        store.Reload();

        // Prior in-memory entry is preserved; the bad edit doesn't evict it.
        store.TryGet("example.com", out var afterBroken).Should().BeTrue();
        afterBroken.Rules[0].Selectors[0].Should().Be("main.good");
    }

    [Fact]
    public void List_Returns_Templates_In_Host_Alphabetical_Order()
    {
        Write("z.example.yaml", """
            host: z.example
            rules:
              - role: MainContent
                selectors:
                  - main
            """);
        Write("a.example.yaml", """
            host: a.example
            rules:
              - role: MainContent
                selectors:
                  - main
            """);
        Write("m.example.yaml", """
            host: m.example
            rules:
              - role: MainContent
                selectors:
                  - main
            """);
        using var store = new YamlFileOperatorTemplateStore(_root, watch: false);
        store.List().Select(t => t.Host).Should().Equal("a.example", "m.example", "z.example");
    }
}
