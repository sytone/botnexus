using System.Runtime.InteropServices;

namespace BotNexus.Cli.Services;

/// <summary>
/// Manages BotNexus gateway as a Windows Service using sc.exe.
/// </summary>
internal sealed class WindowsServiceManager : IOsServiceManager
{
    private const string ServiceName = "BotNexus";
    private const string DisplayName = "BotNexus Gateway";
    private const string Description = "BotNexus AI agent gateway service";

    /// <summary>
    /// Separator reg.exe uses between REG_MULTI_SZ entries, both when printing a value from
    /// <c>reg query</c> and when parsing the <c>/d</c> payload of <c>reg add</c>. It is the
    /// two-character sequence backslash-zero, not a NUL byte.
    /// </summary>
    private const string MultiSzSeparator = @"\0";

    /// <summary>
    /// Environment keys BotNexus owns on the service key. Only these are replaced during an
    /// install; every other entry an operator has set is carried through untouched.
    /// </summary>
    internal static readonly string[] OwnedEnvironmentKeys = ["BOTNEXUS_HOME", "ASPNETCORE_URLS"];

    private readonly IServiceProcessRunner _runner;

    public WindowsServiceManager()
        : this(SystemServiceProcessRunner.Instance)
    {
    }

    internal WindowsServiceManager(IServiceProcessRunner runner) => _runner = runner;

    public bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public string ServiceManagerName => "Windows Service";

    internal static string RegistryKeyPath => $@"HKLM\SYSTEM\CurrentControlSet\Services\{ServiceName}";

    public async Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunScAsync($"query {ServiceName}", cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task<ServiceOperationResult> InstallAsync(string executablePath, string homePath, int port, CancellationToken cancellationToken = default)
    {
        if (await IsInstalledAsync(cancellationToken))
            return new ServiceOperationResult(false, $"Service '{ServiceName}' is already installed. Uninstall first.");

        // Resolve the dotnet host path for running a DLL-based service
        var binPath = executablePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? $"\"{GetDotnetPath()}\" \"{executablePath}\" --urls \"http://localhost:{port}\""
            : $"\"{executablePath}\" --urls \"http://localhost:{port}\"";

        // Create the service
        var create = await RunScAsync(
            $"create {ServiceName} binPath= \"{binPath}\" start= delayed-auto DisplayName= \"{DisplayName}\"",
            cancellationToken);

        if (create.ExitCode != 0)
            return new ServiceOperationResult(false, $"Failed to create service: {create.Output}");

        // Set description
        await RunScAsync($"description {ServiceName} \"{Description}\"", cancellationToken);

        // Merge the BotNexus-owned environment keys into whatever the service key already carries.
        // A failure here is fatal: silently continuing would leave the service without its home
        // path, or would misreport a destroyed operator environment as a clean install.
        var envResult = await SetServiceEnvironmentAsync(homePath, port, cancellationToken);
        if (!envResult.Success)
            return envResult;

        // Configure failure recovery: restart on first three failures
        await RunScAsync($"failure {ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000", cancellationToken);

        // Start the service
        var start = await RunScAsync($"start {ServiceName}", cancellationToken);
        if (start.ExitCode != 0)
            return new ServiceOperationResult(true, $"Service installed but failed to start: {start.Output}. Start manually with 'sc start {ServiceName}'.");

        return new ServiceOperationResult(true, $"Service '{ServiceName}' installed and started (port {port}).");
    }

    public async Task<ServiceOperationResult> UninstallAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsInstalledAsync(cancellationToken))
            return new ServiceOperationResult(true, $"Service '{ServiceName}' is not installed.");

        // Stop first (ignore errors -- may already be stopped)
        await RunScAsync($"stop {ServiceName}", cancellationToken);
        await Task.Delay(2000, cancellationToken); // give it time to stop

        // Delete the service
        var delete = await RunScAsync($"delete {ServiceName}", cancellationToken);
        if (delete.ExitCode != 0)
            return new ServiceOperationResult(false, $"Failed to delete service: {delete.Output}");

