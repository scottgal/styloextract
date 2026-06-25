using Microsoft.Extensions.DependencyInjection;

namespace StyloExtract.AspNetCore;

/// <summary>
/// Backward-compatible forwarding stubs for AspNetCore consumers.
/// The implementation has moved to <c>StyloExtract.Core.StyloExtractOperatorTemplatesExtensions</c>.
/// </summary>
public static class StyloExtractOperatorTemplatesExtensions
{
    /// <inheritdoc cref="StyloExtract.Core.StyloExtractOperatorTemplatesExtensions.AddStyloExtractOperatorTemplates"/>
    public static IServiceCollection AddStyloExtractOperatorTemplates(
        this IServiceCollection services,
        string root)
        => StyloExtract.Core.StyloExtractOperatorTemplatesExtensions.AddStyloExtractOperatorTemplates(services, root);
}
