using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BotNexus.Cli.Services;

/// <summary>
/// Manages BotNexus gateway as a systemd service on Linux.
/// </summary>
internal sealed class SystemdServiceManager : IOsServiceManager
{
    private const string ServiceName = "botnexus";
    private const string UnitFileName = "botnexus.service";
    private static readonly string UnitFilePath = $"/etc/systemd/system/{UnitFileName}";

    public bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public string ServiceManagerName => "systemd";

    public async Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default)
    {
        var (exitCode, _) = await RunAsync("systemctl", $"is-enabled {ServiceName}", cancellationToken);
        return exitCode == 0;
    }

    public async Task<ServiceOperationResult> InstallAsync(string executablePath, string homePath, int port, CancellationToken cancellationToken = default)
    {
        if (await IsInstalledAsync(cancellationToken))
            return new ServiceOperationResult(false, $"Service '{ServiceName}' is already installed. Uninstall first.");

        var execLine = executablePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? $"dotnet \"{executablePath}\""
            : $"\"{executablePath}\"";

        var unitContent = $"""
            [Unit]
            Description=BotNexus AI Agent Gateway
            After=network.target

            [Service]
            Type=notify
            ExecStart={execLine}
            WorkingDirectory={Path.GetDirectoryName(executablePath)}
            Restart=on-failure
            RestartSec=5
            Environment=ASPNETCORE_URLS=http://localhost:{port}
            Environment=BOTNEXUS_HOME={homePath}
            Environment=DOTNET_ENVIRONMENT=Production
            KillSignal=SIGINT
            SyslogIdentifier=botnexus
            TimeoutStopSec=30

            [Install]
            WantedBy=multi-user.target
            """;

        try
        {
            await File.WriteAllTextAsync(UnitFilePath, unitContent, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return new ServiceOperationResult(false, $"Permission denied writing {UnitFilePath}. Run with sudo.");
        }

        // Reload systemd, enable and start
        await RunAsync("systemctl", "daemon-reload", cancellationToken);
        var (enableExit, enableOutput) = await RunAsync("systemctl", $"enable --now {ServiceName}", cancellationToken);

        if (enableExit != 0)
            return new ServiceOperationResult(false, $"Failed to enable/start service: {enableOutput}");

        return new ServiceOperationResult(true, $"Service '{ServiceName}' installed and started (port {port}).");
    }

    public async Task<ServiceOperationResult> UninstallAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsInstalledAsync(cancellationToken))
            return new ServiceOperationResult(true, $"Service '{ServiceName}' is not installed.");

        // Stop and disable
        await RunAsync("systemctl", $"stop {ServiceName}", cancellationToken);
        await RunAsync("systemctl", $"disable {ServiceName}", cancellationToken);

        // Remove unit file
        if (File.Exists(UnitFilePath))
        {
            try
            {
                File.Delete(UnitFilePath);
            }
            catch (UnauthorizedAccessException)
            {
                return new ServiceOperationResult(false, $"Permission denied removing {UnitFilePath}. Run with sudo.");
            }
        }

        await RunAsync("systemctl", "daemon-reload", cancellationToken);

        return new ServiceOperationResult(true, $"Service '{ServiceName}' stopped and removed.");
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
}
