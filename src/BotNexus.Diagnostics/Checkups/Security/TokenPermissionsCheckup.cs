using System.Security.AccessControl;
using System.Security.Principal;
using BotNexus.Core.Abstractions;

namespace BotNexus.Diagnostics.Checkups.Security;

public sealed class TokenPermissionsCheckup(DiagnosticsPaths paths) : IHealthCheckup
{
    private readonly DiagnosticsPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public string Name => "TokenPermissions";
    public string Category => "Security";
    public string Description => "Checks token directory permissions are not world-readable.";

    public Task<CheckupResult> RunAsync(CancellationToken ct = default)
    {
        try
        {
            var tokensPath = _paths.TokensPath;
            if (!Directory.Exists(tokensPath))
            {
                return Task.FromResult(new CheckupResult(
                    CheckupStatus.Warn,
                    $"Token directory '{tokensPath}' does not exist.",
                    "Create ~/.botnexus/tokens and restrict access to the current user."));
            }

            return OperatingSystem.IsWindows()
                ? Task.FromResult(CheckWindowsAcl(tokensPath))
                : Task.FromResult(CheckUnixPermissions(tokensPath));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new CheckupResult(
                CheckupStatus.Fail,
                $"Failed to validate token directory permissions: {ex.Message}",
                "Verify ~/.botnexus/tokens permissions so only trusted users can read tokens."));
        }
    }

    private static CheckupResult CheckWindowsAcl(string tokensPath)
    {
        var security = Directory.GetAccessControl(tokensPath);
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow);

        foreach (var rule in rules)
        {
            if (rule.IdentityReference is not SecurityIdentifier sid || !sid.IsWellKnown(WellKnownSidType.WorldSid))
                continue;

            var worldReadRights = FileSystemRights.ReadData
                                  | FileSystemRights.ListDirectory
                                  | FileSystemRights.Read
                                  | FileSystemRights.ReadAndExecute
                                  | FileSystemRights.FullControl
                                  | FileSystemRights.Modify;

            if ((rule.FileSystemRights & worldReadRights) != 0)
            {
                return new CheckupResult(
                    CheckupStatus.Fail,
                    "Token directory grants read access to Everyone on Windows.",
                    "Remove Everyone read access from ~/.botnexus/tokens ACL (grant only current user/admin as needed).");
            }
        }

        return new CheckupResult(CheckupStatus.Pass, "Token directory ACL does not expose tokens to Everyone.");
    }

    private static CheckupResult CheckUnixPermissions(string tokensPath)
    {
        var mode = File.GetUnixFileMode(tokensPath);
        var worldMask = UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((mode & worldMask) == worldMask)
        {
            return new CheckupResult(
                CheckupStatus.Fail,
                $"Token directory permissions are too open ({mode}).",
                "Run chmod 700 ~/.botnexus/tokens to remove world access.");
        }

        return new CheckupResult(CheckupStatus.Pass, $"Token directory permissions are acceptable ({mode}).");
    }
}
