namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Builds and applies the .NET runtime crash-dump environment variables so that any
/// <b>hard</b> process exit — including a stack overflow or <see cref="System.Environment.FailFast(string)"/>,
/// neither of which raises a catchable managed exception — leaves a minidump on disk.
/// <para>
/// These variables are read by the CLR at process startup, so they must be set in the
/// parent launcher's environment <em>before</em> the gateway process is spawned. Setting
/// them from inside the already-running gateway is too late for the runtime to honour them
/// for the current process, which is why <see cref="BuildVariables"/> is consumed by the
/// CLI process launcher.
/// </para>
/// </summary>
public static class CrashDumpEnvironment
{
    /// <summary>Enables the runtime minidump-on-crash writer.</summary>
    public const string EnableVar = "DOTNET_DbgEnableMiniDump";

    /// <summary>Selects the dump type. 2 = "MiniDumpWithPrivateReadWriteMemory" (Heap), a good crash triage default.</summary>
    public const string TypeVar = "DOTNET_DbgMiniDumpType";

    /// <summary>Template path (with a %d PID placeholder) the runtime writes the dump to.</summary>
    public const string NameVar = "DOTNET_DbgMiniDumpName";

    /// <summary>
    /// Builds the crash-dump variable set that points the runtime at a dump file under
    /// <paramref name="dumpsDirectory"/>. The dump name embeds a <c>%d</c> PID placeholder so
    /// concurrent or successive crashes never overwrite one another.
    /// </summary>
    /// <param name="dumpsDirectory">The directory dumps should be written to (e.g. <c>~/.botnexus/dumps</c>).</param>
    /// <returns>An ordered variable name → value map ready to apply to a child process environment.</returns>
    public static IReadOnlyDictionary<string, string> BuildVariables(string dumpsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dumpsDirectory);

        var dumpName = Path.Combine(dumpsDirectory, "botnexus-gateway.%d.dmp");
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EnableVar] = "1",
            // Heap dump: enough to inspect managed objects/threads without a full-memory dump's size.
            [TypeVar] = "2",
            [NameVar] = dumpName
        };
    }

    /// <summary>
    /// Applies the crash-dump variables via the supplied setter, swallowing any setter failure so
    /// that diagnostics wiring can never abort process launch. The setter is injected so the launch
    /// path (which mutates a <see cref="System.Diagnostics.ProcessStartInfo"/> environment) and the
    /// tests share one contract.
    /// </summary>
    /// <param name="dumpsDirectory">The directory dumps should land in.</param>
    /// <param name="setter">Receives each variable name/value to apply to the target environment.</param>
    /// <returns><c>true</c> when every variable was applied; <c>false</c> if any setter threw.</returns>
    public static bool Apply(string dumpsDirectory, Action<string, string> setter)
    {
        ArgumentNullException.ThrowIfNull(setter);

        try
        {
            foreach (var (key, value) in BuildVariables(dumpsDirectory))
                setter(key, value);
            return true;
        }
        catch
        {
            // Never let crash-dump wiring break the thing it is meant to observe.
            return false;
        }
    }
}
