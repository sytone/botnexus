using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Cron.Tests.TestInfrastructure;
using BotNexus.Cron.Tools;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #2838: the agent-facing <c>cron</c> tool had no write path for
/// <see cref="CronJob.FailureAlertsEnabled"/> / <see cref="CronJob.FailureAlertConversationId"/>,
/// so alerting - fully implemented in the domain, store, scheduler and REST API - was unreachable
/// from the only surface agents have. These tests drive the TOOL's write path (never a hand-built
/// <see cref="CronJob"/>), because a test that constructs the job directly cannot detect the gap
/// this issue is about.
/// </summary>
public sealed class CronToolFailureAlertSurfaceTests
{
    private const string AlertConversationId = "c_alert";

    /// <summary>AC1: create accepts both fields and a subsequent list shows the persisted values.</summary>
    [Fact]
    public async Task Create_WithAlertFields_PersistsThemAndListShowsThem()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var tool = CreateTool(context.Store);

        var created = await InvokeAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "alerting job",
            ["schedule"] = "*/5 * * * *",
            ["message"] = "do the thing",
            ["failureAlertsEnabled"] = true,
            ["failureAlertConversationId"] = AlertConversationId
        });

        created.GetProperty("failureAlertsEnabled").GetBoolean().ShouldBeTrue();
        created.GetProperty("failureAlertConversationId").GetString()
            .ShouldBe(AlertConversationId);

        var listed = await InvokeAsync(tool, new Dictionary<string, object?> { ["action"] = "list" });
        var job = listed.EnumerateArray().ShouldHaveSingleItem();
        job.GetProperty("failureAlertsEnabled").GetBoolean().ShouldBeTrue();
        job.GetProperty("failureAlertConversationId").GetString()
            .ShouldBe(AlertConversationId);
    }

    /// <summary>AC2: update accepts both fields and a re-read returns the new values.</summary>
    [Fact]
    public async Task Update_WithAlertFields_PersistsThemAndReReadReturnsThem()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var tool = CreateTool(context.Store);
        var jobId = await CreateBasicJobAsync(tool);

        var updated = await InvokeAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = jobId,
            ["failureAlertsEnabled"] = true,
            ["failureAlertConversationId"] = AlertConversationId
        });

        updated.GetProperty("failureAlertsEnabled").GetBoolean().ShouldBeTrue();

        var reloaded = (await context.Store.GetAsync(JobId.From(jobId)))!;
        reloaded.FailureAlertsEnabled.ShouldBeTrue();
        reloaded.FailureAlertConversationId!.Value.Value.ShouldBe(AlertConversationId);
    }

    /// <summary>
    /// AC3: an update that omits both fields leaves the stored values alone. This is the #2634
    /// lifecycle-field convention - an unrelated edit must never silently un-alert a job.
    /// </summary>
    [Fact]
    public async Task Update_OmittingAlertFields_LeavesStoredValuesUnchanged()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var tool = CreateTool(context.Store);
        var jobId = await CreateBasicJobAsync(tool);

        await InvokeAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = jobId,
            ["failureAlertsEnabled"] = true,
            ["failureAlertConversationId"] = AlertConversationId
        });

        // An entirely unrelated edit.
        await InvokeAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = jobId,
            ["name"] = "renamed"
        });

        var reloaded = (await context.Store.GetAsync(JobId.From(jobId)))!;
        reloaded.Name.ShouldBe("renamed");
        reloaded.FailureAlertsEnabled.ShouldBeTrue();
        reloaded.FailureAlertConversationId!.Value.Value.ShouldBe(AlertConversationId);
    }

    /// <summary>
    /// AC2 (clearing half): an explicit empty string clears the target, mirroring the
    /// <c>expiresAt</c> spelling. Omission-preserves must not make a field un-clearable.
    /// </summary>
    [Fact]
    public async Task Update_WithEmptyAlertConversationId_ClearsTheTarget()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var tool = CreateTool(context.Store);
        var jobId = await CreateBasicJobAsync(tool);

        await InvokeAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = jobId,
            ["failureAlertConversationId"] = AlertConversationId
        });

        await InvokeAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = jobId,
            ["failureAlertConversationId"] = string.Empty
        });

        (await context.Store.GetAsync(JobId.From(jobId)))!.FailureAlertConversationId.ShouldBeNull();
    }

    /// <summary>
    /// AC4 (sad path): an unresolvable target is rejected at the tool seam by the SHARED
    /// <see cref="CronAlertTarget.ValidateAsync"/>, and the rejection text is that validator's -
    /// pinning that no second validation spelling was introduced.
    /// </summary>
    [Fact]
    public async Task Create_WithUnresolvableAlertConversation_IsRejectedByTheSharedValidator()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var tool = CreateTool(context.Store, resolver: new StubResolver(exists: false));

        var error = await Should.ThrowAsync<ArgumentException>(() => InvokeAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "bad target",
            ["schedule"] = "*/5 * * * *",
            ["message"] = "do the thing",
            ["failureAlertConversationId"] = "c_missing"
        }));

        error.Message.ShouldContain(CronAlertTarget.UnresolvableMessage("c_missing"));
        (await context.Store.ListAsync()).ShouldBeEmpty();
    }

    /// <summary>
    /// AC4 (fail-closed half): with no resolver registered a supplied target cannot be verified,
    /// so the write is refused rather than storing an alert target that may never deliver.
    /// </summary>
    [Fact]
    public async Task Update_WithNoResolverAvailable_FailsClosed()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var tool = CreateTool(context.Store, useDefaultResolver: false);
        var jobId = await CreateBasicJobAsync(tool);

        var error = await Should.ThrowAsync<ArgumentException>(() => InvokeAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = jobId,
            ["failureAlertConversationId"] = "c_unverifiable"
        }));

        error.Message.ShouldContain(CronAlertTarget.UnverifiableMessage("c_unverifiable"));
        (await context.Store.GetAsync(JobId.From(jobId)))!.FailureAlertConversationId.ShouldBeNull();
    }

    /// <summary>
    /// AC5: a run that terminates as <see cref="CronRunStatus.Error"/> on a job configured
    /// THROUGH THE TOOL delivers a <see cref="CronFailureAlert"/> to the configured conversation.
    /// The job is never constructed directly - that is the whole point, because the pre-#2838
    /// defect was invisible to any test that did.
    /// </summary>
    [Fact]
    public async Task ErrorRun_OnToolConfiguredJob_DeliversFailureAlert()
    {
        await using var context = await CronStoreTestContext.CreateAsync();
        var tool = CreateTool(context.Store);

        var created = await InvokeAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "alerting job",
            ["schedule"] = "*/5 * * * *",
            ["actionType"] = "agent-prompt",
            ["message"] = "do the thing",
            ["failureAlertsEnabled"] = true,
            ["failureAlertConversationId"] = AlertConversationId
        });
        var jobId = created.GetProperty("id").GetString()!;

        // The stored action type has to match the throwing action the scheduler will resolve.
        var stored = (await context.Store.GetAsync(JobId.From(jobId)))!;
        await context.Store.UpdateDefinitionAsync(stored with { ActionType = "boom" });

        var sink = new RecordingAlertSink();
        var scheduler = CreateScheduler(context.Store, [new ThrowingAction("boom")], sink);
        await scheduler.RunNowAsync(JobId.From(jobId));

        var captured = sink.Alerts.ShouldHaveSingleItem();
        captured.ConversationId.Value.ShouldBe(AlertConversationId);
        captured.Alert.JobId.Value.ShouldBe(jobId);
    }

    /// <summary>
    /// AC1/AC2 schema half: the model cannot supply a parameter the schema never declares, so the
    /// declaration is part of the fix rather than cosmetic.
    /// </summary>
    [Fact]
    public void Definition_DeclaresBothAlertParameters()
    {
        var tool = CreateTool(new Mock<ICronStore>().Object);
        var properties = tool.Definition.Parameters.GetProperty("properties");

        properties.TryGetProperty("failureAlertsEnabled", out var enabled).ShouldBeTrue();
        enabled.GetProperty("type").GetString().ShouldBe("boolean");
        properties.TryGetProperty("failureAlertConversationId", out var target).ShouldBeTrue();
        target.GetProperty("type").GetString().ShouldBe("string");
    }

    // --- helpers ---

    private static async Task<string> CreateBasicJobAsync(CronTool tool)
    {
        var created = await InvokeAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "plain job",
            ["schedule"] = "*/5 * * * *",
            ["message"] = "do the thing"
        });
        return created.GetProperty("id").GetString()!;
    }

    internal static CronTool CreateTool(
        ICronStore store,
        ICronAlertTargetResolver? resolver = null,
        bool useDefaultResolver = true)
        => new(
            store,
            CreateScheduler(store, []),
            AgentId.From("agent-a"),
            allowCrossAgentCron: true,
            alertTargetResolver: resolver ?? (useDefaultResolver ? new StubResolver(exists: true) : null));

    internal static async Task<JsonElement> InvokeAsync(CronTool tool, IReadOnlyDictionary<string, object?> arguments)
    {
        var prepared = await tool.PrepareArgumentsAsync(arguments);
        var result = await tool.ExecuteAsync("call-1", prepared);
        var text = result.Content[0].Value;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    internal static CronScheduler CreateScheduler(
        ICronStore store,
        IEnumerable<ICronAction> actions,
        ICronFailureAlertSink? sink = null)
    {
        var services = new ServiceCollection();
        if (sink is not null)
            services.AddSingleton(sink);
        services.AddSingleton<ISecretRedactor>(new PassthroughRedactor());
        var provider = services.BuildServiceProvider();

        return new CronScheduler(
            store,
            actions,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<CronOptions>(new CronOptions { Enabled = true, TickIntervalSeconds = 1, DefaultJobTimeoutSeconds = 600 }),
            NullLogger<CronScheduler>.Instance);
    }

    private sealed class ThrowingAction(string actionType) : ICronAction
    {
        public string ActionType => actionType;

        public Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");
    }

    internal sealed class StubResolver(bool exists) : ICronAlertTargetResolver
    {
        public Task<bool> ExistsAsync(ConversationId conversationId, CancellationToken ct = default)
            => Task.FromResult(exists);
    }

    internal sealed record CapturedAlert(ConversationId ConversationId, CronFailureAlert Alert);

    internal sealed class RecordingAlertSink : ICronFailureAlertSink
    {
        private readonly List<CapturedAlert> _alerts = [];

        public IReadOnlyList<CapturedAlert> Alerts
        {
            get { lock (_alerts) { return _alerts.ToList(); } }
        }

        public Task SendAsync(ConversationId conversationId, CronFailureAlert alert, CancellationToken ct = default)
        {
            lock (_alerts) { _alerts.Add(new CapturedAlert(conversationId, alert)); }
            return Task.CompletedTask;
        }
    }

    private sealed class PassthroughRedactor : ISecretRedactor
    {
        public string Redact(string input) => input;
        public string RedactForExternalDelivery(string input) => input;
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue => currentValue;
        public T Get(string? name) => currentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
