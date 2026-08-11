using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Hooks;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Hooks;
using NSubstitute;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Issue #2846 — owner-private workspace content (<c>MEMORY.md</c>, <c>USER.md</c> and the daily
/// memory notes) must not reach a conversation that has non-owner participants.
/// </summary>
/// <remarks>
/// Every assertion here is made against the ASSEMBLED PROMPT STRING, never against the internal
/// file list. The file list is an implementation detail; the prompt is what the model receives and
/// therefore the only surface where disclosure is real. Sentinel contents are chosen so a match
/// cannot come from prompt scaffolding (headings, guidance text) rather than the file body.
/// </remarks>
public sealed class WorkspaceContextBuilderConversationScopeTests
{
    private const string MemorySentinel = "SENTINEL-CONSOLIDATED-PRIVATE-MEMORY";
    private const string UserSentinel = "SENTINEL-OWNER-PROFILE-JON";
    private const string DailySentinel = "SENTINEL-DAILY-NOTE-BODY";
    private const string PublicSentinel = "SENTINEL-PUBLIC-AGENTS-BODY";

    private readonly MockFileSystem _fileSystem = new();

    // ── AC#2: shared conversation excludes both files from the assembled prompt ──────────

    [Fact]
    public async Task BuildSystemPromptAsync_WhenScopeIsShared_AssembledPromptContainsNeitherMemoryNorUserContent()
    {
        var workspacePath = CreateFullWorkspace();
        try
        {
            var builder = new WorkspaceContextBuilder(new StubWorkspaceManager(workspacePath), _fileSystem);
            var descriptor = CreateDescriptor();

            // Positive control: the identical workspace and descriptor DO surface both files on the
            // private path, so the negative assertions below cannot pass vacuously (e.g. because the
            // files never loaded at all).
            var privatePrompt = await builder.BuildSystemPromptAsync(descriptor, null, null, ConversationScope.Private);
            privatePrompt.ShouldContain(MemorySentinel);
            privatePrompt.ShouldContain(UserSentinel);

            var sharedPrompt = await builder.BuildSystemPromptAsync(descriptor, null, null, ConversationScope.Shared);

            sharedPrompt.ShouldNotContain(MemorySentinel);
            sharedPrompt.ShouldNotContain(UserSentinel);
            // The non-private files must still be there — this is an exclusion, not a lobotomy.
            sharedPrompt.ShouldContain(PublicSentinel);
        }
        finally
        {
            Cleanup(workspacePath);
        }
    }

    [Fact]
    public async Task BuildSystemPromptAsync_WhenScopeIsShared_AlsoExcludesDailyMemoryNotes()
    {
        var workspacePath = CreateFullWorkspace();
        try
        {
            var builder = new WorkspaceContextBuilder(new StubWorkspaceManager(workspacePath), _fileSystem);
            var descriptor = CreateDescriptor();

            (await builder.BuildSystemPromptAsync(descriptor, null, null, ConversationScope.Private))
                .ShouldContain(DailySentinel);

            var sharedPrompt = await builder.BuildSystemPromptAsync(descriptor, null, null, ConversationScope.Shared);

            // Daily notes are the same trust class as MEMORY.md — excluding the consolidated file
            // while leaking today's raw note would be a fix in name only.
            sharedPrompt.ShouldNotContain(DailySentinel);
        }
        finally
        {
            Cleanup(workspacePath);
        }
    }

    [Fact]
    public async Task BuildSystemPromptAsync_WhenScopeIsShared_ExcludesPrivateFilesNamedExplicitlyInSystemPromptFiles()
    {
        var workspacePath = CreateFullWorkspace();
        try
        {
            var builder = new WorkspaceContextBuilder(new StubWorkspaceManager(workspacePath), _fileSystem);
            var descriptor = CreateDescriptor() with { SystemPromptFiles = ["AGENTS.md", "USER.md", "MEMORY.md"] };

            (await builder.BuildSystemPromptAsync(descriptor, null, null, ConversationScope.Private))
                .ShouldContain(MemorySentinel);

            var sharedPrompt = await builder.BuildSystemPromptAsync(descriptor, null, null, ConversationScope.Shared);

            // An explicit systemPromptFiles entry is an operator request, but it cannot authorise
            // disclosure to third parties — the conversation boundary outranks the config list.
            sharedPrompt.ShouldNotContain(MemorySentinel);
            sharedPrompt.ShouldNotContain(UserSentinel);
            sharedPrompt.ShouldContain(PublicSentinel);
        }
        finally
        {
            Cleanup(workspacePath);
        }
    }

