using System.Text;

namespace BotNexus.Agent.Providers.Core.Utilities;

/// <summary>
/// Sanitize unpaired Unicode surrogates that can cause API errors.
/// Port of pi-mono's utils/sanitize-unicode.ts.
/// </summary>
public static class UnicodeSanitizer
{
    /// <summary>
    /// Remove unpaired Unicode surrogates.
    /// </summary>
    /// <remarks>
    /// Documented forwarding shim (#2925). Callable as <c>text.SanitizeSurrogates()</c> via
    /// <see cref="UnicodeStringExtensions.SanitizeSurrogates"/>; this entry point is retained
    /// verbatim so the ~30 existing converter call sites and the public API surface are untouched.
    /// </remarks>
    public static string SanitizeSurrogates(string text) => SanitizeSurrogatesCore(text);

    /// <summary>Single implementation shared by the static shim and the string extension.</summary>
    internal static string SanitizeSurrogatesCore(string text)
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
