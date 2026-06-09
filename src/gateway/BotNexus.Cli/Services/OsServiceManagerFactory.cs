using System.Runtime.InteropServices;

namespace BotNexus.Cli.Services;

/// <summary>
/// Resolves the platform-appropriate <see cref="IOsServiceManager"/> implementation.
/// </summary>
internal static class OsServiceManagerFactory
{
    /// <summary>
    /// Returns the service manager for the current OS, or null if unsupported.
    /// </summary>
    public static IOsServiceManager? Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WindowsServiceManager();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new SystemdServiceManager();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new LaunchdServiceManager();
        return null;
    }
}
