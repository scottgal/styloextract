namespace StyloExtract.Heuristics;

public static class CssSelectorGeneralizer
{
    public static string Generalize(string xpath)
    {
        // Strip [nth] indices, convert / to ' > ', lowercase.
        var parts = xpath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var clean = parts.Select(p =>
        {
            var bracket = p.IndexOf('[');
            return bracket > 0 ? p[..bracket] : p;
        });
        return string.Join(" > ", clean).ToLowerInvariant();
    }
}
