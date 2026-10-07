using System.Runtime.InteropServices;

namespace BotNexus.Cli.Services;

/// <summary>
/// Manages BotNexus gateway as a launchd user agent on macOS.
/// </summary>
internal sealed class LaunchdServiceManager : IOsServiceManager
{
    private const string ServiceLabel = "ai.botnexus.gateway";
    private static readonly string PlistPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", $"{ServiceLabel}.plist");

    private readonly IServiceProcessRunner _runner;
    private readonly string _plistPath;

    public LaunchdServiceManager()
        : this(SystemServiceProcessRunner.Instance, PlistPath)
    {
    }

    internal LaunchdServiceManager(IServiceProcessRunner runner, string plistPath)
    {
        _runner = runner;
        _plistPath = plistPath;
    }

    public bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    public string ServiceManagerName => "launchd";

    public Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(File.Exists(_plistPath));
    }

    public async Task<bool> IsRunningAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync("launchctl", ["list", ServiceLabel], cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task<ServiceOperationResult> StopAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync("launchctl", ["unload", _plistPath], cancellationToken);
        return result.ExitCode == 0
            ? new ServiceOperationResult(true, $"Stop requested for service '{ServiceLabel}'.")
            : new ServiceOperationResult(false, $"Failed to stop service: {result.Output}");
    }

    public async Task<ServiceOperationResult> InstallAsync(string executablePath, string homePath, int port, CancellationToken cancellationToken = default)
    {
        if (await IsInstalledAsync(cancellationToken))
            return new ServiceOperationResult(false, $"Service '{ServiceLabel}' is already installed. Uninstall first.");

        var (programPath, programArgs) = executablePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? ("dotnet", new[] { executablePath })
            : (executablePath, Array.Empty<string>());

        var argsXml = string.Join("\n    ", programArgs.Select(a => $"<string>{EscapeXml(a)}</string>"));
        var programArgsSection = programArgs.Length > 0
            ? $"\n    <key>ProgramArguments</key>\n    <array>\n      <string>{EscapeXml(programPath)}</string>\n      {argsXml}\n    </array>"
            : $"\n    <key>Program</key>\n    <string>{EscapeXml(programPath)}</string>";

        var plistContent = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key>
              <string>{ServiceLabel}</string>{programArgsSection}
              <key>EnvironmentVariables</key>
              <dict>
                <key>ASPNETCORE_URLS</key>
                <string>http://localhost:{port}</string>
                <key>BOTNEXUS_HOME</key>
                <string>{EscapeXml(homePath)}</string>
                <key>DOTNET_ENVIRONMENT</key>
                <string>Production</string>
              </dict>
              <key>RunAtLoad</key>
              <true/>
              <key>KeepAlive</key>
              <true/>
              <key>StandardOutPath</key>
              <string>{EscapeXml(Path.Combine(homePath, "logs", "launchd-stdout.log"))}</string>
              <key>StandardErrorPath</key>
              <string>{EscapeXml(Path.Combine(homePath, "logs", "launchd-stderr.log"))}</string>
            </dict>
            </plist>
            """;

        var dir = Path.GetDirectoryName(_plistPath)!;
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(_plistPath, plistContent, cancellationToken);

        var (loadExit, loadOutput) = await RunAsync("launchctl", ["load", _plistPath], cancellationToken);
        if (loadExit != 0)
            return new ServiceOperationResult(false, $"Plist written but launchctl load failed: {loadOutput}");

        return new ServiceOperationResult(true, $"Service '{ServiceLabel}' installed and loaded (port {port}).");
    }

    public async Task<ServiceOperationResult> UninstallAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsInstalledAsync(cancellationToken))
            return new ServiceOperationResult(true, $"Service '{ServiceLabel}' is not installed.");

        // The lifecycle coordinator has already confirmed the job is down.
        if (File.Exists(_plistPath))
            File.Delete(_plistPath);

        return new ServiceOperationResult(true, $"Service '{ServiceLabel}' unloaded and removed.");
    }

    private Task<ProcessRunResult> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        => _runner.RunAsync(command, arguments, cancellationToken);

    private static string EscapeXml(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
