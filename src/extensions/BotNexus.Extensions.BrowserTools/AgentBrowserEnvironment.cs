namespace BotNexus.Extensions.BrowserTools;

/// <summary>
/// Builds the environment handed to the <c>agent-browser</c> child process (#3031 AC4).
/// </summary>
/// <remarks>
/// <para>
/// The child environment is constructed FROM EMPTY and then populated from an explicit
/// allow-list. It is never the parent's environment with a few names removed. That direction is
/// the whole control (GHSA-m4m8-xjp4-5rmm): a deny-list is a claim about every secret name that
/// will ever exist, and the operator keyring this gateway runs under carries provider API keys,
/// channel tokens and cloud credentials under names nobody here can enumerate. A browser worker
/// that is driven by attacker-controlled page content must not be able to read any of them, and
/// the only way to guarantee that is to never copy them in the first place.
/// </para>
/// <para>
/// Everything on the allow-list is there because the process cannot start or cannot find Chrome
/// without it. Nothing on it carries authentication material. When a new name is proposed for
/// this list the question to ask is not "is it useful" but "could its value authenticate to
/// anything" - if the answer is yes or unknown, it does not go on.
/// </para>
/// </remarks>
public static class AgentBrowserEnvironment
{
    /// <summary>
    /// The only parent environment variables copied into the child, in full.
    /// </summary>
    /// <remarks>
    /// Deliberately public and deliberately a single list: a test asserts against this exact set,
    /// so widening it is a visible edit in a reviewed file rather than a scattered string literal.
    /// </remarks>
    public static readonly IReadOnlyList<string> AllowedVariables =
    [
        // Process launch and executable resolution.
        "PATH",
        "PATHEXT",
        "SystemRoot",
        "SystemDrive",
        "windir",
        "ComSpec",

        // Scratch space. agent-browser and Chrome both need somewhere to write.
        "TEMP",
        "TMP",
        "TMPDIR",

        // Profile roots. Chrome's user-data directory and agent-browser's own state live here.
        "HOME",
        "USERPROFILE",
        "APPDATA",
        "LOCALAPPDATA",
        "XDG_CONFIG_HOME",
        "XDG_CACHE_HOME",
        "XDG_RUNTIME_DIR",

        // Locale and display. A headful Chrome on Linux cannot attach without DISPLAY.
        "LANG",
        "LC_ALL",
        "DISPLAY",
        "WAYLAND_DISPLAY",

        // Explicit browser location overrides an operator may have set.
        "CHROME_PATH",
        "CHROME_EXECUTABLE_PATH",
        "AGENT_BROWSER_HOME",
    ];

    /// <summary>
    /// Builds the child environment from an empty dictionary using <see cref="AllowedVariables"/>.
    /// </summary>
    /// <param name="readParentVariable">
    /// Reads a named variable from the parent environment. Injected rather than calling
    /// <see cref="Environment.GetEnvironmentVariable(string)"/> directly so a test can present a
    /// parent environment containing a sentinel secret and assert its absence from the result -
    /// which is how AC4 is proven rather than asserted about.
    /// </param>
    /// <returns>
    /// A fresh dictionary containing only allow-listed names that had a non-empty parent value.
    /// </returns>
    public static IReadOnlyDictionary<string, string> Build(
        Func<string, string?>? readParentVariable = null)
    {
        var read = readParentVariable ?? Environment.GetEnvironmentVariable;

        // Ordinal, not OrdinalIgnoreCase. Environment variable names are case-insensitive on
        // Windows and case-SENSITIVE on Linux; using the case-insensitive comparer everywhere
        // would silently collapse two distinct Linux variables into one.
        var child = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in AllowedVariables)
        {
            var value = read(name);
            if (!string.IsNullOrEmpty(value))
            {
                child[name] = value;
            }
        }

        return child;
    }
}
