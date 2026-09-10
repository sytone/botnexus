namespace BotNexus.Agent.Core.Tools;

/// <summary>
/// Builds the environment handed to a tool subprocess - the <c>bash</c>/<c>shell</c> tool and the
/// <c>exec</c> tool - from an explicit allow-list rather than from the gateway's own environment.
/// </summary>
/// <remarks>
/// <para>
/// This is the same control <c>AgentBrowserEnvironment</c> applies to the browser worker
/// (GHSA-m4m8-xjp4-5rmm), applied to the two tools that are granted far more often. The browser
/// worker was hardened because it processes attacker-controlled page content; a shell tool runs
/// commands an agent composed from that same content, and is handed to more agents. The narrower
/// case was fixed first only because that is the order the advisories arrived in, not because the
/// shell is safer.
/// </para>
/// <para>
/// The direction matters and is the whole control: the child environment is constructed FROM
/// EMPTY and populated from a list of names, never the parent's environment with secrets removed.
/// A deny-list is a claim about every secret name that will ever exist. The environment a gateway
/// runs under carries provider API keys, channel tokens and whatever an operator exported for the
/// <c>env:</c> credential references in their own configuration - names nobody in this repository
/// can enumerate. <c>ISecretResolver</c> is careful never to place a resolved credential in an
/// agent's context, but that guarantee was void while any agent holding <c>bash</c> could read the
/// same variables straight out of its own process environment.
/// </para>
/// <para>
/// Everything on the allow-list is here because a command cannot run, resolve a binary, or write
/// a temporary file without it. When a new name is proposed the question is not "is it useful"
/// but "could its value authenticate to anything" - if the answer is yes or unknown, it does not
/// go on, and <paramref name="passThrough"/> is where an operator takes that risk explicitly.
/// </para>
/// </remarks>
public static class ToolProcessEnvironment
{
    /// <summary>
    /// The only parent environment variables copied into a tool subprocess by default.
    /// </summary>
    /// <remarks>
    /// Deliberately public and deliberately one list: a test asserts against this exact set, so
    /// widening it is a visible edit in a reviewed file rather than a scattered string literal.
    /// </remarks>
    public static readonly IReadOnlyList<string> AllowedVariables =
    [
        // Process launch and executable resolution. Without PATH nothing resolves at all.
        "PATH",
        "PATHEXT",
        "SystemRoot",
        "SystemDrive",
        "windir",
        "ComSpec",
        "SHELL",

        // Scratch space. Redirection, here-documents and most build tools need somewhere to write.
        "TEMP",
        "TMP",
        "TMPDIR",

        // Profile roots. Tool configuration and caches live under these.
        "HOME",
        "USERPROFILE",
        "APPDATA",
        "LOCALAPPDATA",
        "XDG_CONFIG_HOME",
        "XDG_CACHE_HOME",
        "XDG_DATA_HOME",
        "XDG_RUNTIME_DIR",

        // Locale, timezone and terminal. Output encoding and date formatting depend on these.
        "LANG",
        "LC_ALL",
        "LC_CTYPE",
        "TZ",
        "TERM",

        // Identity, as the OS already reports it. None of these authenticate anything.
        "USER",
        "LOGNAME",
        "HOSTNAME",
    ];

    /// <summary>
    /// Builds a child environment from empty, using <see cref="AllowedVariables"/> plus any names
    /// the operator has explicitly chosen to pass through.
    /// </summary>
    /// <param name="readParentVariable">
    /// Reads a named variable from the parent environment. Injected rather than calling
    /// <see cref="Environment.GetEnvironmentVariable(string)"/> directly so a test can present a
    /// parent environment containing a sentinel secret and assert its absence from the result -
    /// which is how this is proven rather than asserted about.
    /// </param>
    /// <param name="passThrough">
    /// Additional variable names an operator has opted to expose to tool subprocesses. Empty by
    /// default. This is the escape hatch for a toolchain that genuinely needs a variable the list
    /// above does not carry; it is deliberately explicit, per-name, and visible in configuration
    /// rather than a switch that turns the control off wholesale.
    /// </param>
    /// <returns>
    /// A fresh dictionary containing only permitted names that had a non-empty parent value.
    /// </returns>
    public static IReadOnlyDictionary<string, string> Build(
        Func<string, string?>? readParentVariable = null,
        IEnumerable<string>? passThrough = null)
    {
        var read = readParentVariable ?? Environment.GetEnvironmentVariable;

        // Ordinal, not OrdinalIgnoreCase. Environment variable names are case-insensitive on
        // Windows and case-SENSITIVE on Linux; the case-insensitive comparer would silently
        // collapse two distinct Linux variables into one.
        var child = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in AllowedVariables)
        {
            Copy(name);
        }

        if (passThrough is not null)
        {
            foreach (var name in passThrough)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    Copy(name.Trim());
                }
            }
        }

        return child;

        void Copy(string name)
        {
            var value = read(name);
            if (!string.IsNullOrEmpty(value))
            {
                child[name] = value;
            }
        }
    }

    /// <summary>
    /// Replaces a <see cref="System.Diagnostics.ProcessStartInfo"/>'s environment block with the
    /// allow-listed one.
    /// </summary>
    /// <remarks>
    /// The <c>Clear()</c> is the control, not tidiness. .NET seeds that dictionary from the parent
    /// process, so populating it without clearing first hands the child the whole operator keyring
    /// PLUS the allow-list - the exact opposite of the intent. Every spawn site goes through here
    /// so that the clear cannot be forgotten at one of them.
    /// </remarks>
    /// <param name="target">The environment block being built; cleared, then repopulated.</param>
    /// <param name="readParentVariable">Reads a parent variable; see <see cref="Build"/>.</param>
    /// <param name="passThrough">Operator-permitted extra names; see <see cref="Build"/>.</param>
    public static void ApplyTo(
        IDictionary<string, string?> target,
        Func<string, string?>? readParentVariable = null,
        IEnumerable<string>? passThrough = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        target.Clear();

        foreach (var (name, value) in Build(readParentVariable, passThrough))
        {
            target[name] = value;
        }
    }
}
