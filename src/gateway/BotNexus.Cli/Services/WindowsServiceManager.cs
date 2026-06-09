using System.Diagnostics;
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

    public bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public string ServiceManagerName => "Windows Service";

    public async Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default)
    {
        var (exitCode, _) = await RunScAsync($"query {ServiceName}", cancellationToken);
        return exitCode == 0;
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
        var (createExit, createOutput) = await RunScAsync(
            $"create {ServiceName} binPath= \"{binPath}\" start= delayed-auto DisplayName= \"{DisplayName}\"",
            cancellationToken);

        if (createExit != 0)
            return new ServiceOperationResult(false, $"Failed to create service: {createOutput}");

        // Set description
        await RunScAsync($"description {ServiceName} \"{Description}\"", cancellationToken);

        // Set environment variable for BotNexus home
        // Windows services get environment from the system; we set via registry
        await SetServiceEnvironmentAsync(homePath, port, cancellationToken);

        // Configure failure recovery: restart on first three failures
        await RunScAsync($"failure {ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000", cancellationToken);

        // Start the service
        var (startExit, startOutput) = await RunScAsync($"start {ServiceName}", cancellationToken);
        if (startExit != 0)
            return new ServiceOperationResult(true, $"Service installed but failed to start: {startOutput}. Start manually with 'sc start {ServiceName}'.");

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
        var (deleteExit, deleteOutput) = await RunScAsync($"delete {ServiceName}", cancellationToken);
        if (deleteExit != 0)
            return new ServiceOperationResult(false, $"Failed to delete service: {deleteOutput}");

        return new ServiceOperationResult(true, $"Service '{ServiceName}' stopped and removed.");
    }

    private static async Task SetServiceEnvironmentAsync(string homePath, int port, CancellationToken cancellationToken)
    {
        // Set machine-level environment variables that the service will pick up
        // Using reg.exe to set under the service's registry key
        var regKey = $@"HKLM\SYSTEM\CurrentControlSet\Services\{ServiceName}";
        var envValue = $"BOTNEXUS_HOME={homePath}\0ASPNETCORE_URLS=http://localhost:{port}\0";

        var psi = new ProcessStartInfo
        {
            FileName = "reg",
            Arguments = $"add \"{regKey}\" /v Environment /t REG_MULTI_SZ /d \"{envValue}\" /f",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process != null)
            await process.WaitForExitAsync(cancellationToken);
    }

    private static async Task<(int ExitCode, string Output)> RunScAsync(string arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start sc.exe");

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, string.IsNullOrWhiteSpace(output) ? error : output);
    }

    private static string GetDotnetPath()
    {
        var dotnetPath = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetPath))
            return Path.Combine(dotnetPath, "dotnet.exe");

        // Fallback to PATH resolution
        return "dotnet";
    }
}
