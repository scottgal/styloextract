using StyloExtract.AspNetCore.Markdown;

namespace StyloExtract.AspNetCore.Tests;

/// <summary>
/// Thin wrapper that exposes the internal <see cref="AcceptHeaderParser"/> to tests.
/// </summary>
internal static class AcceptHeaderParserAccessor
{
    public static double GetQuality(string? acceptHeader, string mediaType) =>
        AcceptHeaderParser.GetQuality(acceptHeader, mediaType);

    public static bool Prefers(string? acceptHeader, string preferredType, string fallbackType) =>
        AcceptHeaderParser.Prefers(acceptHeader, preferredType, fallbackType);
}