    // ── AC#3: private conversation is byte-identical to today ────────────────────────────

    [Fact]
    public async Task BuildSystemPromptAsync_PrivateScope_ProducesPromptByteIdenticalToDefaultOverload()
    {
        var workspacePath = CreateFullWorkspace();
        try
        {
            var builder = new WorkspaceContextBuilder(new StubWorkspaceManager(workspacePath), _fileSystem);
            var descriptor = CreateDescriptor();

            // The pre-#2846 entry point (no scope argument) and the explicitly-private one must
            // produce the same bytes: that is the whole parity claim for the default path.
            var legacyPrompt = await builder.BuildSystemPromptAsync(descriptor, null, null);
            var privatePrompt = await builder.BuildSystemPromptAsync(descriptor, null, null, ConversationScope.Private);

            privatePrompt.ShouldBe(legacyPrompt);
            privatePrompt.ShouldContain(MemorySentinel);
            privatePrompt.ShouldContain(UserSentinel);
            privatePrompt.ShouldContain(DailySentinel);
        }
        finally
        {
            Cleanup(workspacePath);
        }
    }

    [Fact]
    public async Task BuildSystemPromptAsync_DescriptorOnlyOverload_DefaultsToPrivateAndKeepsPrivateFiles()
    {
        var workspacePath = CreateFullWorkspace();
        try
        {
            var builder = new WorkspaceContextBuilder(new StubWorkspaceManager(workspacePath), _fileSystem);

            var prompt = await builder.BuildSystemPromptAsync(CreateDescriptor());

            prompt.ShouldContain(MemorySentinel);
            prompt.ShouldContain(UserSentinel);
        }
        finally
        {
            Cleanup(workspacePath);
        }
    }

    // ── AC#4: exclusion applies AFTER context hooks run ──────────────────────────────────

    [Fact]
    public async Task BuildSystemPromptAsync_WhenHookReAddsMemoryFileInSharedConversation_ItIsStillExcluded()
    {
        var workspacePath = CreateFullWorkspace();
        try
        {
            var dispatcher = new HookDispatcher();
            var hook = new ContextFileInjectingHook(
                new PromptContextFile("MEMORY.md", MemorySentinel),
                new PromptContextFile("USER.md", UserSentinel),
                new PromptContextFile("memory/2020-01-01.md", DailySentinel));
            dispatcher.Register<BeforeContextFilesBuildEvent, BeforeContextFilesBuildResult>(hook);

            var builder = new WorkspaceContextBuilder(new StubWorkspaceManager(workspacePath), _fileSystem, dispatcher);
            var descriptor = CreateDescriptor();

            var sharedPrompt = await builder.BuildSystemPromptAsync(descriptor, null, null, ConversationScope.Shared);

            // The hook must genuinely have run — otherwise this test proves nothing about ORDERING,
            // only that a hook which never fired could not leak anything.
            hook.Invocations.ShouldBe(1);
            hook.ObservedScope.ShouldBe(ConversationScope.Shared);
            sharedPrompt.ShouldNotContain(MemorySentinel);
            sharedPrompt.ShouldNotContain(UserSentinel);
            sharedPrompt.ShouldNotContain(DailySentinel);
        }
        finally
        {
            Cleanup(workspacePath);
        }
    }

