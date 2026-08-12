using System.IO.Abstractions;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Cli.Commands.Doctor;

/// <summary>
/// Aggregate-suite check that reports which <em>world</em> this process believes it is in: the
/// resolved world ID alongside the resolved home path (#2834, acceptance criterion 6).
/// </summary>
/// <remarks>
/// <para>An operator running several gateways on one machine - a dev home, a test home and the live
/// home - previously had no way to tell them apart at a glance. #2819 happened because a wrongly
/// opened store had no identity to compare against and the opening process had no identity to
/// assert. Printing the pair (world ID, home path) together is the cheapest possible answer to
/// "which world is this?".</para>
/// <para>The ID is read through <see cref="WorldIdResolver"/> - the same single derivation the
/// gateway injects - and this check <b>never writes</b>. A home that has not started yet legitimately
/// has no ID, which is reported as a warning rather than an error: it is created on next start.</para>
/// </remarks>
internal sealed class WorldIdCheck : IDoctorCheck
{
    private readonly IFileSystem _fileSystem;

    public WorldIdCheck(IFileSystem? fileSystem = null)
        => _fileSystem = fileSystem ?? new FileSystem();

    public string Id => "world-identity";

    public string Title => "World identity";

    public Task<DoctorCheckResult> RunAsync(DoctorCheckContext context, CancellationToken cancellationToken)
    {
        var homeLine = $"Home: {context.HomePath}";

        if (!_fileSystem.File.Exists(context.ConfigPath))
        {
            return Task.FromResult(DoctorCheckResult.Error(
                "config.json not found",
                homeLine,
                $"Expected at {context.ConfigPath}. Run 'botnexus init' first."));
        }

        var identity = WorldIdResolver.TryRead(context.ConfigPath, _fileSystem);

        if (identity is null)
        {
            return Task.FromResult(DoctorCheckResult.Warning(
                "no world ID yet",
                homeLine,
                "World ID: (none) - one is generated and persisted on the next gateway start."));
        }

        return Task.FromResult(new DoctorCheckResult(
            DoctorOutcome.Healthy,
            $"world {identity.Value}",
            [homeLine, $"World ID: {identity.Value}"]));
    }
}
