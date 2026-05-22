using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BotNexus.Cli.Services;

/// <summary>
/// Installs and uninstalls the BotNexus Gateway as a native OS service.
/// <list type="bullet">
///   <item><description>Linux — writes a systemd unit file under <c>/etc/systemd/system/</c> and runs <c>systemctl enable --now</c>.</description></item>
///   <item><description>Windows — uses <c>sc.exe</c> to register the service.</description></item>
///   <item><description>macOS — writes a launchd plist to <c>~/Library/LaunchAgents/</c> and loads it.</description></item>
/// </list>
/// </summary>
public sealed class GatewayServiceInstaller : IGatewayServiceInstaller
{
    internal const string ServiceName = "botnexus";
    internal const string SystemdUnitPath = "/etc/systemd/system/botnexus.service";
    internal const string LaunchAgentDir = "Library/LaunchAgents";
    internal const string LaunchAgentPlistName = "ai.botnexus.gateway";

    /// <inheritdoc />
    public Task<ServiceInstallStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var installed = File.Exists(SystemdUnitPath);
            return Task.FromResult(new ServiceInstallStatus(installed, "linux", ServiceName));
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var installed = WindowsServiceExists();
            return Task.FromResult(new ServiceInstallStatus(installed, "windows", ServiceName));
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var plistPath = Path.Combine(home, LaunchAgentDir, $"{LaunchAgentPlistName}.plist");
            var installed = File.Exists(plistPath);
            return Task.FromResult(new ServiceInstallStatus(installed, "macos", LaunchAgentPlistName));
        }

        return Task.FromResult(new ServiceInstallStatus(false, "unknown", null));
    }

    /// <inheritdoc />
    public async Task<ServiceInstallResult> InstallAsync(
        string executablePath,
        string homePath,
        int port = 5005,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return new ServiceInstallResult(false, "Executable path is required.");
        if (!File.Exists(executablePath))
            return new ServiceInstallResult(false, $"Executable not found: {executablePath}");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return await InstallSystemdAsync(executablePath, homePath, port, cancellationToken);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return await InstallWindowsServiceAsync(executablePath, homePath, port, cancellationToken);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return await InstallLaunchdAsync(executablePath, homePath, port, cancellationToken);

        return new ServiceInstallResult(false, "OS service installation is not supported on this platform.");
    }

    /// <inheritdoc />
    public async Task<ServiceInstallResult> UninstallAsync(CancellationToken cancellationToken = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return await UninstallSystemdAsync(cancellationToken);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return await UninstallWindowsServiceAsync(cancellationToken);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return await UninstallLaunchdAsync(cancellationToken);

        return new ServiceInstallResult(false, "OS service uninstall is not supported on this platform.");
    }

    // ── Linux / systemd ──────────────────────────────────────────────────────

    /// <summary>Generates the systemd unit file content for the gateway service.</summary>
    internal static string BuildSystemdUnit(string executablePath, string homePath, int port)
    {
        var dotnetPath = ResolveDotnetPath();
        return $"""
[Unit]
Description=BotNexus Gateway
After=network.target

[Service]
Type=notify
ExecStart={dotnetPath} "{executablePath}" --urls "http://localhost:{port}"
Environment=BOTNEXUS_HOME={homePath}
Restart=on-failure
RestartSec=5s
WorkingDirectory={Path.GetDirectoryName(executablePath) ?? "/"}

[Install]
WantedBy=multi-user.target
""";
    }

    private static async Task<ServiceInstallResult> InstallSystemdAsync(
        string executablePath, string homePath, int port, CancellationToken cancellationToken)
    {
        var unitContent = BuildSystemdUnit(executablePath, homePath, port);
        try
        {
            await File.WriteAllTextAsync(SystemdUnitPath, unitContent, cancellationToken);
        }
        catch (Exception ex)
        {
            return new ServiceInstallResult(false,
                $"Failed to write unit file (run with sudo): {ex.Message}");
        }

        var (code, output) = await RunProcessAsync("systemctl", "enable --now botnexus", cancellationToken);
        if (code != 0)
            return new ServiceInstallResult(false, $"systemctl enable --now failed: {output}");

        return new ServiceInstallResult(true, $"Service installed and started. Unit: {SystemdUnitPath}");
    }

    private static async Task<ServiceInstallResult> UninstallSystemdAsync(CancellationToken cancellationToken)
    {
        await RunProcessAsync("systemctl", "disable --now botnexus", cancellationToken);

        if (File.Exists(SystemdUnitPath))
        {
            try { File.Delete(SystemdUnitPath); }
            catch (Exception ex)
            {
                return new ServiceInstallResult(false,
                    $"Failed to delete unit file (run with sudo): {ex.Message}");
            }
        }

        await RunProcessAsync("systemctl", "daemon-reload", cancellationToken);
        return new ServiceInstallResult(true, "Service stopped, disabled, and unit file removed.");
    }

    // ── Windows / sc.exe ─────────────────────────────────────────────────────

    private static async Task<ServiceInstallResult> InstallWindowsServiceAsync(
        string executablePath, string homePath, int port, CancellationToken cancellationToken)
    {
        var dotnetPath = ResolveDotnetPath();
        var binPath = $"{dotnetPath} \"{executablePath}\" --urls \"http://localhost:{port}\"";

        var (code, output) = await RunProcessAsync(
            "sc.exe",
            $"create {ServiceName} binPath= \"{binPath}\" start= auto DisplayName= \"BotNexus Gateway\"",
            cancellationToken);

        if (code != 0)
            return new ServiceInstallResult(false, $"sc create failed: {output}");

        var (startCode, startOutput) = await RunProcessAsync(
            "sc.exe", $"start {ServiceName}", cancellationToken);

        if (startCode != 0)
            return new ServiceInstallResult(false,
                $"Service registered but failed to start: {startOutput}");

        return new ServiceInstallResult(true, $"Service '{ServiceName}' installed and started.");
    }

    private static async Task<ServiceInstallResult> UninstallWindowsServiceAsync(
        CancellationToken cancellationToken)
    {
        await RunProcessAsync("sc.exe", $"stop {ServiceName}", cancellationToken);
        var (code, output) = await RunProcessAsync(
            "sc.exe", $"delete {ServiceName}", cancellationToken);

        if (code != 0)
            return new ServiceInstallResult(false, $"sc delete failed: {output}");

        return new ServiceInstallResult(true, $"Service '{ServiceName}' stopped and removed.");
    }

    private static bool WindowsServiceExists()
    {
        var (exitCode, _) = RunProcessAsync("sc.exe", $"query {ServiceName}",
            CancellationToken.None).GetAwaiter().GetResult();
        return exitCode == 0;
    }

    // ── macOS / launchd ──────────────────────────────────────────────────────

    /// <summary>Generates the launchd plist content for the gateway service.</summary>
    internal static string BuildLaunchAgentPlist(string executablePath, string homePath, int port)
    {
        var dotnetPath = ResolveDotnetPath();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var logDir = Path.Combine(home, ".botnexus", "logs");
        return $"""
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
    "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>{LaunchAgentPlistName}</string>
    <key>ProgramArguments</key>
    <array>
        <string>{dotnetPath}</string>
        <string>{executablePath}</string>
        <string>--urls</string>
        <string>http://localhost:{port}</string>
    </array>
    <key>EnvironmentVariables</key>
    <dict>
        <key>BOTNEXUS_HOME</key>
        <string>{homePath}</string>
    </dict>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>StandardOutPath</key>
    <string>{logDir}/gateway-launchd.log</string>
    <key>StandardErrorPath</key>
    <string>{logDir}/gateway-launchd-error.log</string>
</dict>
</plist>
""";
    }

    private static async Task<ServiceInstallResult> InstallLaunchdAsync(
        string executablePath, string homePath, int port, CancellationToken cancellationToken)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var launchAgentsDir = Path.Combine(home, LaunchAgentDir);
        var plistPath = Path.Combine(launchAgentsDir, $"{LaunchAgentPlistName}.plist");

        try
        {
            Directory.CreateDirectory(launchAgentsDir);
            await File.WriteAllTextAsync(plistPath,
                BuildLaunchAgentPlist(executablePath, homePath, port), cancellationToken);
        }
        catch (Exception ex)
        {
            return new ServiceInstallResult(false, $"Failed to write plist: {ex.Message}");
        }

        var (code, output) = await RunProcessAsync(
            "launchctl", $"load -w \"{plistPath}\"", cancellationToken);

        if (code != 0)
            return new ServiceInstallResult(false, $"launchctl load failed: {output}");

        return new ServiceInstallResult(true, $"LaunchAgent installed. Plist: {plistPath}");
    }

    private static async Task<ServiceInstallResult> UninstallLaunchdAsync(
        CancellationToken cancellationToken)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var plistPath = Path.Combine(home, LaunchAgentDir, $"{LaunchAgentPlistName}.plist");

        await RunProcessAsync("launchctl", $"unload -w \"{plistPath}\"", cancellationToken);

        if (File.Exists(plistPath))
        {
            try { File.Delete(plistPath); }
            catch (Exception ex)
            {
                return new ServiceInstallResult(false, $"Failed to delete plist: {ex.Message}");
            }
        }

        return new ServiceInstallResult(true, "LaunchAgent unloaded and plist removed.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    internal static string ResolveDotnetPath()
    {
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
        {
            var exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
            var candidate = Path.Combine(dotnetRoot, exe);
            if (File.Exists(candidate))
                return candidate;
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
    }

    internal static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string fileName, string arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var combined = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
        return (process.ExitCode, combined);
    }
}
