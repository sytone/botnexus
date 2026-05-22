namespace BotNexus.Cli.Services;

/// <summary>
/// Result of a service install or uninstall operation.
/// </summary>
public sealed record ServiceInstallResult(bool Success, string Message);

/// <summary>
/// Represents the installation state of the gateway OS service.
/// </summary>
public sealed record ServiceInstallStatus(bool IsInstalled, string Platform, string? ServiceName);

/// <summary>
/// Abstracts OS-level service registration: install, uninstall, and status.
/// </summary>
public interface IGatewayServiceInstaller
{
    /// <summary>
    /// Returns the current installation status of the gateway OS service.
    /// </summary>
    Task<ServiceInstallStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs and enables the gateway as an OS service.
    /// </summary>
    /// <param name="executablePath">Absolute path to the gateway DLL (dotnet publish output).</param>
    /// <param name="homePath">BotNexus home directory passed to the service via environment variable.</param>
    /// <param name="port">HTTP port the gateway should listen on.</param>
    Task<ServiceInstallResult> InstallAsync(
        string executablePath,
        string homePath,
        int port = 5005,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops and removes the gateway OS service registration.
    /// </summary>
    Task<ServiceInstallResult> UninstallAsync(CancellationToken cancellationToken = default);
}
