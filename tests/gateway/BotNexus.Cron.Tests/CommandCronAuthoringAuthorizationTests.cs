using BotNexus.Agent.Core.Types;
using BotNexus.Cron.Actions;
using BotNexus.Cron.Tools;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #2462 (authoring half of acceptance criterion 2): the firing gate shipped in #2505 stopped a
/// denied command from ever running, but a denied command could still be <b>stored</b> by the
/// <c>cron</c> tool. These tests gate AUTHORING - create/update carrying a <c>shellCommand</c> -
/// through the same <see cref="ICommandCronAuthorizer"/> seam, so the policy vocabulary stays
/// single-sourced on the exec/shell tool boundary.
///
/// The assertions are OBSERVABLE: a denied authoring attempt must leave no row in the store
/// (<c>CreateAsync</c>/<c>UpdateDefinitionAsync</c> never invoked), not merely return a decision.
/// </summary>
public sealed class CommandCronAuthoringAuthorizationTests
{
    // ---------------------------------------------------------------------
    // Create
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Create_CommandJob_WhenAuthorizerAllows_IsPersisted()
    {
        var store = new Mock<ICronStore>();
        CronJob? created = null;
        store.Setup(v => v.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => created = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);
        var tool = CreateTool(store, Allowing());

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "Health check",
            ["schedule"] = "0 * * * *",
            ["actionType"] = "command",
            ["shellCommand"] = "pwsh -NoProfile -File ./check.ps1"
        });

        created.ShouldNotBeNull();
        created!.ShellCommand.ShouldBe("pwsh -NoProfile -File ./check.ps1");
    }

    [Fact]
    public async Task Create_CommandJob_WhenAuthorizerDenies_IsNotPersisted()
    {
        var store = new Mock<ICronStore>();
        var tool = CreateTool(store, Denying());

        var ex = await Should.ThrowAsync<UnauthorizedAccessException>(async () =>
            await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
            {
                ["action"] = "create",
                ["name"] = "Sneaky",
                ["schedule"] = "0 * * * *",
                ["actionType"] = "command",
                ["shellCommand"] = "pwsh -NoProfile -c 'rm -rf /'"
            }));

        ex.Message.ShouldContain("denied");
        store.Verify(s => s.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task Create_CommandJob_WithNoAuthorizer_FailsClosed()
    {
        var store = new Mock<ICronStore>();
        var tool = CreateTool(store, authorizer: null);

        await Should.ThrowAsync<UnauthorizedAccessException>(async () =>
            await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
            {
                ["action"] = "create",
                ["name"] = "Sneaky",
                ["schedule"] = "0 * * * *",
                ["actionType"] = "command",
                ["shellCommand"] = "pwsh -NoProfile -File ./check.ps1"
            }));

        store.Verify(s => s.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task Create_CommandJob_WithUnclassifiableCommand_FailsClosed()
    {
        var store = new Mock<ICronStore>();
        // Maximally permissive policy: the unclassifiable path must deny anyway.
        var tool = CreateTool(store, new ToolPolicyCommandCronAuthorizer(
            new FakeToolPolicyProvider(requiresApproval: false, ToolApprovalFallback.Allow)));

        var ex = await Should.ThrowAsync<UnauthorizedAccessException>(async () =>
            await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
            {
                ["action"] = "create",
                ["name"] = "Sneaky",
                ["schedule"] = "0 * * * *",
                ["actionType"] = "command",
                ["shellCommand"] = "| Write-Output pwned"
            }));

        ex.Message.ShouldContain("unclassifiable");
        store.Verify(s => s.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task Create_AgentPromptJob_IsUnaffectedByTheCommandGate()
    {
        var store = new Mock<ICronStore>();
        CronJob? created = null;
        store.Setup(v => v.CreateAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Callback<CronJob, CancellationToken>((job, _) => created = job)
            .ReturnsAsync((CronJob job, CancellationToken _) => job);
        // Deny-everything authorizer: a command job would be refused outright.
        var tool = CreateTool(store, Denying());

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "Daily summary",
            ["schedule"] = "*/5 * * * *",
            ["message"] = "Summarize status"
        });

        created.ShouldNotBeNull();
        created!.ActionType.ShouldBe("agent-prompt");
    }

    // ---------------------------------------------------------------------
    // Update
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Update_ReplacingShellCommand_WhenDenied_IsNotPersisted()
    {
        var store = new Mock<ICronStore>();
        SetupExisting(store, CreateCommandJob());
        var tool = CreateTool(store, Denying());

        await Should.ThrowAsync<UnauthorizedAccessException>(async () =>
            await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
            {
                ["action"] = "update",
                ["jobId"] = "job-cmd",
                ["shellCommand"] = "./evil.ps1"
            }));

        store.Verify(s => s.UpdateDefinitionAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task Update_SwitchingPromptJobToCommand_WhenDenied_IsNotPersisted()
    {
        var store = new Mock<ICronStore>();
        SetupExisting(store, CreatePromptJob());
        var tool = CreateTool(store, Denying());

        await Should.ThrowAsync<UnauthorizedAccessException>(async () =>
            await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
            {
                ["action"] = "update",
                ["jobId"] = "job-prompt",
                ["actionType"] = "command",
                ["shellCommand"] = "./switched.ps1"
            }));

        store.Verify(s => s.UpdateDefinitionAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task Update_ScheduleOnlyOnCommandJob_IsStillReauthorized_AndAllowedWhenPolicyAllows()
    {
        var store = new Mock<ICronStore>();
        SetupExisting(store, CreateCommandJob());
        var authorizer = new RecordingAuthorizer(allowed: true);
        var tool = CreateTool(store, authorizer);

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-cmd",
            ["schedule"] = "*/30 * * * *"
        });

        // The retained command is re-checked: a policy tightened after creation takes effect on
        // the next edit rather than being grandfathered in.
        authorizer.AuthoringCommands.ShouldContain("./check.ps1");
        store.Verify(s => s.UpdateDefinitionAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task Update_AgentPromptJob_IsUnaffectedByTheCommandGate()
    {
        var store = new Mock<ICronStore>();
        SetupExisting(store, CreatePromptJob());
        var tool = CreateTool(store, Denying());

        await tool.ExecuteAsync("call-1", new Dictionary<string, object?>
        {
            ["action"] = "update",
            ["jobId"] = "job-prompt",
            ["message"] = "Updated prompt"
        });

        store.Verify(s => s.UpdateDefinitionAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()), Times.Once());
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static ICommandCronAuthorizer Allowing() =>
        new ToolPolicyCommandCronAuthorizer(new FakeToolPolicyProvider(false, ToolApprovalFallback.Allow));

    private static ICommandCronAuthorizer Denying() =>
        new ToolPolicyCommandCronAuthorizer(new FakeToolPolicyProvider(true, ToolApprovalFallback.Deny));

    private static CronTool CreateTool(Mock<ICronStore> store, ICommandCronAuthorizer? authorizer) =>
        new(store.Object, CreateScheduler(), AgentId.From("agent-a"), commandAuthorizer: authorizer);

    private static void SetupExisting(Mock<ICronStore> store, CronJob initial)
    {
        store.Setup(s => s.GetAsync(initial.Id, It.IsAny<CancellationToken>())).ReturnsAsync(initial);
        store.Setup(s => s.UpdateDefinitionAsync(It.IsAny<CronJob>(), It.IsAny<CancellationToken>()))
            .Returns<CronJob, CancellationToken>((job, _) => Task.FromResult<CronJob?>(job));
        store.Setup(s => s.SetNextRunAtAsync(It.IsAny<JobId>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private static CronJob CreateCommandJob() => new()
    {
        Id = JobId.From("job-cmd"),
        Name = "Health check",
        Schedule = "0 * * * *",
        ActionType = "command",
        AgentId = AgentId.From("agent-a"),
        ShellCommand = "./check.ps1",
        Enabled = true,
        CreatedBy = "agent-a",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static CronJob CreatePromptJob() => new()
    {
        Id = JobId.From("job-prompt"),
        Name = "Daily summary",
        Schedule = "0 * * * *",
        ActionType = "agent-prompt",
        AgentId = AgentId.From("agent-a"),
        Message = "Summarize status",
        Enabled = true,
        CreatedBy = "agent-a",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static CronScheduler CreateScheduler()
    {
        var scopeFactory = new ServiceCollection().BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        return new CronScheduler(
            new Mock<ICronStore>().Object,
            Array.Empty<ICronAction>(),
            scopeFactory,
            new StaticOptionsMonitor<CronOptions>(new CronOptions()),
            NullLogger<CronScheduler>.Instance);
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FakeToolPolicyProvider(bool requiresApproval, ToolApprovalFallback fallback)
        : IToolPolicyProvider
    {
        public ToolRiskLevel GetRiskLevel(string toolName) =>
            requiresApproval ? ToolRiskLevel.Dangerous : ToolRiskLevel.Safe;

        public bool RequiresApproval(string toolName, string? agentId = null) => requiresApproval;

        public ToolApprovalFallback GetApprovalFallback(string toolName, string? agentId = null) => fallback;

        public IReadOnlyList<string> GetDeniedForHttp() => [];
    }

    private sealed class RecordingAuthorizer(bool allowed) : ICommandCronAuthorizer
    {
        public List<string> AuthoringCommands { get; } = [];

        public CommandAuthorizationDecision AuthorizeFiring(CronJob job, string command) =>
            allowed ? CommandAuthorizationDecision.Allow("test") : CommandAuthorizationDecision.Deny("test");

        public CommandAuthorizationDecision AuthorizeAuthoring(CronJob job, string command)
        {
            AuthoringCommands.Add(command);
            return allowed ? CommandAuthorizationDecision.Allow("test") : CommandAuthorizationDecision.Deny("test");
        }
    }
}
