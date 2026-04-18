namespace BotNexus.Agent.Providers.Core.Utilities;

/// <summary>
/// Generates short deterministic hash strings for tool call ID normalization.
/// Matches pi-mono's shortHash() behavior for cross-provider compatibility.
/// </summary>
public static class ShortHash
{
    /// <summary>
    /// Returns a short alphanumeric hash of the input string.
    /// Port of pi-mono's shortHash() from utils/hash.ts.
    /// </summary>
    /// <param name="input">Source string to hash.</param>
    /// <returns>Deterministic lowercase base-36 hash string.</returns>
    public static string Generate(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var h1 = unchecked((int)0xDEADBEEF);
        var h2 = unchecked((int)0x41C6CE57);

        foreach (var ch in input)
        {
            var codePoint = (int)ch;
            h1 = unchecked((int)((uint)(h1 ^ codePoint) * 2654435761u));
            h2 = unchecked((int)((uint)(h2 ^ codePoint) * 1597334677u));
        }

        h1 = unchecked((int)((uint)(h1 ^ (int)((uint)h1 >> 16)) * 2246822507u))
             ^ unchecked((int)((uint)(h2 ^ (int)((uint)h2 >> 13)) * 3266489909u));

        h2 = unchecked((int)((uint)(h2 ^ (int)((uint)h2 >> 16)) * 2246822507u))
             ^ unchecked((int)((uint)(h1 ^ (int)((uint)h1 >> 13)) * 3266489909u));

        return ToBase36((uint)h2) + ToBase36((uint)h1);
    }

    private static string ToBase36(uint value)
    {
        if (value == 0)
            return "0";

        Span<char> buffer = stackalloc char[13];
        var index = buffer.Length;

        while (value > 0)
        {
            var digit = (int)(value % 36);
            buffer[--index] = (char)(digit < 10 ? '0' + digit : 'a' + (digit - 10));
            value /= 36;
        }

        return new string(buffer[index..]);
    }
}
