using System.Text.RegularExpressions;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Strips ANSI terminal escape sequences from strings so tool output
/// renders cleanly in the Blazor portal instead of showing raw control codes.
/// </summary>
public static partial class AnsiStripper
{
    /// <summary>
    /// Removes all ANSI escape sequences (CSI, OSC, and single-character escapes)
    /// from the input string. Returns the input unchanged when null or empty.
    /// </summary>
    public static string? Strip(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        return AnsiEscapeRegex().Replace(input, string.Empty);
    }

    // Matches CSI sequences (\e[...X), OSC sequences (\e]...BEL/ST), and
    // two-character escape sequences (\eX) commonly emitted by CLI tools.
    [GeneratedRegex(@"\u001b(?:\[[0-9;]*[a-zA-Z]|\][^\u0007]*(?:\u0007|\u001b\\)|[a-zA-Z])")]
    private static partial Regex AnsiEscapeRegex();
}
