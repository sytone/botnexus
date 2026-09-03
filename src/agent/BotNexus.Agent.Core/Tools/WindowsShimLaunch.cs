using System.Diagnostics;

namespace BotNexus.Agent.Core.Tools;

/// <summary>
/// A resolved child-process launch descriptor: the executable to start plus EITHER a structured
/// argument list (the normal case) OR a raw command line that must be handed to the child verbatim.
/// </summary>
/// <remarks>
/// The raw form exists solely for the Windows <c>.cmd</c>/<c>.bat</c> shim path.
/// <c>cmd.exe /d /s /c</c> requires its payload wrapped in a literal outer quote pair, and
/// <see cref="ProcessStartInfo.ArgumentList"/> cannot express that: .NET applies CRT quoting to
/// every entry, so a payload that already contains quotes comes back out escaped as <c>\"</c>.
/// cmd.exe does not recognise backslash-escaped quotes, so it carried them into the program name
/// and reported <c>'"C:\Program Files\nodejs\npm.cmd"' is not recognized</c> - a correct path that
/// could never launch. Setting <see cref="ProcessStartInfo.Arguments"/> directly is the only way to
/// hand cmd.exe the byte sequence it actually parses.
/// </remarks>
/// <param name="FileName">Executable to launch; never quoted (UseShellExecute=false).</param>
/// <param name="Args">Structured arguments; empty when <paramref name="RawArgumentLine"/> is set.</param>
/// <param name="RawArgumentLine">Verbatim command line, or null to use <paramref name="Args"/>.</param>
/// <param name="ResolvedTarget">
/// The path the PATH/PATHEXT probe actually selected, or null when nothing matched. Carried so a
/// launch failure can name what was attempted instead of surfacing the bare OS string
/// <c>The system cannot find the path specified.</c>, which named neither the command nor a path
/// and left the agent unable to tell the tool, the working directory and an argument apart (#3710).
/// </param>
public sealed record ProcessLaunch(
    string FileName,
    IReadOnlyList<string> Args,
    string? RawArgumentLine = null,
    string? ResolvedTarget = null)
{
    /// <summary>
    /// Describes what this descriptor tried to launch, for use in a start-failure message.
    /// </summary>
    /// <param name="requestedCommand">The command name as the caller configured it.</param>
    public string FormatLaunchFailureDetail(string requestedCommand)
        => ResolvedTarget is { } target && !string.Equals(target, requestedCommand, StringComparison.Ordinal)
            ? $"'{requestedCommand}' resolved to '{target}' and was launched via '{FileName}'"
            : $"'{requestedCommand}' was not resolved to any file on PATH (probed extensions: {string.Join(", ", ProbeExtensionNames)})";

    /// <summary>Extensions the PATH probe considers, in order; surfaced for diagnostics.</summary>
    internal static IReadOnlyList<string> ProbeExtensionNames => WindowsShimLaunch.ProbeExtensions;

    /// <summary>Two-value deconstruction for callers that do not care about the raw line.</summary>
    public void Deconstruct(out string fileName, out IReadOnlyList<string> args)
    {
        fileName = FileName;
        args = Args;
    }

    /// <summary>
    /// Applies this descriptor's arguments onto <paramref name="startInfo"/>, choosing the raw line
    /// or the structured list. Callers must use this rather than looping over <see cref="Args"/>
    /// themselves: doing it by hand is precisely how a raw cmd.exe payload gets pushed back through
    /// <see cref="ProcessStartInfo.ArgumentList"/> and re-escaped.
    /// </summary>
    public void ApplyArgumentsTo(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        if (RawArgumentLine is { } raw)
        {
            startInfo.Arguments = raw;
            return;
        }

        foreach (var arg in Args)
        {
            startInfo.ArgumentList.Add(arg);
        }
    }
}

/// <summary>
/// Single resolution seam for launching a command that may be a Windows <c>.cmd</c>/<c>.bat</c> shim.
/// </summary>
/// <remarks>
/// <para>
/// Every spawn site that accepts a user-configured command (the <c>exec</c> tool, the MCP stdio
/// transport) resolves through <see cref="Resolve"/> instead of carrying its own copy of the PATH
/// probe plus cmd.exe quoting rules. Two independent copies existed before issue #3642 and they
/// drifted exactly as duplicated code does: the <c>exec</c> copy was fixed for the escaped-quote
/// defect while the MCP copy kept building the payload as an <see cref="ProcessStartInfo.ArgumentList"/>
/// entry, so every <c>npx</c>-launched stdio MCP server on Windows failed to start.
/// </para>
/// <para>
/// This mirrors the <see cref="ProcessEnvironment"/> precedent (#2892): the platform-specific rule
/// is decided once here rather than being re-derived - and re-broken - per spawn site.
/// </para>
/// </remarks>
public static class WindowsShimLaunch
{
    internal static readonly string[] ProbeExtensions = [".exe", ".cmd", ".bat", ".ps1"];

