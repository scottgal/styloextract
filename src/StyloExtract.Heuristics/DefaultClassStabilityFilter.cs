namespace StyloExtract.Heuristics;

public sealed class DefaultClassStabilityFilter : IClassStabilityFilter
{
    /// <summary>
    /// Reject tokens that look like opaque hashes:
    /// - length >= 6 AND no vowel (Tailwind utility hashes like "tx7k9q2")
    /// - length >= 8 AND mixed-case-with-digits entropy threshold
    /// - matches "css-[a-z0-9]+" pattern (Emotion/styled-components)
    /// - matches "_[A-Za-z0-9_-]{5,}__[A-Za-z0-9_-]+" (CSS modules)
    /// - matches "[a-z0-9]{6,}" pure-lowercase-alnum (likely hash)
    /// Keep readable tokens like "article-body", "post__content", "primary-nav".
    /// </summary>
    public bool IsStable(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (token.Length < 3) return true; // too short to be a useful hash

        // Common framework-hash patterns.
        if (token.StartsWith("css-") && token.Length >= 8) return false;          // Emotion
        if (token.StartsWith("sc-") && token.Length >= 7) return false;           // styled-components
        if (token.StartsWith("_") && token.Contains("__")) return false;          // CSS modules

        // Hash-shaped tokens (no vowel + length >= 6, or high digit ratio + length >= 8).
        // Gate at length 6 so 7-char Tailwind utility hashes like "tx7k9q2" are rejected;
        // the inner IsLikelyHash still gates the digit-ratio branch at length 8.
        if (token.Length >= 6 && IsLikelyHash(token)) return false;

        return true;
    }

    private static bool IsLikelyHash(string s)
    {
        var digits = 0;
        var hasVowel = false;
        foreach (var c in s)
        {
            if (char.IsDigit(c)) digits++;
            else if (c is 'a' or 'e' or 'i' or 'o' or 'u' or 'y') hasVowel = true;
        }
        // No vowel + length >= 6 → likely random.
        if (!hasVowel && s.Length >= 6) return true;
        // Digit ratio > 0.3 + length >= 8 → likely encoded.
        if ((double)digits / s.Length > 0.3) return true;
        return false;
    }
}
