using System.Diagnostics;
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

    public bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    public string ServiceManagerName => "launchd";

    public Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(File.Exists(PlistPath));
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

        var dir = Path.GetDirectoryName(PlistPath)!;
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(PlistPath, plistContent, cancellationToken);

        var (loadExit, loadOutput) = await RunAsync("launchctl", $"load \"{PlistPath}\"", cancellationToken);
        if (loadExit != 0)
            return new ServiceOperationResult(false, $"Plist written but launchctl load failed: {loadOutput}");

        return new ServiceOperationResult(true, $"Service '{ServiceLabel}' installed and loaded (port {port}).");
    }

    public async Task<ServiceOperationResult> UninstallAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsInstalledAsync(cancellationToken))
            return new ServiceOperationResult(true, $"Service '{ServiceLabel}' is not installed.");

        await RunAsync("launchctl", $"unload \"{PlistPath}\"", cancellationToken);

        if (File.Exists(PlistPath))
            File.Delete(PlistPath);

        return new ServiceOperationResult(true, $"Service '{ServiceLabel}' unloaded and removed.");
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string command, string arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {command}");

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, string.IsNullOrWhiteSpace(output) ? error : output);
    }

    private static string EscapeXml(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
