using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Core.OperatorTemplates;
using Xunit;

namespace StyloExtract.Core.Tests;

/// <summary>
/// Pins the deterministic-vs-hand-authored distinction in
/// <see cref="YamlFileOperatorTemplateStore"/>. Before this fix the store
/// loaded both <c>&lt;host&gt;.yaml</c> and <c>&lt;host&gt;-deterministic.yaml</c>
/// under the same host key with no way to tell them apart, so the
/// TemplateEnrichmentCoordinator's "an operator template already exists,
/// skip the LLM induce" gate would silently skip whenever the deterministic
/// sink had written a file — which it does on every novel host. Net effect:
/// the LLM never ran on dogfood hosts. With <see cref="OperatorTemplate.IsDeterministic"/>
/// the coordinator can ignore deterministic entries.
/// </summary>
public class DeterministicYamlOriginTests : IDisposable
{
    private readonly string _root;

    public DeterministicYamlOriginTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "stylo-determorigin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private const string YamlPayload = """
        host: example.com
        rules:
          - role: MainContent
            selectors:
              - main
        """;

    [Fact]
    public void Deterministic_Yaml_Loads_With_IsDeterministic_True()
    {
        File.WriteAllText(Path.Combine(_root, "example.com-deterministic.yaml"), YamlPayload);

        using var store = new YamlFileOperatorTemplateStore(_root, watch: false);
        store.TryGet("example.com", out var t).Should().BeTrue();
        t.IsDeterministic.Should().BeTrue(
            "templates loaded from a `-deterministic.yaml` file must surface the IsDeterministic flag");
    }

    [Fact]
    public void Hand_Authored_Yaml_Loads_With_IsDeterministic_False()
    {
        File.WriteAllText(Path.Combine(_root, "example.com.yaml"), YamlPayload);

        using var store = new YamlFileOperatorTemplateStore(_root, watch: false);
        store.TryGet("example.com", out var t).Should().BeTrue();
        t.IsDeterministic.Should().BeFalse(
            "templates loaded from a bare `<host>.yaml` file are hand-authored / LLM-induced overrides");
    }

    [Fact]
    public void When_Both_Files_Exist_HandAuthored_Wins_The_Host_Slot()
    {
        // The TemplateEnrichmentCoordinator's Induce-skip gate now reads
        // OperatorTemplate.IsDeterministic to decide whether to skip. For
        // that gate to correctly KEEP skipping when a hand-authored override
        // coexists with the deterministic snapshot, TryGet must return the
        // hand-authored entry (IsDeterministic=false), not the deterministic
        // one. The store's load pass ensures the hand-authored entry takes
        // the host slot.
        File.WriteAllText(Path.Combine(_root, "example.com-deterministic.yaml"), YamlPayload);
        File.WriteAllText(Path.Combine(_root, "example.com.yaml"), YamlPayload);

        using var store = new YamlFileOperatorTemplateStore(_root, watch: false);
        store.TryGet("example.com", out var t).Should().BeTrue();
        t.IsDeterministic.Should().BeFalse(
            "the hand-authored entry takes the host slot when both file kinds exist");
    }
}