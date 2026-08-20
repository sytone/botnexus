namespace BotNexus.Agent.Providers.Core.Utilities;

using System.Text;

/// <summary>
/// <c>this string</c> extensions for provider-side text hygiene (#2925), so an engineer holding a
/// payload string can discover the surrogate repair without knowing a helper class name.
/// </summary>
/// <remarks>
/// <para>
/// This is the implementation, not a wrapper. The former <c>UnicodeSanitizer</c> static helper was
/// removed outright rather than retained as a forwarding shim: it had no callers outside this
/// repository, so keeping a second entry point would have preserved exactly the discoverability
/// problem #2925 exists to remove. One operation, one file.
/// </para>
/// <para>
/// This lives in <c>BotNexus.Agent.Providers.Core</c> rather than the domain home
/// (<c>BotNexus.Domain.Text.StringTextExtensions</c>) because the provider layer does not depend on
/// <c>BotNexus.Domain</c> and adding that reference to make one extension reachable would be a far
/// larger change than the operation is worth.
/// </para>
/// </remarks>
public static class UnicodeStringExtensions
{
    /// <summary>
    /// Removes unpaired Unicode surrogates, which several provider APIs reject outright.
    /// Returns the original reference when the text is already well formed, so the common
    /// case allocates nothing.
    /// </summary>
    /// <param name="text">The text to repair. Null and empty are returned unchanged.</param>
    public static string SanitizeSurrogates(this string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var needsRepair = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    needsRepair = true;
                    break;
                }

                i++; // skip the valid low surrogate
            }
            else if (char.IsLowSurrogate(c))
            {
                needsRepair = true;
                break;
            }
        }

        if (!needsRepair)
            return text;

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    sb.Append(c);
                    sb.Append(text[++i]);
                }
            }
            else if (!char.IsLowSurrogate(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