    [Fact]
    public async Task BuildSystemPromptAsync_WhenHookAddsNonPrivateFileInSharedConversation_ItIsInjected()
    {
        var workspacePath = CreateFullWorkspace();
        try
        {
            var dispatcher = new HookDispatcher();
            var hook = new ContextFileInjectingHook(new PromptContextFile("SKILLS.md", "SENTINEL-HOOK-CONTRIBUTED-SKILLS"));
            dispatcher.Register<BeforeContextFilesBuildEvent, BeforeContextFilesBuildResult>(hook);

            var builder = new WorkspaceContextBuilder(new StubWorkspaceManager(workspacePath), _fileSystem, dispatcher);

            var sharedPrompt = await builder.BuildSystemPromptAsync(CreateDescriptor(), null, null, ConversationScope.Shared);

            // Sad-path guard for the filter: it must drop owner-private files only, not silently
            // discard every hook contribution and appear to "pass" the exclusion tests above.
            sharedPrompt.ShouldContain("SENTINEL-HOOK-CONTRIBUTED-SKILLS");
        }
        finally
        {
            Cleanup(workspacePath);
        }
    }

    // ── Scope derivation from the persisted participant set ──────────────────────────────

    [Fact]
    public async Task BuildSystemPromptAsync_WhenConversationHasSecondHumanParticipant_EscalatesToSharedAndExcludesPrivateFiles()
    {
        var workspacePath = CreateFullWorkspace();
        try
        {
            var conversation = CreateConversation(
                Participant(CitizenId.Of(UserId.From("jon"))),
                Participant(CitizenId.Of(AgentId.From("farnsworth"))),
                Participant(CitizenId.Of(UserId.From("someone-else"))));

            var builder = CreateBuilderWithConversation(workspacePath, conversation, out var executionContext);

            // Caller says Private; the persisted participant list says otherwise and wins upward.
            var prompt = await builder.BuildSystemPromptAsync(CreateDescriptor(), executionContext, null, ConversationScope.Private);

            prompt.ShouldNotContain(MemorySentinel);
            prompt.ShouldNotContain(UserSentinel);
        }
        finally
        {
            Cleanup(workspacePath);
        }
    }

    [Fact]
    public async Task BuildSystemPromptAsync_WhenConversationHasForeignAgentParticipant_EscalatesToShared()
    {
        var workspacePath = CreateFullWorkspace();
        try
        {
            var conversation = CreateConversation(
                Participant(CitizenId.Of(UserId.From("jon"))),
                Participant(CitizenId.Of(AgentId.From("farnsworth"))),
                Participant(CitizenId.Of(AgentId.From("nova"))));

            var builder = CreateBuilderWithConversation(workspacePath, conversation, out var executionContext);

            var prompt = await builder.BuildSystemPromptAsync(CreateDescriptor(), executionContext, null, ConversationScope.Private);

            prompt.ShouldNotContain(MemorySentinel);
            prompt.ShouldNotContain(UserSentinel);
        }
        finally
        {
            Cleanup(workspacePath);
        }
    }

    [Fact]
    public async Task BuildSystemPromptAsync_WhenConversationIsOwnerAndSingleHuman_StaysPrivate()
    {
        var workspacePath = CreateFullWorkspace();
        try
        {
            var conversation = CreateConversation(
                Participant(CitizenId.Of(UserId.From("jon"))),
                Participant(CitizenId.Of(AgentId.From("farnsworth"))));

            var builder = CreateBuilderWithConversation(workspacePath, conversation, out var executionContext);

            var prompt = await builder.BuildSystemPromptAsync(CreateDescriptor(), executionContext, null, ConversationScope.Private);

            // The classic one-to-one channel. Escalating this would break every existing agent.
            prompt.ShouldContain(MemorySentinel);
            prompt.ShouldContain(UserSentinel);
        }
        finally
        {
            Cleanup(workspacePath);
        }
    }

