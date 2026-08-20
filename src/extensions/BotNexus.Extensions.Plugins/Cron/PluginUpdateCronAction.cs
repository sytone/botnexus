using BotNexus.Cron;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Plugins.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Plugins.Cron;

/// <summary>
/// Updates every installed plugin whose update preference is enabled. Action type:
/// <c>plugin-update</c> (#2683, slice 4 of the plugin epic #2623).
/// </summary>
/// <remarks>
/// <para>
/// <b>Agentless, like <c>command</c>.</b> The action does everything itself: it bonds no session,
/// resolves no model, and never calls <c>RecordCost</c> or <c>RecordToolInvocationCount</c>. Those
/// counters therefore stay null - the platform's "not measured" reading - rather than being
/// stamped with a zero that would rank a job with no model concept as the cheapest agent job on
/// the platform. An agent turn here would also be actively harmful: updating plugins is a
/// deterministic mechanical operation, and routing it through a model would make it
/// non-deterministic and chargeable for no gain.
/// </para>
/// <para>
/// <b>Why a schedule and not a startup check.</b> Claude Code and Copilot check plugins when their
/// framework starts because their host is a short-lived CLI. BotNexus is an always-up gateway, so
/// a startup check means never checking in production while passing every test - tests restart the
/// process. The schedule is the trigger precisely because it is the one thing a long-lived process
/// still experiences.
/// </para>
/// <para>
/// <b>One plugin's failure is not the run's failure.</b> Each plugin is updated inside its own
/// try/catch and the run continues. Aborting on the first fault would let a single unreachable
/// source silently freeze every other plugin on the gateway at its installed revision - the
/// failure would be visible for the broken plugin and invisible for all the healthy ones. The run
/// throws only when EVERY enabled plugin failed, because at that point "some plugins are stale" is
/// the honest summary and there is no partial success to protect.
/// </para>
/// </remarks>
public sealed class PluginUpdateCronAction : ICronAction
{
    /// <summary>The registered cron action type string. Referenced by the provisioner so the two cannot drift.</summary>
    public const string TypeName = "plugin-update";

    /// <inheritdoc/>
    public string ActionType => TypeName;

    /// <inheritdoc/>
    public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var logger = context.Services.GetService<ILogger<PluginUpdateCronAction>>();

        // Fail loudly rather than recording a successful run that updated nothing. A missing
        // service means the plugin extension is not composed, and a green run would assert that
        // every plugin is current when in fact none was even looked at.
        var updater = context.Services.GetService<IPluginUpdateService>()
            ?? throw new InvalidOperationException(
                $"Cron job '{context.Job.Id}' has action type '{TypeName}' but no {nameof(IPluginUpdateService)} is registered.");

        // The preference is read HERE, before any work, so a pinned plugin's source is never even
        // fetched. Filtering after the fetch would cost a clone per run to reach a foregone
        // conclusion, and would make "pinned" observationally identical to "already current".
        var candidates = updater.List().Where(plugin => plugin.UpdatesEnabled).ToList();
        if (candidates.Count == 0)
        {
            logger?.LogInformation("Plugin update run found no plugins with updates enabled.");
            return;
        }

        var updated = 0;
        var unchanged = 0;
        var failures = new List<string>();

        foreach (var plugin in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await updater.UpdateAsync(plugin.Name, cancellationToken).ConfigureAwait(false);

                if (!result.IsSuccess)
                {
                    var reason = result.Errors.Count == 0
                        ? "the update failed without reporting a reason"
                        : string.Join("; ", result.Errors.Select(e => $"{e.Field}: {e.Message}"));
                    failures.Add($"{plugin.Name} ({reason})");
                    logger?.LogWarning("Plugin {Plugin} failed to update: {Reason}", plugin.Name, reason);
                    continue;
                }

                if (result.Outcome == PluginOperationOutcome.Updated)
                {
                    updated++;
                    logger?.LogInformation(
                        "Updated plugin {Plugin} from {From} to {To}.",
                        plugin.Name, result.PreviousVersion, result.Plugin?.ResolvedVersion);
                }
                else
                {
                    unchanged++;
                }
            }
            // A host shutdown is not a plugin failure and must not be recorded as one, nor
            // absorbed: swallowing it would turn every gateway restart into a spurious partial
            // failure report and rob the scheduler of the cancellation it needs.
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"{plugin.Name} ({ex.Message})");
                logger?.LogWarning(ex, "Plugin {Plugin} failed to update.", plugin.Name);
            }
        }

        logger?.LogInformation(
            "Plugin update run complete: {Updated} updated, {Unchanged} already current, {Failed} failed of {Total} considered.",
            updated, unchanged, failures.Count, candidates.Count);

        // Total failure is a failed run; a partial failure is not. If even one plugin updated or
        // was confirmed current, the run did real work and marking it error would bury that.
        if (failures.Count == candidates.Count)
        {
            throw new InvalidOperationException(
                $"Every plugin update failed ({failures.Count} of {candidates.Count}): " + string.Join(", ", failures));
        }
    }
}
