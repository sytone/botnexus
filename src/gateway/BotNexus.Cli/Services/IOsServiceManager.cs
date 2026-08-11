namespace BotNexus.Cli.Services;

/// <summary>
/// Abstracts OS service installation/uninstallation for cross-platform support.
/// </summary>
internal interface IOsServiceManager
{
    /// <summary>Returns true if the current platform is supported for service installation.</summary>
    bool IsSupported { get; }

    /// <summary>Gets the name of the platform service manager (e.g., "systemd", "Windows Service", "launchd").</summary>
    string ServiceManagerName { get; }

    /// <summary>Returns true if the BotNexus service is currently installed.</summary>
    Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs the BotNexus gateway as an OS service.
    /// </summary>
    /// <remarks>
    /// Implementations must treat the service environment as a SHARED surface. Only the
    /// BotNexus-owned keys may be written (<c>BOTNEXUS_HOME</c>, <c>ASPNETCORE_URLS</c>, plus
    /// <c>DOTNET_ENVIRONMENT</c> on systemd); every other entry an operator has set must be read
    /// and carried through unchanged. A failed environment write must surface as an unsuccessful
    /// <see cref="ServiceOperationResult"/> rather than being swallowed.
    /// </remarks>
    /// <param name="executablePath">Full path to the gateway executable or DLL.</param>
    /// <param name="homePath">BotNexus home directory path.</param>
    /// <param name="port">Port for the gateway to listen on.</param>
    Task<ServiceOperationResult> InstallAsync(string executablePath, string homePath, int port, CancellationToken cancellationToken = default);

    /// <summary>Uninstalls (stops + removes) the BotNexus gateway OS service.</summary>
    Task<ServiceOperationResult> UninstallAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a service install/uninstall operation.
/// </summary>
/// <param name="Success">Whether the operation completed successfully.</param>
/// <param name="Message">Human-readable outcome description.</param>
internal record ServiceOperationResult(bool Success, string Message);