    [Fact]
    public async Task BuildSystemPromptAsync_WhenParticipantListIsEmpty_StaysPrivate()
    {
        var workspacePath = CreateFullWorkspace();
        try
        {
            var builder = CreateBuilderWithConversation(workspacePath, CreateConversation(), out var executionContext);

            var prompt = await builder.BuildSystemPromptAsync(CreateDescriptor(), executionContext, null, ConversationScope.Private);

            // Legacy rows written before participants were tracked have an empty list. Treating
            // those as shared would silently strip memory from every pre-existing conversation.
            prompt.ShouldContain(MemorySentinel);
        }
        finally
        {
            Cleanup(workspacePath);
        }
    }

    [Fact]
    public async Task BuildSystemPromptAsync_SharedCallerIsNeverDowngradedByAPrivateLookingParticipantList()
    {
        var workspacePath = CreateFullWorkspace();
        try
        {
            var conversation = CreateConversation(Participant(CitizenId.Of(UserId.From("jon"))));

            var builder = CreateBuilderWithConversation(workspacePath, conversation, out var executionContext);

            var prompt = await builder.BuildSystemPromptAsync(CreateDescriptor(), executionContext, null, ConversationScope.Shared);

            // Escalation is one-way. A federation entry point that already knows the conversation
            // is shared must not be talked out of it by a participant list that lags behind.
            prompt.ShouldNotContain(MemorySentinel);
            prompt.ShouldNotContain(UserSentinel);
        }
        finally
        {
            Cleanup(workspacePath);
        }
    }

    // ── AC#5: no memory-write guidance in a shared conversation ──────────────────────────

    [Fact]
    public void BuildMemorySection_WhenScopeIsShared_EmitsNothing()
    {
        IReadOnlySet<string> tools = new HashSet<string>(StringComparer.Ordinal) { "memory_save", "memory_search" };

        var privateLines = SystemPromptBuilder.BuildMemorySection(false, "full", tools, ConversationScope.Private);
        var sharedLines = SystemPromptBuilder.BuildMemorySection(false, "full", tools, ConversationScope.Shared);

        // Positive control first: with the same tools and mode the private call DOES emit guidance,
        // so the empty shared result is caused by the scope and not by an unrelated early return.
        privateLines.ShouldNotBeEmpty();
        privateLines.ShouldContain(line => line.Contains("MEMORY.md", StringComparison.Ordinal));
        sharedLines.ShouldBeEmpty();
    }

    [Fact]
    public async Task BuildSystemPromptAsync_WhenScopeIsShared_AssembledPromptCarriesNoMemoryWriteGuidance()
    {
        var workspacePath = CreateFullWorkspace();
        try
        {
            var builder = new WorkspaceContextBuilder(new StubWorkspaceManager(workspacePath), _fileSystem);
            var descriptor = CreateDescriptor();

            var privatePrompt = await builder.BuildSystemPromptAsync(descriptor, null, null, ConversationScope.Private);
            var sharedPrompt = await builder.BuildSystemPromptAsync(descriptor, null, null, ConversationScope.Shared);

            const string guidance = "Use `MEMORY.md` as long-lived consolidated context";
            privatePrompt.ShouldContain(guidance);
            sharedPrompt.ShouldNotContain(guidance);
            sharedPrompt.ShouldNotContain("<memory>");
        }
        finally
        {
            Cleanup(workspacePath);
        }
    }

