namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>How a marketplace offering relates to what is already installed.</summary>
public enum MarketplaceOfferingState
{
    /// <summary>Nothing of that name is installed; the offering can be installed.</summary>
    NotInstalled,

    /// <summary>Installed at the same version the source offers.</summary>
    Installed,

    /// <summary>Installed, and the source offers a NEWER version.</summary>
    UpdateAvailable,

    /// <summary>
    /// Installed, and the source offers an OLDER version - a catalog pinned behind what is
    /// running. Called out separately because presenting a downgrade as an update is the one
    /// mistake this whole comparison exists to avoid.
    /// </summary>
    SourceIsBehind,

    /// <summary>
    /// Installed at a different version, but the two cannot be ordered - one of them is not
    /// dotted-numeric. Says "differs" rather than guessing a direction.
    /// </summary>
    VersionDiffers,
}

/// <summary>
/// Compares an installed plugin's version with the version a marketplace source offers.
/// </summary>
/// <remarks>
/// Both sides are manifest versions - the offering's version is read from the plugin's own
/// manifest at the pinned reference, not from the catalog entry - so this compares like with like.
/// </remarks>
public static class MarketplaceOfferingComparison
{
    /// <summary>
    /// Resolves the state to render for one offering.
    /// </summary>
    /// <param name="installedVersion">Installed manifest version, or <c>null</c> if not installed.</param>
    /// <param name="offeredVersion">Version the source offers, or <c>null</c> when unversioned.</param>
    /// <param name="isInstalled">Whether a plugin of that name is installed at all.</param>
    public static MarketplaceOfferingState Resolve(
        string? installedVersion,
        string? offeredVersion,
        bool isInstalled)
    {
        if (!isInstalled)
        {
            return MarketplaceOfferingState.NotInstalled;
        }

        var installed = (installedVersion ?? string.Empty).Trim();
        var offered = (offeredVersion ?? string.Empty).Trim();

        // Nothing to compare: an unversioned plugin on either side is just "installed". Claiming a
        // difference from a missing value would flag every unversioned plugin forever.
        if (installed.Length == 0 || offered.Length == 0)
        {
            return MarketplaceOfferingState.Installed;
        }

        if (string.Equals(installed, offered, StringComparison.OrdinalIgnoreCase))
        {
            return MarketplaceOfferingState.Installed;
        }

        // null means "cannot be ordered", which is NOT the same as "ordered and equal" - 1.2 and
        // 1.2.0 are the same release written two ways, while 1.2.0 and 1.2.1-beta cannot be ranked
        // at all. Collapsing the two would report an unnecessary difference for the first.
        return CompareDottedNumeric(installed, offered) switch
        {
            null => MarketplaceOfferingState.VersionDiffers,
            < 0 => MarketplaceOfferingState.UpdateAvailable,
            > 0 => MarketplaceOfferingState.SourceIsBehind,
            _ => MarketplaceOfferingState.Installed,
        };
    }

    /// <summary>
    /// Orders two dotted-numeric versions, returning <c>null</c> when they cannot be ordered.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow. A leading <c>v</c> is tolerated because tags carry it, and a missing
    /// trailing segment counts as zero so <c>1.2</c> and <c>1.2.0</c> are the same release. Anything
    /// that is not a run of digits - a pre-release suffix, a date, a commit - makes the pair
    /// unorderable rather than guessing: "differs" is honest, and a wrong direction here is what
    /// would tell someone to install a downgrade.
    /// </remarks>
    private static int? CompareDottedNumeric(string left, string right)
    {
        var a = Segments(left);
        var b = Segments(right);

        if (a is null || b is null)
        {
            return null;
        }

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y)
            {
                return x < y ? -1 : 1;
            }
        }

        return 0;
    }

    private static int[]? Segments(string version)
    {
        var text = version.StartsWith('v') || version.StartsWith('V') ? version[1..] : version;

        if (text.Length == 0)
        {
            return null;
        }

        var parts = text.Split('.');
        var result = new int[parts.Length];

        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out var value) || value < 0)
            {
                return null;
            }

            result[i] = value;
        }

        return result;
    }
}
