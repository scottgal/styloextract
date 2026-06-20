namespace StyloExtract.Markdown;

internal static class MarkdownEscaper
{
    public static string Escape(string input)
    {
        return input
            .Replace("\\", "\\\\")
            .Replace("*", "\\*")
            .Replace("_", "\\_")
            .Replace("`", "\\`");
    }
}
