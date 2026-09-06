using System.Collections.Concurrent;
using System.IO.Abstractions;
using BotNexus.Agent.Core.Tools;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Agents;

/// <summary>Exercises production spawn, workspace storage and tool construction without an LLM.</summary>
public sealed class SubAgentWorkspaceProvisioningTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Spawn_FirstCommandUsesOwnedCwd_AndTerminalCleanupPreservesParent(bool shareWorkspace)
    {
        await using var fixture = new SpawnFixture();
        var spawned = await fixture.SpawnAsync(shareWorkspace);
        await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        fixture.ExistsAtHandleCreation.ShouldBeTrue();
        fixture.Workspace.ShouldNotBeNull();
        var workspace = fixture.Workspace;
        Directory.Exists(workspace).ShouldBeTrue();
        Directory.EnumerateFileSystemEntries(workspace).ShouldBeEmpty();
        fixture.Registry.Object.Contains(AgentId.From(spawned.ChildAgentId.ShouldNotBeNull())).ShouldBeTrue();
        (await fixture.Manager.GetAsync(spawned.SubAgentId)).ShouldNotBeNull().Status.ShouldBe(SubAgentStatus.Running);
        fixture.Audits.ShouldBeEmpty();

        var descriptor = fixture.ChildDescriptor.ShouldNotBeNull();
        if (shareWorkspace)
        {
            descriptor.FileAccess.ShouldNotBeNull().AllowedReadPaths.ShouldContain(fixture.ParentWorkspace);
            descriptor.FileAccess.ShouldNotBeNull().AllowedWritePaths.ShouldContain(fixture.ParentWorkspace);
        }
        else
        {
            descriptor.FileAccess.ShouldBeNull();
        }

        // No write/memory call has occurred in the child. The production shell must start in its cwd.
        var shell = fixture.Tools.ShouldNotBeNull().Single(tool => tool.Name is "shell" or "bash");
        var result = await shell.ExecuteAsync("first-cwd", new Dictionary<string, object?>
        {
            ["command"] = OperatingSystem.IsWindows() ? "(Get-Location).Path" : "pwd",
            ["timeout"] = 15
        });
        var output = string.Join("\n", result.Content.Select(item => item.Value));
        output.ShouldContain(workspace);
        Directory.EnumerateFileSystemEntries(workspace).ShouldBeEmpty();

        fixture.Release.TrySetResult();
        await fixture.WaitForCleanupAsync();
        Directory.Exists(workspace).ShouldBeFalse();
        fixture.Workspaces.GetWorkspacePath(spawned.ChildAgentId.ShouldNotBeNull()).ShouldBe(workspace);
        Directory.Exists(workspace).ShouldBeFalse("path resolution must not recreate terminal workspaces");
        fixture.Audits.Count.ShouldBe(1);
        File.ReadAllText(Path.Combine(fixture.ParentWorkspace, "private.txt")).ShouldBe("parent-private");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalCleanup_EmitsAuditOnlyForActualRemoval(bool removeBeforeCompletion)
    {
        await using var fixture = new SpawnFixture();
        await fixture.SpawnAsync();
        await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var workspace = fixture.Workspace.ShouldNotBeNull();
        // Isolate the audit contract from the independent provisioning regression on the RED tree.
        Directory.CreateDirectory(workspace);
        if (removeBeforeCompletion)
            Directory.Delete(Path.GetDirectoryName(workspace).ShouldNotBeNull(), recursive: true);

        fixture.Release.TrySetResult();
        await fixture.WaitForCleanupAsync();

        Directory.Exists(workspace).ShouldBeFalse();
        fixture.Audits.Count.ShouldBe(removeBeforeCompletion ? 0 : 1);
    }

    [Fact]
    public async Task Kill_RemovesWorkspaceOnce_AndRetainsParentFiles()
    {
        await using var fixture = new SpawnFixture();
        var spawned = await fixture.SpawnAsync();
        await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var workspace = fixture.Workspace.ShouldNotBeNull();
        Directory.Exists(workspace).ShouldBeTrue();

        (await fixture.Manager.KillAsync(spawned.SubAgentId, SpawnFixture.ParentSession)).ShouldBeTrue();
        await fixture.WaitForCleanupAsync();
        Directory.Exists(workspace).ShouldBeFalse();
        fixture.Audits.Count.ShouldBe(1);
        fixture.Audits.Single().ShouldContain(nameof(SubAgentStatus.Killed));
        File.Exists(Path.Combine(fixture.ParentWorkspace, "private.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task Kill_PublishesDispositionBeforeCancellation_AndRejectsSecondKill()
    {
        await using var fixture = new SpawnFixture();
        var spawned = await fixture.SpawnAsync();
        await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        (await fixture.Manager.KillAsync(spawned.SubAgentId, SpawnFixture.ParentSession)).ShouldBeTrue();
        var observed = await fixture.CancellationSnapshot.ShouldNotBeNull();
        observed.ShouldNotBeNull().Status.ShouldBe(SubAgentStatus.Killed);
        (await fixture.Manager.KillAsync(spawned.SubAgentId, SpawnFixture.ParentSession)).ShouldBeFalse();
        fixture.Audits.Count.ShouldBe(1);
        fixture.Audits.Single().ShouldContain(nameof(SubAgentStatus.Killed));
    }

    [Fact]
    public async Task Kill_AfterCompletion_DoesNotOverwriteWinningDisposition()
    {
        await using var fixture = new SpawnFixture();
        var spawned = await fixture.SpawnAsync();
        await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        fixture.Release.TrySetResult();
        await fixture.WaitForCleanupAsync();

        (await fixture.Manager.KillAsync(spawned.SubAgentId, SpawnFixture.ParentSession)).ShouldBeFalse();
        (await fixture.Manager.GetAsync(spawned.SubAgentId)).ShouldNotBeNull().Status.ShouldBe(SubAgentStatus.Completed);
        fixture.Audits.Count.ShouldBe(1);
        fixture.Audits.Single().ShouldContain(nameof(SubAgentStatus.Completed));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Kill_ThrowingChildCallback_DisposesAndCleansExactlyOnce(bool raceCompletion, bool workspaceAlreadyAbsent)
    {
        await using var fixture = new SpawnFixture(throwOnCancellation: true, raceCompletion: raceCompletion);
        var spawned = await fixture.SpawnAsync();
        await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var child = AgentId.From(spawned.ChildAgentId.ShouldNotBeNull());
        var workspace = fixture.Workspace.ShouldNotBeNull();
        Directory.Exists(workspace).ShouldBeTrue();
        if (workspaceAlreadyAbsent)
            Directory.Delete(Path.GetDirectoryName(workspace).ShouldNotBeNull(), recursive: true);

        (await fixture.Manager.KillAsync(spawned.SubAgentId, SpawnFixture.ParentSession)).ShouldBeTrue();
        await fixture.WaitForCleanupAsync();
        if (fixture.CompletionRace is { } race)
            await race;
        await fixture.Manager.OnCompletedAsync(spawned.SubAgentId, "late completion");

        fixture.ThrowingCallbackCount.ShouldBe(1);
        fixture.StopCount.ShouldBe(1);
        fixture.Registry.Verify(r => r.Unregister(child), Times.Once);
        fixture.Registry.Object.Contains(child).ShouldBeFalse();
        Directory.Exists(workspace).ShouldBeFalse();
        fixture.Manager.IsRetiredForTest(spawned.SubAgentId).ShouldBeTrue();
        (await fixture.Manager.GetAsync(spawned.SubAgentId)).ShouldNotBeNull().Status.ShouldBe(SubAgentStatus.Killed);
        Should.Throw<ObjectDisposedException>(() => { _ = fixture.ChildToken.WaitHandle; });
        fixture.Warnings.ShouldContain(w => w.Contains("cancellation", StringComparison.OrdinalIgnoreCase)
            && w.Contains("callback exploded", StringComparison.Ordinal));
        fixture.Audits.Count.ShouldBe(workspaceAlreadyAbsent ? 0 : 1);
        if (!workspaceAlreadyAbsent)
            fixture.Audits.Single().ShouldContain(nameof(SubAgentStatus.Killed));
        (await fixture.Manager.KillAsync(spawned.SubAgentId, SpawnFixture.ParentSession)).ShouldBeFalse();
        fixture.StopCount.ShouldBe(1);
        fixture.Registry.Verify(r => r.Unregister(child), Times.Once);
    }

    private sealed class SpawnFixture : IAsyncDisposable
    {
        internal static readonly SessionId ParentSession = SessionId.From("provision-parent-session");
        private static readonly AgentId Parent = AgentId.From("provision-parent");
        private readonly string _root = Path.Combine(Path.GetTempPath(), "bnx-provision-tests", Guid.NewGuid().ToString("N"));
        private readonly ConcurrentDictionary<AgentId, AgentDescriptor> _descriptors = new();
        private readonly TaskCompletionSource _unregistered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _subAgentId;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<string> Audits { get; } = new();
        internal Mock<IAgentRegistry> Registry { get; } = new();
        internal DefaultSubAgentManager Manager { get; }
        internal FileAgentWorkspaceManager Workspaces { get; }
        internal string ParentWorkspace { get; }
        internal string? Workspace { get; private set; }
        internal bool ExistsAtHandleCreation { get; private set; }
        internal AgentDescriptor? ChildDescriptor { get; private set; }
        internal IReadOnlyList<IAgentTool>? Tools { get; private set; }
        internal Task<SubAgentInfo?>? CancellationSnapshot { get; private set; }

        internal CancellationToken ChildToken { get; private set; }
        internal int ThrowingCallbackCount;
        internal int StopCount;
        internal Task? CompletionRace { get; private set; }
        internal ConcurrentQueue<string> Warnings { get; } = new();

        internal SpawnFixture(bool throwOnCancellation = false, bool raceCompletion = false)
        {
            var fileSystem = new FileSystem();
            Workspaces = new FileAgentWorkspaceManager(new BotNexusHome(fileSystem, Path.Combine(_root, "home")), fileSystem,
                Options.Create(new SubAgentOptions { WorkspaceRoot = Path.Combine(_root, "children") }));
            ParentWorkspace = Workspaces.GetWorkspacePath(Parent.Value);
            File.WriteAllText(Path.Combine(ParentWorkspace, "private.txt"), "parent-private");
            _descriptors[Parent] = new AgentDescriptor { AgentId = Parent, DisplayName = "Parent", ModelId = "test", ApiProvider = "test" };
            Registry.Setup(r => r.Get(It.IsAny<AgentId>())).Returns<AgentId>(id => _descriptors.GetValueOrDefault(id));
            Registry.Setup(r => r.Contains(It.IsAny<AgentId>())).Returns<AgentId>(_descriptors.ContainsKey);
            Registry.Setup(r => r.Register(It.IsAny<AgentDescriptor>())).Callback<AgentDescriptor>(d => _descriptors[d.AgentId] = d);
            Registry.Setup(r => r.Unregister(It.IsAny<AgentId>())).Callback<AgentId>(id =>
            {
                _descriptors.TryRemove(id, out _);
                _unregistered.TrySetResult();
            });
            var handle = new Mock<IAgentHandle>();
            handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>(async (_, ct) =>
                {
                    // Register the observer after WaitAsync's cancellation callback so it captures
                    // the state synchronously before cancellation can unwind and dispose it.
                    ChildToken = ct;
                    var waitForRelease = Release.Task.WaitAsync(ct);
                    using var registration = ct.Register(() =>
                    {
                        if (_subAgentId is not null)
                            CancellationSnapshot = Manager.ShouldNotBeNull().GetAsync(_subAgentId);
                        if (raceCompletion && _subAgentId is not null)
                            CompletionRace = Manager.ShouldNotBeNull().OnCompletedAsync(_subAgentId, "callback completion");
                        if (throwOnCancellation)
                        {
                            Interlocked.Increment(ref ThrowingCallbackCount);
                            throw new InvalidOperationException("callback exploded");
                        }
                    });
                    Entered.TrySetResult();
                    await waitForRelease;
                    return new AgentResponse { Content = "done" };
                });
            var supervisor = new Mock<IAgentSupervisor>();
            supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
                .Returns<AgentId, SessionId, CancellationToken>((id, _, _) =>
                {
                    if (id == Parent)
                        return Task.FromResult(handle.Object);
                    ChildDescriptor = _descriptors[id];
                    Workspace = Workspaces.GetWorkspacePath(id.Value);
                    ExistsAtHandleCreation = Directory.Exists(Workspace);
                    Tools = new DefaultAgentToolFactory(shellCommand: OperatingSystem.IsWindows()
                        ? ["pwsh", "-NoProfile", "-Command"] : ["bash", "-c"])
                        .CreateTools(WorkingDir.From(Workspace), new DefaultPathValidator(ChildDescriptor.FileAccess, Workspace));
                    return Task.FromResult(handle.Object);
                });
            supervisor.Setup(s => s.StopAsync(It.IsAny<AgentId>(), It.IsAny<SessionId>(), It.IsAny<CancellationToken>()))
                .Callback(() => Interlocked.Increment(ref StopCount))
                .Returns(Task.CompletedTask);
            var activity = new Mock<IActivityBroadcaster>();
            activity.Setup(a => a.PublishAsync(It.IsAny<GatewayActivity>(), It.IsAny<CancellationToken>()))
                .Callback<GatewayActivity, CancellationToken>((item, _) =>
                {
                    if (item.Type is GatewayActivityType.SubAgentCompleted or GatewayActivityType.SubAgentFailed or GatewayActivityType.SubAgentKilled)
                        _terminal.TrySetResult();
                }).Returns(ValueTask.CompletedTask);
            Manager = new DefaultSubAgentManager(supervisor.Object, Registry.Object, activity.Object,
                Mock.Of<IChannelDispatcher>(), new TestOptionsMonitor<GatewayOptions>(new GatewayOptions()),
                new AuditLogger(Audits, Warnings), workspaceManager: Workspaces);
        }

        internal async Task<SubAgentInfo> SpawnAsync(bool shared = false)
        {
            var info = await Manager.SpawnAsync(new SubAgentSpawnRequest
            {
                ParentAgentId = Parent, ParentSessionId = ParentSession, Task = "cwd probe",
                Mode = new Embody(SubAgentArchetype.General), ShareWorkspace = shared,
                InheritedConversationId = ConversationId.From("provision-parent-conversation")
            });
            _subAgentId = info.SubAgentId;
            return info;
        }

        internal Task WaitForCleanupAsync() => _terminal.Task.WaitAsync(TimeSpan.FromSeconds(30));

        public async ValueTask DisposeAsync()
        {
            Release.TrySetResult();
            if (_subAgentId is not null)
            {
                await Manager.KillAsync(_subAgentId, ParentSession);
                // A deliberately failing RED kill may never retire. Do not mask that assertion
                // with a second timeout while disposing the test's private filesystem fixture.
                if (Manager.IsRetiredForTest(_subAgentId))
                    await _unregistered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class AuditLogger(ConcurrentQueue<string> audits, ConcurrentQueue<string> warnings) : ILogger<DefaultSubAgentManager>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (logLevel == LogLevel.Warning)
                warnings.Enqueue(message + " " + exception);
            if (logLevel == LogLevel.Information && message.Contains(SubAgentWorkspaceReclamationAudit.MessagePrefix, StringComparison.Ordinal))
                audits.Enqueue(message);
        }
    }
}
