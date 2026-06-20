using System.Reflection;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace StyloExtract.Heuristics;

public sealed class ClassNoiseFilter
{
    private readonly HashSet<string> _exact;
    private readonly string[] _prefixes;
    private readonly Regex _hashedBemSuffix;

    private ClassNoiseFilter(HashSet<string> exact, string[] prefixes, Regex hashedBemSuffix)
    {
        _exact = exact;
        _prefixes = prefixes;
        _hashedBemSuffix = hashedBemSuffix;
    }

    public static ClassNoiseFilter LoadFromEmbeddedResource()
    {
        var assembly = typeof(ClassNoiseFilter).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("class-noise-tokens.yaml", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var yaml = reader.ReadToEnd();
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        var dto = deserializer.Deserialize<ClassNoiseDto>(yaml);
        return new ClassNoiseFilter(
            new HashSet<string>(dto.ExactTokens, StringComparer.OrdinalIgnoreCase),
            dto.Prefixes,
            new Regex(dto.HashedBemSuffixPattern, RegexOptions.Compiled));
    }

    public IReadOnlyList<string> Filter(IReadOnlyList<string> rawClassTokens)
    {
        var result = new List<string>(rawClassTokens.Count);
        foreach (var token in rawClassTokens)
        {
            if (_exact.Contains(token)) continue;
            if (_prefixes.Any(p => token.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            var stripped = _hashedBemSuffix.Replace(token, string.Empty);
            if (!string.IsNullOrEmpty(stripped))
            {
                result.Add(stripped);
            }
        }
        return result;
    }

    private sealed class ClassNoiseDto
    {
        public List<string> ExactTokens { get; set; } = new();
        public string[] Prefixes { get; set; } = [];
        public string HashedBemSuffixPattern { get; set; } = "";
    }
}