        return new ServiceOperationResult(true, $"Service '{ServiceName}' stopped and removed.");
    }

    /// <summary>
    /// Reads the existing service <c>Environment</c> REG_MULTI_SZ value, replaces only the
    /// BotNexus-owned keys, and writes the union back. Entries BotNexus does not own -- including
    /// operator-supplied credentials -- survive the write.
    /// </summary>
    internal async Task<ServiceOperationResult> SetServiceEnvironmentAsync(string homePath, int port, CancellationToken cancellationToken)
    {
        var existing = await ReadExistingEnvironmentAsync(cancellationToken);

        var desired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BOTNEXUS_HOME"] = homePath,
            ["ASPNETCORE_URLS"] = $"http://localhost:{port}"
        };

        var merged = MergeEnvironment(existing, desired);
        var payload = string.Join(MultiSzSeparator, merged);

        // Discrete argument tokens: the runtime performs platform-correct quoting, so a homePath
        // containing a quote character cannot terminate the argument early or inject a new one.
        var add = await _runner.RunAsync(
            "reg",
            ["add", RegistryKeyPath, "/v", "Environment", "/t", "REG_MULTI_SZ", "/d", payload, "/f"],
            cancellationToken);

        if (add.ExitCode != 0)
            return new ServiceOperationResult(false, $"Failed to write service environment (reg.exe exit {add.ExitCode}): {add.Output}");

        return new ServiceOperationResult(true, "Service environment updated.");
    }

    private async Task<IReadOnlyList<string>> ReadExistingEnvironmentAsync(CancellationToken cancellationToken)
    {
        var query = await _runner.RunAsync(
            "reg",
            ["query", RegistryKeyPath, "/v", "Environment"],
            cancellationToken);

        // A missing value is the normal first-install case, not an error.
        return query.ExitCode != 0 ? [] : ParseMultiSz(query.Output);
    }

    /// <summary>
    /// Extracts the REG_MULTI_SZ entries from <c>reg query</c> output, which prints the value on a
    /// single indented line as <c>Environment    REG_MULTI_SZ    A=1\0B=2</c>.
    /// </summary>
    internal static IReadOnlyList<string> ParseMultiSz(string regQueryOutput)
    {
        if (string.IsNullOrWhiteSpace(regQueryOutput))
            return [];

        foreach (var line in regQueryOutput.Split('\n'))
        {
            var index = line.IndexOf("REG_MULTI_SZ", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                continue;

            var payload = line[(index + "REG_MULTI_SZ".Length)..].Trim('\r', ' ', '\t');
            if (payload.Length == 0)
                return [];

            return payload
                .Split(MultiSzSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Trim('\r'))
                .Where(entry => entry.Length > 0)
                .ToArray();
        }

        return [];
    }

    /// <summary>
    /// Produces the merged entry list: existing entries keep their relative order, owned keys are
    /// updated in place rather than duplicated, and owned keys not already present are appended.
    /// </summary>
    internal static IReadOnlyList<string> MergeEnvironment(IReadOnlyList<string> existing, IReadOnlyDictionary<string, string> owned)
    {
        var merged = new List<string>();
        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in existing)
        {
            var separator = entry.IndexOf('=');
            var key = separator < 0 ? entry : entry[..separator];

            if (owned.TryGetValue(key, out var value))
            {
                if (applied.Add(key))
                    merged.Add($"{key}={value}");
                continue; // duplicate occurrences of an owned key collapse into the single new entry
            }

            merged.Add(entry);
        }

        foreach (var (key, value) in owned)
        {
            if (applied.Add(key))
                merged.Add($"{key}={value}");
        }

        return merged;
    }

    private Task<ProcessRunResult> RunScAsync(string arguments, CancellationToken cancellationToken)
        // sc.exe uses 'name= value' option syntax that discrete argument tokens cannot express.
        => _runner.RunRawAsync("sc.exe", arguments, cancellationToken);

    private static string GetDotnetPath()
    {
        var dotnetPath = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetPath))
            return Path.Combine(dotnetPath, "dotnet.exe");

        // Fallback to PATH resolution
        return "dotnet";
    }
}
