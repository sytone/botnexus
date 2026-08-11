using System.Runtime.InteropServices;

namespace BotNexus.Cli.Services;

/// <summary>
/// Manages BotNexus gateway as a systemd service on Linux.
/// </summary>
internal sealed class SystemdServiceManager : IOsServiceManager
{
    private const string ServiceName = "botnexus";
    private const string UnitFileName = "botnexus.service";
    private const string DefaultUnitFilePath = $"/etc/systemd/system/{UnitFileName}";

    /// <summary>
    /// Environment keys BotNexus owns in the unit file. Any other <c>Environment=</c> line found in
    /// an existing unit is carried over verbatim.
    /// </summary>
    internal static readonly string[] OwnedEnvironmentKeys = ["ASPNETCORE_URLS", "BOTNEXUS_HOME", "DOTNET_ENVIRONMENT"];

    private readonly IServiceProcessRunner _runner;
    private readonly string _unitFilePath;

    public SystemdServiceManager()
        : this(SystemServiceProcessRunner.Instance, DefaultUnitFilePath)
    {
    }

    internal SystemdServiceManager(IServiceProcessRunner runner, string unitFilePath)
    {
        _runner = runner;
        _unitFilePath = unitFilePath;
    }

    public bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public string ServiceManagerName => "systemd";

    public async Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync("systemctl", $"is-enabled {ServiceName}", cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task<ServiceOperationResult> InstallAsync(string executablePath, string homePath, int port, CancellationToken cancellationToken = default)
    {
        if (await IsInstalledAsync(cancellationToken))
            return new ServiceOperationResult(false, $"Service '{ServiceName}' is already installed. Uninstall first.");

        var execLine = executablePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? $"dotnet \"{executablePath}\""
            : $"\"{executablePath}\"";

        // A unit may already exist (repair/reinstall over a disabled unit). Anything an operator
        // added there -- typically a credential-bearing Environment= line -- must survive.
        var preserved = File.Exists(_unitFilePath)
            ? ExtractForeignEnvironmentLines(await File.ReadAllTextAsync(_unitFilePath, cancellationToken))
            : [];

        var environmentBlock = string.Join(
            Environment.NewLine,
            new[]
            {
                $"Environment=ASPNETCORE_URLS=http://localhost:{port}",
                $"Environment=BOTNEXUS_HOME={homePath}",
                "Environment=DOTNET_ENVIRONMENT=Production"
            }.Concat(preserved));

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
            {environmentBlock}
            KillSignal=SIGINT
            SyslogIdentifier=botnexus
            TimeoutStopSec=30

            [Install]
            WantedBy=multi-user.target
            """;

        try
        {
            await File.WriteAllTextAsync(_unitFilePath, unitContent, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return new ServiceOperationResult(false, $"Permission denied writing {_unitFilePath}. Run with sudo.");
        }

        // Reload systemd, enable and start
        await RunAsync("systemctl", "daemon-reload", cancellationToken);
        var enable = await RunAsync("systemctl", $"enable --now {ServiceName}", cancellationToken);

        if (enable.ExitCode != 0)
            return new ServiceOperationResult(false, $"Failed to enable/start service: {enable.Output}");

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
        if (File.Exists(_unitFilePath))
        {
            try
            {
                File.Delete(_unitFilePath);
            }
            catch (UnauthorizedAccessException)
            {
                return new ServiceOperationResult(false, $"Permission denied removing {_unitFilePath}. Run with sudo.");
            }
        }

        await RunAsync("systemctl", "daemon-reload", cancellationToken);

        return new ServiceOperationResult(true, $"Service '{ServiceName}' stopped and removed.");
    }

    /// <summary>
    /// Returns the <c>Environment=</c> lines of an existing unit whose key BotNexus does not own,
    /// in file order and verbatim, so they can be reproduced in the regenerated unit.
    /// </summary>
    internal static IReadOnlyList<string> ExtractForeignEnvironmentLines(string unitContent)
    {
        if (string.IsNullOrWhiteSpace(unitContent))
            return [];

        var preserved = new List<string>();

        foreach (var rawLine in unitContent.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ', '\t');
            if (!line.StartsWith("Environment=", StringComparison.Ordinal))
                continue;

            var assignment = line["Environment=".Length..];
            var separator = assignment.IndexOf('=');
            var key = separator < 0 ? assignment : assignment[..separator];

            if (OwnedEnvironmentKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                continue;

            preserved.Add(line);
        }

        return preserved;
    }

    private Task<ProcessRunResult> RunAsync(string command, string arguments, CancellationToken cancellationToken)
        => _runner.RunRawAsync(command, arguments, cancellationToken);
}
