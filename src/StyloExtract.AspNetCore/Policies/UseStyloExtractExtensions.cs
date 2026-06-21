using Microsoft.AspNetCore.Builder;

namespace StyloExtract.AspNetCore.Policies;

/// <summary>
/// Application builder extensions for wiring the StyloExtract response-policy pipeline.
/// </summary>
public static class UseStyloExtractExtensions
{
    /// <summary>
    /// Wires the StyloExtract response-policy pipeline into the middleware chain.
    /// Place AFTER UseRouting, UseAuthentication, and UseAuthorization so that
    /// endpoint metadata is resolved and authentication context is available.
    /// </summary>
    public static IApplicationBuilder UseStyloExtract(this IApplicationBuilder app)
    {
        return app.UseMiddleware<ResponsePolicyMiddleware>();
    }
}