    [Fact]
    public void BuildMemorySection_PrivateScope_MatchesTheLegacyOverloadExactly()
    {
        IReadOnlySet<string> tools = new HashSet<string>(StringComparer.Ordinal) { "memory_save" };

        var legacy = SystemPromptBuilder.BuildMemorySection(false, "full", tools);
        var explicitPrivate = SystemPromptBuilder.BuildMemorySection(false, "full", tools, ConversationScope.Private);

        explicitPrivate.ShouldBe(legacy);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────

    private static AgentDescriptor CreateDescriptor() => new()
    {
        AgentId = AgentId.From("farnsworth"),
        DisplayName = "Farnsworth",
        ModelId = "test-model",
        ApiProvider = "test-provider",
        Memory = new MemoryAgentConfig { Enabled = true }
    };

    private static SessionParticipant Participant(CitizenId citizenId) => new() { CitizenId = citizenId };

    private static Conversation CreateConversation(params SessionParticipant[] participants) => new()
    {
        ConversationId = ConversationId.From("c_scope_test"),
        AgentId = AgentId.From("farnsworth"),
        Title = "Scope test",
        Participants = [.. participants]
    };

    private WorkspaceContextBuilder CreateBuilderWithConversation(
        string workspacePath,
        Conversation conversation,
        out AgentExecutionContext executionContext)
    {
        var sessionId = SessionId.From("s_scope_test");
        executionContext = new AgentExecutionContext { SessionId = sessionId };
        conversation.ActiveSessionId = sessionId;

        var conversationStore = Substitute.For<IConversationStore>();
        conversationStore
            .ListAsync(AgentId.From("farnsworth"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Conversation>>([conversation]));

        var sessionStore = Substitute.For<ISessionStore>();
        sessionStore.GetAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GatewaySession?>(null));

        return new WorkspaceContextBuilder(
            new StubWorkspaceManager(workspacePath), _fileSystem, conversationStore, sessionStore);
    }

    private string CreateFullWorkspace()
    {
        var today = DateTime.Now.Date;
        return CreateWorkspace(
            ("AGENTS.md", PublicSentinel),
            ("SOUL.md", "SENTINEL-SOUL-BODY"),
            ("IDENTITY.md", "SENTINEL-IDENTITY-BODY"),
            ("USER.md", UserSentinel),
            ("MEMORY.md", MemorySentinel),
            (Path.Combine("memory", $"{today:yyyy-MM-dd}.md"), DailySentinel));
    }

    private string CreateWorkspace(params (string FileName, string Content)[] files)
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "botnexus-scope-tests", Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(rootPath, "workspace");
        _fileSystem.Directory.CreateDirectory(workspacePath);
        foreach (var (fileName, content) in files)
        {
            var normalized = fileName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            var filePath = Path.Combine(workspacePath, normalized);
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
                _fileSystem.Directory.CreateDirectory(directory);
            _fileSystem.File.WriteAllText(filePath, content);
        }

        return workspacePath;
    }

    private void Cleanup(string workspacePath)
    {
        var root = Path.GetDirectoryName(workspacePath);
        if (!string.IsNullOrWhiteSpace(root) && _fileSystem.Directory.Exists(root))
            _fileSystem.Directory.Delete(root, recursive: true);
    }

    /// <summary>
    /// A context-file hook that unconditionally contributes the files it was constructed with, and
    /// records that it ran. The invocation counter is what makes the AC#4 ordering assertion
    /// non-vacuous.
    /// </summary>
    private sealed class ContextFileInjectingHook(params PromptContextFile[] files)
        : IHookHandler<BeforeContextFilesBuildEvent, BeforeContextFilesBuildResult>
    {
        public int Priority => 0;

        public int Invocations { get; private set; }

        public ConversationScope? ObservedScope { get; private set; }

        public Task<BeforeContextFilesBuildResult?> HandleAsync(
            BeforeContextFilesBuildEvent hookEvent,
            CancellationToken ct = default)
        {
            Invocations++;
            ObservedScope = hookEvent.Scope;
            return Task.FromResult<BeforeContextFilesBuildResult?>(
                new BeforeContextFilesBuildResult { AdditionalContextFiles = files });
        }
    }

    private sealed class StubWorkspaceManager(string workspacePath) : IAgentWorkspaceManager
    {
        public Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken ct = default)
            => Task.FromResult(new AgentWorkspace(agentName, Soul: string.Empty, Identity: string.Empty, User: string.Empty, Memory: string.Empty));

        public Task SaveMemoryAsync(string agentName, string content, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SaveMemoryAsync(string agentName, string? filePath, string content, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SaveMemoryAsync(string agentName, string? filePath, string content, string? memoryPathOverride, CancellationToken ct = default)
            => Task.CompletedTask;

        public string GetWorkspacePath(string agentName) => workspacePath;
    }
}