    /// <summary>
    /// Resolves <paramref name="command"/> plus <paramref name="args"/> into a launch descriptor,
    /// routing Windows batch shims through <c>cmd.exe /d /s /c</c> with a verbatim payload.
    /// </summary>
    /// <param name="command">Command as configured; may be bare (PATH-probed) or an explicit path.</param>
    /// <param name="args">Arguments as configured.</param>
    /// <param name="fileExists">
    /// Existence probe used for the PATH search. Injected so tests can drive the resolver
    /// deterministically instead of depending on what happens to be installed on the runner.
    /// Defaults to <see cref="File.Exists(string)"/>.
    /// </param>
    public static ProcessLaunch Resolve(
        string command,
        IReadOnlyList<string> args,
        Func<string, bool>? fileExists = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        args ??= [];

        if (!OperatingSystem.IsWindows())
        {
            return new ProcessLaunch(command, args);
        }

        var probe = fileExists ?? File.Exists;
        var resolved = ResolveWindowsExecutable(command, probe);
        if (resolved is not null && IsWindowsBatchFile(resolved))
        {
            // Route through cmd.exe /d /s /c. The payload MUST be a raw line with a literal outer
            // quote pair - /s tells cmd.exe to strip exactly that outer pair and run the remainder
            // verbatim, which is what makes an inner quoted path with spaces survive.
            return new ProcessLaunch(
                Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                [],
                resolved.BuildCmdRawArgumentLine(args),
                resolved);
        }

        if (resolved is not null && IsWindowsPowerShellScript(resolved))
        {
            // A .ps1 is not an executable image, so CreateProcess cannot start it at all - that is
            // the #3710 defect. It needs a PowerShell host, and deliberately NOT the cmd.exe raw-line
            // treatment: -File hands the remaining tokens to the script as literal arguments, so a
            // structured ArgumentList passes an argument containing a space or a '$' through
            // unmodified. Routing it through cmd.exe would re-parse those under cmd's rules.
            var hostArgs = new List<string>(args.Count + 3) { "-NoProfile", "-File", resolved };
            hostArgs.AddRange(args);
            return new ProcessLaunch(ResolvePowerShellHost(probe), hostArgs, null, resolved);
        }

        return new ProcessLaunch(resolved ?? command, args, null, resolved);
    }

    /// <summary>True when <paramref name="path"/> names a PowerShell script that needs a host (#3710).</summary>
    public static bool IsWindowsPowerShellScript(string path)
        => string.Equals(Path.GetExtension(path), ".ps1", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Picks the PowerShell host for a <c>.ps1</c> shim: <c>pwsh</c> when it is on PATH, otherwise
    /// the Windows-inbox <c>powershell.exe</c>, which is always present on a Windows host.
    /// </summary>
    private static string ResolvePowerShellHost(Func<string, bool> fileExists)
    {
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in pathDirs)
        {
            var candidate = Path.Combine(dir, "pwsh.exe");
            if (fileExists(candidate))
            {
                return candidate;
            }
        }

        return "powershell.exe";
    }

    /// <summary>
    /// Builds the verbatim <c>cmd.exe</c> argument line for a resolved <c>.cmd</c>/<c>.bat</c> shim.
    /// </summary>
    /// <remarks>
    /// Declared as a <c>this string</c> extension per the #2925 fence: this is a general-purpose
    /// string-to-string transformation, so it must be discoverable from the path itself rather than
    /// requiring the caller to already know this class's name.
    /// </remarks>
    /// <param name="resolvedShimPath">Fully resolved path to the .cmd/.bat shim.</param>
    /// <param name="args">Arguments to pass through to the shim.</param>
    public static string BuildCmdRawArgumentLine(this string resolvedShimPath, IReadOnlyList<string> args)
        => $"/d /s /c \"{BuildCmdCommandLine(resolvedShimPath, args)}\"";

    /// <summary>True when <paramref name="path"/> names a Windows batch shim.</summary>
    public static bool IsWindowsBatchFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".cmd" or ".bat";
    }

    private static string BuildCmdCommandLine(string command, IReadOnlyList<string> args)
    {
        var parts = new List<string>(args.Count + 1) { QuoteForCmd(command) };
        foreach (var arg in args)
        {
            parts.Add(QuoteForCmd(arg));
        }

        return string.Join(' ', parts);
    }

    private static string QuoteForCmd(string arg)
    {
        if (!arg.Contains(' ', StringComparison.Ordinal) && !arg.Contains('"', StringComparison.Ordinal))
        {
            return arg;
        }

        return $"\"{arg.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string? ResolveWindowsExecutable(string command, Func<string, bool> fileExists)
    {
        if (Path.HasExtension(command))
        {
            return command;
        }

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var ext in ProbeExtensions)
        {
            var candidate = command + ext;
            foreach (var dir in pathDirs)
            {
                var fullPath = Path.Combine(dir, candidate);
                if (fileExists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        return null;
    }
}
