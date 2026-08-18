using BotNexus.Cron;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Plugins.Cron;
using BotNexus.Extensions.Plugins.Lifecycle;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Plugins.Tests;

/// <summary>
/// Pins the agentless <c>plugin-update</c> cron action (#2683, slice 4 of #2623).
/// </summary>
/// <remarks>
/// The load-bearing tests are the two that pin behaviour a reasonable implementation gets wrong:
/// a pinned plugin must not even be attempted (not merely skipped after a fetch), and one
/// plugin's failure must not deprive the remaining plugins of their update. Both assert the
/// negative directly - which plugins were NOT attempted, and which ones still were - rather than
/// asserting a happy path and inferring the rest.
/// </remarks>
public sealed class PluginUpdateCronActionTests
{
    private static InstalledPlugin Plugin(string name, bool updatesEnabled = true) => new()
    {
        Name = name,
        Source = $"https://example.com/{name}.git",
        ResolvedVersion = "aaaa1111",
        UpdatesEnabled = updatesEnabled,
        InstalledAtUtc = DateTimeOffset.UnixEpoch,
    };

    private static CronExecutionContext Context(IServiceProvider services) => new()
    {
        Job = new CronJob
        {
            Id = JobId.From(PluginUpdateCronProvisioner.PlatformJobId),
            Name = "Plugin Updates",
            Schedule = "0 3 * * *",
            ActionType = PluginUpdateCronAction.TypeName,
            AgentId = null,
        },
        RunId = RunId.Create(),
        TriggeredAt = DateTimeOffset.UnixEpoch,
        TriggerType = CronTriggerType.Scheduled,
        Services = services,
    };

    private static IServiceProvider Services(IPluginUpdateService updater) =>
        new ServiceCollection().AddSingleton(updater).BuildServiceProvider();

    // AC1 - the action is agentless: it runs with no agent turn and reports no model cost.
    [Fact]
    public async Task RunsWithNoAgentTurnAndReportsNoModelCost()
    {
        var updater = new FakePluginUpdateService([Plugin("alpha")]);
        var context = Context(Services(updater));

        await new PluginUpdateCronAction().ExecuteAsync(context);

        // No session was ever bonded, so no agent turn could have occurred...
        Assert.Null(context.SessionId);
        Assert.Null(context.ConversationId);

        // ...and every cost field stays null, which is the platform's "not measured" reading.
        // A zero here would rank this job as the cheapest agent job on the platform rather than
        // as a job that has no model cost concept at all.
        Assert.Null(context.Cost.PromptTokens);
        Assert.Null(context.Cost.CompletionTokens);
        Assert.Null(context.Cost.TurnCount);

        // The action reports no tool count either: null means "no opinion", and a zero would
        // make every execution-class run of this job record no_tool_calls.
        Assert.Null(context.ToolInvocationCount);

        Assert.Equal(["alpha"], updater.Attempted);
    }

    // AC4 - a plugin whose update preference is disabled is never even attempted.
    [Fact]
    public async Task UpdatesOnlyPluginsWhoseUpdatePreferenceIsEnabled()
    {
        var updater = new FakePluginUpdateService(
        [
            Plugin("enabled-one"),
            Plugin("pinned", updatesEnabled: false),
            Plugin("enabled-two"),
        ]);

        await new PluginUpdateCronAction().ExecuteAsync(Context(Services(updater)));

        // The pinned plugin is absent from the attempt list entirely. Asserting only that it was
        // not UPDATED would also pass for an implementation that fetched its source and then
        // discarded the result - a clone per run to reach a foregone conclusion.
        Assert.Equal(["enabled-one", "enabled-two"], updater.Attempted);
        Assert.DoesNotContain("pinned", updater.Attempted);
    }

    // AC5 - one plugin's failure must not abort the run for the others.
    [Fact]
    public async Task OneFailingPluginDoesNotAbortTheRemainingUpdates()
    {
        var updater = new FakePluginUpdateService([Plugin("alpha"), Plugin("boom"), Plugin("zeta")]);
        updater.ThrowFor.Add("boom");

        var context = Context(Services(updater));

        // The run itself does not fault: a partial failure is reported, not thrown, because
        // throwing would record the whole run as an error and hide the two plugins that did update.
        await new PluginUpdateCronAction().ExecuteAsync(context);

        // Every plugin was attempted, INCLUDING the one after the failure. Asserting only that
        // no exception escaped would pass for an implementation that caught the fault and then
        // stopped iterating.
        Assert.Equal(["alpha", "boom", "zeta"], updater.Attempted);
        Assert.Equal(["alpha", "zeta"], updater.Updated);
    }

    // A failure that is a returned result rather than a thrown exception is equally non-fatal.
    [Fact]
    public async Task AFailedResultAlsoDoesNotAbortTheRemainingUpdates()
    {
        var updater = new FakePluginUpdateService([Plugin("alpha"), Plugin("bad"), Plugin("zeta")]);
        updater.FailResultFor.Add("bad");

        await new PluginUpdateCronAction().ExecuteAsync(Context(Services(updater)));

        Assert.Equal(["alpha", "bad", "zeta"], updater.Attempted);
        Assert.Equal(["alpha", "zeta"], updater.Updated);
    }

    // The action must fail loudly when its update service is absent rather than recording a
    // successful run that updated nothing.
    [Fact]
    public async Task ThrowsWhenNoUpdateServiceIsRegistered()
    {
        var context = Context(new ServiceCollection().BuildServiceProvider());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PluginUpdateCronAction().ExecuteAsync(context));
    }

    /// <summary>The registered action type is the string the provisioner writes onto the job.</summary>
    [Fact]
    public void ActionTypeMatchesTheProvisionedJob()
    {
        Assert.Equal("plugin-update", PluginUpdateCronAction.TypeName);
        Assert.Equal(PluginUpdateCronAction.TypeName, new PluginUpdateCronAction().ActionType);
    }

    private sealed class FakePluginUpdateService(IReadOnlyList<InstalledPlugin> installed) : IPluginUpdateService
    {
        public List<string> Attempted { get; } = [];

        public List<string> Updated { get; } = [];

        public HashSet<string> ThrowFor { get; } = new(StringComparer.Ordinal);

        public HashSet<string> FailResultFor { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<InstalledPlugin> List() => installed;

        public Task<PluginOperationResult> UpdateAsync(string name, CancellationToken cancellationToken = default)
        {
            Attempted.Add(name);

            if (ThrowFor.Contains(name))
            {
                throw new IOException($"Simulated transport failure updating '{name}'.");
            }

            if (FailResultFor.Contains(name))
            {
                return Task.FromResult(PluginOperationResult.Failure(name, "source", "Simulated failure."));
            }

            Updated.Add(name);
            return Task.FromResult(new PluginOperationResult
            {
                Outcome = PluginOperationOutcome.Updated,
                Name = name,
            });
        }
    }
}
