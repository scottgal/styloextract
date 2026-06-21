namespace StyloExtract.AspNetCore.Markdown;

/// <summary>
/// Parses RFC 7231 Accept header values and computes effective q-values for MIME types.
/// </summary>
internal static class AcceptHeaderParser
{
    /// <summary>
    /// Returns the effective q-value (0.0..1.0) of <paramref name="mediaType"/> given the Accept header.
    /// Returns 0.0 when the type is explicitly rejected (q=0) or absent and no wildcard covers it.
    /// Returns 1.0 when the Accept header is null or empty (no preference expressed).
    /// </summary>
    public static double GetQuality(string? acceptHeader, string mediaType)
    {
        if (string.IsNullOrWhiteSpace(acceptHeader))
        {
            // No Accept header: all types acceptable at q=1.
            return 1.0;
        }

        mediaType = mediaType.Trim().ToLowerInvariant();

        var parts = acceptHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        double bestQ = -1.0; // -1 means "not matched yet"
        int bestSpecificity = -1; // higher = more specific match wins

        foreach (var part in parts)
        {
            var (token, q) = ParseToken(part);
            if (token is null)
                continue;

            var specificity = GetSpecificity(token);
            if (!Matches(token, mediaType))
                continue;

            // Use most-specific and highest-q among matching entries.
            if (specificity > bestSpecificity || (specificity == bestSpecificity && q > bestQ))
            {
                bestSpecificity = specificity;
                bestQ = q;
            }
        }

        // No wildcard or explicit match found: treat as not acceptable.
        return bestQ < 0.0 ? 0.0 : bestQ;
    }

    /// <summary>
    /// Returns true when <paramref name="preferredType"/> has a strictly higher q-value
    /// than <paramref name="fallbackType"/> in the Accept header.
    /// </summary>
    public static bool Prefers(string? acceptHeader, string preferredType, string fallbackType)
    {
        var preferred = GetQuality(acceptHeader, preferredType);
        var fallback = GetQuality(acceptHeader, fallbackType);
        return preferred > fallback;
    }

    // ------------------------------------------------------------------ internals

    private static (string? token, double q) ParseToken(ReadOnlySpan<char> segment)
    {
        // Segment looks like: "text/markdown" or "text/markdown;q=0.9" or "text/*;q=0.5"
        // Split on ';' to separate the media-range from parameters.
        double q = 1.0;
        string? token = null;

        var semicolon = segment.IndexOf(';');
        if (semicolon < 0)
        {
            token = segment.Trim().ToString().ToLowerInvariant();
        }
        else
        {
            token = segment[..semicolon].Trim().ToString().ToLowerInvariant();
            var parameters = segment[(semicolon + 1)..];
            foreach (var param in parameters.ToString().Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = param.IndexOf('=', StringComparison.Ordinal);
                if (eq < 0)
                    continue;

                var name = param[..eq].Trim();
                var value = param[(eq + 1)..].Trim();

                if (name.Equals("q", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                {
                    q = Math.Clamp(parsed, 0.0, 1.0);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(token))
            return (null, 0.0);

        return (token, q);
    }

    // Returns 2 for exact match, 1 for subtype-wildcard (text/*), 0 for full wildcard (*/*).
    private static int GetSpecificity(string token) =>
        token switch
        {
            "*/*" => 0,
            _ when token.EndsWith("/*", StringComparison.Ordinal) => 1,
            _ => 2
        };

    private static bool Matches(string pattern, string mediaType)
    {
        if (pattern == "*/*")
            return true;

        if (pattern.EndsWith("/*", StringComparison.Ordinal))
        {
            // e.g. "text/*" matches "text/markdown", "text/html"
            var patternType = pattern[..^2]; // strip "/*"
            var slashIdx = mediaType.IndexOf('/', StringComparison.Ordinal);
            if (slashIdx < 0)
                return false;
            var mediaMainType = mediaType[..slashIdx];
            return patternType.Equals(mediaMainType, StringComparison.OrdinalIgnoreCase);
        }

        return pattern.Equals(mediaType, StringComparison.OrdinalIgnoreCase);
    }
}
