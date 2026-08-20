namespace BotNexus.Agent.Providers.Core.Utilities;

/// <summary>
/// <c>this string</c> extensions for provider-side text hygiene (#2925), so an engineer holding a
/// payload string can discover the surrogate repair without knowing the helper class name.
/// </summary>
/// <remarks>
/// This lives in <c>BotNexus.Agent.Providers.Core</c> rather than the domain home
/// (<c>BotNexus.Domain.Text.StringTextExtensions</c>) because the provider layer does not depend on
/// <c>BotNexus.Domain</c> and adding that reference to make one extension reachable would be a far
/// larger change than the operation is worth.
/// </remarks>
public static class UnicodeStringExtensions
{
    /// <summary>
    /// Removes unpaired Unicode surrogates, which several provider APIs reject outright.
    /// Returns the original reference when the text is already well formed.
    /// </summary>
    /// <param name="text">The text to repair. Null and empty are returned unchanged.</param>
    public static string SanitizeSurrogates(this string text)
        => UnicodeSanitizer.SanitizeSurrogatesCore(text);
}
