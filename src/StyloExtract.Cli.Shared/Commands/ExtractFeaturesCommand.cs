using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StyloExtract.Html;
using StyloExtract.Ml.Features;

namespace StyloExtract.Cli.Shared.Commands;

/// <summary>
/// Dumps the 45-dimensional feature vector for every element in an input HTML
/// file. One JSON Lines record per element: <c>{xpath, tag, text_len,
/// features: [45 floats]}</c>. Consumed by the out-of-band training pipeline
/// in <c>training/wcxb_to_features.py</c>; keeping extraction in C# eliminates
/// any drift between training-time features and inference-time features.
/// </summary>
public static class ExtractFeaturesCommand
{
    public static Command Build()
    {
        var pathArg = new Argument<string>("path") { Description = "HTML file (or - for stdin)." };
        var minTextOpt = new Option<int>("--min-text")
        {
            Description = "Skip elements whose TextContent length is below this. Default 0 emits every element.",
            DefaultValueFactory = _ => 0,
        };
        var prettyOpt = new Option<bool>("--pretty") { Description = "Pretty-print each JSON record (default is JSONL, one record per line)." };

        var cmd = new Command("extract-features", "Dump per-element feature vectors as JSON Lines.");
        cmd.Add(pathArg);
        cmd.Options.Add(minTextOpt);
        cmd.Options.Add(prettyOpt);

        cmd.SetAction(async (ParseResult pr) =>
        {
            var path = pr.GetValue(pathArg)!;
            var minText = pr.GetValue(minTextOpt);
            var pretty = pr.GetValue(prettyOpt);

            string html;
            if (path == "-")
            {
                using var stdin = Console.OpenStandardInput();
                using var sr = new StreamReader(stdin, Encoding.UTF8);
                html = await sr.ReadToEndAsync();
            }
            else
            {
                html = await File.ReadAllTextAsync(path);
            }

            var parser = new AngleSharpHtmlDomParser();
            var doc = parser.Parse(html);
            if (doc.Body is null) return 0;

            var extractor = new ElementFeatureExtractor();
            var buf = new float[FeatureNames.Dim];
            var opts = new JsonSerializerOptions { WriteIndented = pretty };

            foreach (var el in doc.Body.QuerySelectorAll("*"))
            {
                var textLen = el.TextContent.Length;
                if (textLen < minText) continue;
                extractor.Extract(el, buf);
                // Whitespace-normalised text excerpt for the training-time
                // label projection. Cap at 4 KB per element; the projection
                // does substring containment over the gold main_content blob
                // and doesn't need more than a few hundred chars to disambiguate.
                var rawText = el.TextContent;
                var excerpt = NormaliseExcerpt(rawText, maxLen: 4096);
                var record = new FeatureRecord
                {
                    Xpath = XPathOf(el),
                    Tag = el.LocalName,
                    TextLen = textLen,
                    TextExcerpt = excerpt,
                    Features = buf.ToArray(),
                };
                var json = JsonSerializer.Serialize(record, ExtractFeaturesJsonContext.Default.FeatureRecord);
                Console.WriteLine(json);
            }
            return 0;
        });
        return cmd;
    }

    private static string NormaliseExcerpt(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(Math.Min(text.Length, maxLen));
        bool prevWs = true;
        foreach (var c in text)
        {
            bool isWs = c == ' ' || c == '\t' || c == '\n' || c == '\r';
            if (isWs)
            {
                if (!prevWs && sb.Length > 0)
                {
                    sb.Append(' ');
                    prevWs = true;
                }
                continue;
            }
            // Lowercase fold for case-insensitive substring matching at projection time.
            sb.Append(c is >= 'A' and <= 'Z' ? (char)(c + 32) : c);
            prevWs = false;
            if (sb.Length >= maxLen) break;
        }
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.ToString();
    }

    private static string XPathOf(AngleSharp.Dom.IElement el)
    {
        // Simple positional XPath: /html/body/div[1]/main/article[2]/p[3] ...
        // Used as a stable element identifier between extract-features and the
        // training-time label projection. AngleSharp's XPath module isn't pulled
        // in, but the shape is trivial to roll by hand.
        var parts = new List<string>();
        var cur = el;
        while (cur is not null && cur.ParentElement is not null)
        {
            int index = 1;
            var sib = cur.PreviousElementSibling;
            while (sib is not null)
            {
                if (sib.LocalName == cur.LocalName) index++;
                sib = sib.PreviousElementSibling;
            }
            parts.Add($"{cur.LocalName}[{index}]");
            cur = cur.ParentElement;
        }
        parts.Reverse();
        return "/" + string.Join("/", parts);
    }
}

public sealed record FeatureRecord
{
    [JsonPropertyName("xpath")] public string Xpath { get; init; } = "";
    [JsonPropertyName("tag")] public string Tag { get; init; } = "";
    [JsonPropertyName("text_len")] public int TextLen { get; init; }
    [JsonPropertyName("text")] public string TextExcerpt { get; init; } = "";
    [JsonPropertyName("features")] public float[] Features { get; init; } = Array.Empty<float>();
}

[JsonSerializable(typeof(FeatureRecord))]
internal partial class ExtractFeaturesJsonContext : JsonSerializerContext { }
