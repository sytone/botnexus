using System.Reflection;
using System.Text;
using System.Text.Json;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using BotNexus.Cron;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Commands;

public sealed class LoopCommandTests
{
    [Theory]
    [InlineData("/loop check the queue", "*/5 * * * *", "check the queue")]
    [InlineData("/loop 15m check the queue", "*/15 * * * *", "check the queue")]
    [InlineData("/loop 2h check the queue", "0 */2 * * *", "check the queue")]
    [InlineData("/LOOP 24h check the queue", "0 0 * * *", "check the queue")]
    [InlineData("/loop 60m check the queue", "0 */1 * * *", "check the queue")]
    [InlineData("/loop 3m check  the\nqueue", "*/3 * * * *", "check  the\nqueue")]
    public async Task Dispatch_Create_PersistsEnabledAgentPromptInCurrentHumanConversation(string input, string schedule, string prompt)
    {
        await using var fixture = new Fixture();
        var result = await fixture.Execute(input);
        result.IsError.ShouldBeFalse();
        var job = (await fixture.Store.ListAsync()).ShouldHaveSingleItem();
        job.Schedule.ShouldBe(schedule);
        job.Message.ShouldBe(prompt);
        job.Enabled.ShouldBeTrue();
        job.System.ShouldBeFalse();
        job.ActionType.ShouldBe("agent-prompt");
        job.AgentId.ShouldBe(Fixture.Agent);
        job.CreatedBy.ShouldBe(Fixture.Agent.Value);
        job.ConversationId.ShouldBe(Fixture.ConversationKey);
        result.Body.ShouldContain(job.Name);
        // Read the SQLite row again: identification must survive serialization/restart.
        (await fixture.Execute("/loop status")).Body.ShouldContain(job.Name);
    }

    [Theory]
    [InlineData("30s")]
    [InlineData("0m")]
    [InlineData("7m")]
    [InlineData("90m")]
    [InlineData("5h")]
    [InlineData("1.5h")]
    [InlineData("-2m")]
    [InlineData("15minutes")]
    [InlineData("every")]
    [InlineData("PT5M")]
    [InlineData("120m")]
    [InlineData("2147483647h")]
    [InlineData("5")]
    [InlineData(".5h")]
    [InlineData("+2m")]
    public async Task Dispatch_InvalidExplicitInterval_RefusesAndNamesForms(string token)
    {
        await using var fixture = new Fixture();
        var result = await fixture.Execute($"/loop {token} check queue");
        result.IsError.ShouldBeTrue();
        result.Body.ShouldContain("m");
        result.Body.ShouldContain("h");
        (await fixture.Store.ListAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/loop")]
    [InlineData("/loop 5m")]
    [InlineData("/loop status extra")]
    [InlineData("/loop stop one two")]
    public async Task Dispatch_MissingPromptOrInvalidManagementArguments_Refuses(string input)
    {
        await using var fixture = new Fixture();
        (await fixture.Execute(input)).IsError.ShouldBeTrue();
        (await fixture.Store.ListAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("headless")]
    [InlineData("missing-session")]
    [InlineData("missing-conversation")]
    [InlineData("foreign-conversation")]
    [InlineData("foreign-session")]
    [InlineData("archived")]
    [InlineData("stale-session")]
    [InlineData("ambiguous-fallback")]
    [InlineData("foreign-unbound-session")]
    [InlineData("wrong-id-unbound-session")]
    public async Task Dispatch_UnresolvedOrMismatchedContext_RefusesWithoutCreating(string scenario)
    {
        await using var fixture = new Fixture();
        fixture.BreakContext(scenario);
        var result = await fixture.Execute("/loop check queue", headless: scenario == "headless");
        result.IsError.ShouldBeTrue();
        result.Body.ToLowerInvariant().ShouldContain("conversation");
        (await fixture.Store.ListAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Dispatch_DurableActiveSessionFallback_BindsCurrentConversation()
    {
        await using var fixture = new Fixture();
        fixture.Session = null;
        (await fixture.Execute("/loop check queue")).IsError.ShouldBeFalse();
        (await fixture.Store.ListAsync()).ShouldHaveSingleItem().ConversationId.ShouldBe(Fixture.ConversationKey);
    }

    [Fact]
    public async Task Dispatch_SameAgentUnboundSession_UsesDurableFallback()
    {
        await using var fixture = new Fixture();
        fixture.Session = new GatewaySession { SessionId = SessionId.From("current-session"), AgentId = Fixture.Agent };
        (await fixture.Execute("/loop check queue")).IsError.ShouldBeFalse();
        (await fixture.Store.ListAsync()).ShouldHaveSingleItem().ConversationId.ShouldBe(Fixture.ConversationKey);
    }

    [Theory]
    [InlineData("prefix /loop stop")]
    [InlineData("/loop\u00a0stop")]
    [InlineData("/loop\u000bstop")]
    public async Task Dispatch_RegistryRecognizedStop_NeverCreatesAnotherLoop(string input)
    {
        await using var fixture = new Fixture();
        (await fixture.Execute("/loop check queue")).IsError.ShouldBeFalse();
        (await fixture.Execute(input)).IsError.ShouldBeFalse();
        (await fixture.Store.ListAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Dispatch_PromptWhitespace_IsPreserved()
    {
        await using var fixture = new Fixture();
        (await fixture.Execute("/loop 5m check  the\nqueue")).IsError.ShouldBeFalse();
        (await fixture.Store.ListAsync()).ShouldHaveSingleItem().Message.ShouldBe("check  the\nqueue");
    }

    [Theory]
    [InlineData(600, 3600, "*/10 * * * *")]
    [InlineData(60, 120, "*/2 * * * *")]
    [InlineData(-1, -1, "*/5 * * * *")]
    public async Task Dispatch_DefaultInterval_ConsumesExistingClamp(int floor, int ceiling, string schedule)
    {
        await using var fixture = new Fixture(new CronOptions { SelfPacingFloorSeconds = floor, SelfPacingCeilingSeconds = ceiling });
        var result = await fixture.Execute("/loop check queue");
        result.IsError.ShouldBeFalse();
        result.Body.ShouldContain("requested 300s");
        result.Body.ShouldContain("effective");
        result.Body.ShouldContain("clamp");
        (await fixture.Store.ListAsync()).ShouldHaveSingleItem().Schedule.ShouldBe(schedule);
    }

    [Fact]
    public async Task Dispatch_UnrepresentableConfiguredDefault_RefusesRatherThanApproximates()
    {
        await using var fixture = new Fixture(new CronOptions { SelfPacingFloorSeconds = 301 });
        (await fixture.Execute("/loop check queue")).IsError.ShouldBeTrue();
        (await fixture.Store.ListAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Dispatch_StatusAndStop_FilterExactConversationAgentAndLoopMarker()
    {
        await using var fixture = new Fixture();
        (await fixture.Execute("/loop check queue")).IsError.ShouldBeFalse();
        var own = (await fixture.Store.ListAsync()).ShouldHaveSingleItem();
        var others = new[]
        {
            own with { Id = JobId.From("other-conversation"), Name = "other-conversation", ConversationId = ConversationId.From("elsewhere") },
            own with { Id = JobId.From("foreign-agent"), Name = "foreign-agent", AgentId = AgentId.From("foreign") },
            own with { Id = JobId.From("foreign-creator"), Name = "foreign-creator", CreatedBy = "foreign" },
            own with { Id = JobId.From("ordinary-cron"), Name = "ordinary-cron", Metadata = null },
            own with { Id = JobId.From("command-cron"), Name = "command-cron", ActionType = "command" },
            own with { Id = JobId.From("system-cron"), Name = "system-cron", System = true }
        };
        foreach (var job in others) await fixture.Store.CreateAsync(job);
        var status = await fixture.Execute("/loop status");
        status.IsError.ShouldBeFalse();
        status.Body.ShouldContain(own.Name);
        foreach (var job in others)
        {
            status.Body.ShouldNotContain(job.Name);
            (await fixture.Execute($"/loop stop {job.Name}")).IsError.ShouldBeTrue();
            (await fixture.Store.GetAsync(job.Id)).ShouldNotBeNull();
        }
        (await fixture.Execute("/loop stop")).IsError.ShouldBeFalse();
        (await fixture.Store.GetAsync(own.Id)).ShouldBeNull();
        fixture.Conversations.Verify(store => store.ArchiveAsync(It.IsAny<ConversationId>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Dispatch_Stop_ZeroOneManyAndNamedSelectionAreUnambiguous()
    {
        await using var fixture = new Fixture();
        (await fixture.Execute("/loop status")).Body.ShouldContain("No loops");
        (await fixture.Execute("/loop stop")).IsError.ShouldBeTrue();
        (await fixture.Execute("/loop check one")).IsError.ShouldBeFalse();
        (await fixture.Execute("/loop check two")).IsError.ShouldBeFalse();
        var jobs = await fixture.Store.ListAsync();
        (await fixture.Execute("/loop stop")).IsError.ShouldBeTrue();
        (await fixture.Store.ListAsync()).Count.ShouldBe(2);
        (await fixture.Execute($"/loop stop {jobs[0].Name}")).IsError.ShouldBeFalse();
        (await fixture.Store.GetAsync(jobs[0].Id)).ShouldBeNull();
        (await fixture.Store.GetAsync(jobs[1].Id)).ShouldNotBeNull();
        (await fixture.Execute("/loop stop")).IsError.ShouldBeFalse();
        (await fixture.Store.ListAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Dispatch_Stop_CancelsActiveRunAndReclaimsOnlyItsRunSessions()
    {
        var action = new BlockingAction();
        await using var fixture = new Fixture(action: action);
        (await fixture.Execute("/loop check queue")).IsError.ShouldBeFalse();
        var job = (await fixture.Store.ListAsync()).ShouldHaveSingleItem();
        var ownedSession = SessionId.From($"cron:{job.Id.Value}:run-one");
        fixture.Sessions.Setup(store => store.ListAsync(Fixture.Agent, It.IsAny<CancellationToken>())).ReturnsAsync(
        [new GatewaySession { SessionId = ownedSession, AgentId = Fixture.Agent },
         new GatewaySession { SessionId = SessionId.From("human-session"), AgentId = Fixture.Agent },
         new GatewaySession { SessionId = SessionId.From("cron:other-job:run-one"), AgentId = Fixture.Agent }]);
        var runTask = fixture.Scheduler.RunNowAsync(job.Id);
        await action.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        (await fixture.Execute($"/loop stop {job.Name}")).IsError.ShouldBeFalse();
        action.Token.IsCancellationRequested.ShouldBeTrue();
        (await runTask).Status.ShouldBe(CronRunStatus.Aborted);
        fixture.Sessions.Verify(store => store.DeleteAsync(ownedSession, It.IsAny<CancellationToken>()), Times.Once);
        fixture.Sessions.Verify(store => store.DeleteAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Conversations.Verify(store => store.ArchiveAsync(It.IsAny<ConversationId>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        (await fixture.Store.GetAsync(job.Id)).ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("?agent=allowed-agent")]
    [InlineData("?agentId=allowed-agent")]
    public async Task Execute_ScopedCallerWithValidForbiddenSession_DeniesBeforeWritesOrTools(string query)
    {
        await using var fixture = new Fixture();
        var caller = new GatewayCallerIdentity { CallerId = "scoped", AllowedAgents = ["allowed-agent"] };
        var response = await fixture.ExecuteHttp(caller, query);
        response.ShouldBe(StatusCodes.Status403Forbidden);
        (await fixture.Store.ListAsync()).ShouldBeEmpty();
        fixture.Supervisor.VerifyNoOtherCalls();
        fixture.Inspector.VerifyNoOtherCalls();
        fixture.Sessions.Verify(store => store.GetAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Execute_MissingAuthenticatedIdentity_DeniesBeforeWritesOrTools()
    {
        await using var fixture = new Fixture();
        var response = await fixture.ExecuteHttp(new GatewayCallerIdentity { CallerId = "admin", IsAdmin = true }, removeIdentity: true);
        response.ShouldBe(StatusCodes.Status403Forbidden);
        (await fixture.Store.ListAsync()).ShouldBeEmpty();
        fixture.Supervisor.VerifyNoOtherCalls();
        fixture.Inspector.VerifyNoOtherCalls();
        fixture.Sessions.Verify(store => store.GetAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("authorized")]
    [InlineData("admin")]
    [InlineData("unrestricted")]
    [InlineData("development")]
    public async Task Execute_AuthorizedCaller_CreatesLoopThroughMiddlewareControllerAndRegistry(string mode)
    {
        await using var fixture = new Fixture();
        var caller = mode switch
        {
            "authorized" => new GatewayCallerIdentity { CallerId = mode, AllowedAgents = [Fixture.Agent.Value.ToUpperInvariant()] },
            "admin" => new GatewayCallerIdentity { CallerId = mode, IsAdmin = true, AllowedAgents = ["other-agent"] },
            "development" => new GatewayCallerIdentity { CallerId = "gateway-dev", IsAdmin = true, Permissions = ["*"] },
            _ => new GatewayCallerIdentity { CallerId = mode }
        };
        (await fixture.ExecuteHttp(caller)).ShouldBe(StatusCodes.Status200OK);
        var job = (await fixture.Store.ListAsync()).ShouldHaveSingleItem();
        job.AgentId.ShouldBe(Fixture.Agent);
        job.ConversationId.ShouldBe(Fixture.ConversationKey);
    }

    [Theory]
    [InlineData("?agent=allowed-agent")]
    [InlineData("?agentId=allowed-agent")]
    [InlineData("?agent=loop-agent&agentId=allowed-agent")]
    public async Task Execute_BothAgentsAllowedButSelectorsDisagree_RejectsBeforeLookupOrWrites(string query)
    {
        await using var fixture = new Fixture();
        var caller = new GatewayCallerIdentity { CallerId = "scoped", AllowedAgents = ["allowed-agent", Fixture.Agent.Value] };
        (await fixture.ExecuteHttp(caller, query)).ShouldBe(StatusCodes.Status400BadRequest);
        fixture.Inspector.VerifyNoOtherCalls();
        fixture.Sessions.Verify(store => store.GetAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.AssertNoSessionOrConversationWrites();
        (await fixture.Store.ListAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/loop status")]
    [InlineData("/loop stop")]
    public async Task Execute_ForbiddenManagement_DeniesAndPreservesExistingLoop(string input)
    {
        await using var fixture = new Fixture();
        (await fixture.Execute("/loop check queue")).IsError.ShouldBeFalse();
        var job = (await fixture.Store.ListAsync()).ShouldHaveSingleItem();
        fixture.Sessions.Invocations.Clear();
        var caller = new GatewayCallerIdentity { CallerId = "scoped", AllowedAgents = ["allowed-agent"] };
        (await fixture.ExecuteHttp(caller, "?agent=allowed-agent", input: input)).ShouldBe(StatusCodes.Status403Forbidden);
        // Metadata dictionaries are materialized anew by SQLite; compare persisted bytes,
        // not their CLR reference identity, while checking the complete row is unchanged.
        JsonSerializer.Serialize((await fixture.Store.ListAsync()).ShouldHaveSingleItem())
            .ShouldBe(JsonSerializer.Serialize(job));
        fixture.Inspector.VerifyNoOtherCalls();
        fixture.Sessions.Verify(store => store.GetAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.AssertNoSessionOrConversationWrites();
    }

    [Theory]
    [InlineData("/new", "foreign-session")]
    [InlineData("/new", "missing-session")]
    [InlineData("/new", "wrong-id-unbound-session")]
    [InlineData("/model clear", "foreign-session")]
    [InlineData("/reasoning high", "foreign-conversation")]
    [InlineData("/tools clear", "stale-session")]
    [InlineData("/new", "missing-conversation")]
    [InlineData("/model clear", "wrong-conversation-id")]
    [InlineData("/new", "archived")]
    public async Task Execute_InconsistentSessionOrDurableParent_RejectsBeforeToolsAndWrites(string input, string scenario)
    {
        await using var fixture = new Fixture();
        fixture.BreakContext(scenario);
        var caller = new GatewayCallerIdentity { CallerId = "scoped", AllowedAgents = [Fixture.Agent.Value] };
        (await fixture.ExecuteHttp(caller, input: input)).ShouldBe(StatusCodes.Status400BadRequest);
        fixture.Inspector.VerifyNoOtherCalls();
        fixture.AssertNoSessionOrConversationWrites();
        (await fixture.Store.ListAsync()).ShouldBeEmpty();
        if (fixture.Session is { } session) session.Status.ShouldNotBe(SessionStatus.Sealed);
    }

    [Theory]
    [InlineData(null, "current-session")]
    [InlineData(" ", "current-session")]
    [InlineData("loop-agent", " ")]
    [InlineData("loop-agent", "")]
    public async Task Execute_MissingAgentOrMalformedContext_RejectsWithoutWrites(string? agent, string? session)
    {
        await using var fixture = new Fixture();
        var caller = new GatewayCallerIdentity { CallerId = "scoped", AllowedAgents = [Fixture.Agent.Value] };
        (await fixture.ExecuteHttp(caller, input: "/new", agentId: agent, sessionId: session, useDefaultContext: false))
            .ShouldBe(StatusCodes.Status400BadRequest);
        fixture.Inspector.VerifyNoOtherCalls();
        fixture.AssertNoSessionOrConversationWrites();
        (await fixture.Store.ListAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/help", null)]
    [InlineData("/new", "loop-agent")]
    public async Task Execute_IntentionalNoSessionContext_RemainsAvailable(string input, string? agent)
    {
        await using var fixture = new Fixture();
        var caller = new GatewayCallerIdentity { CallerId = "scoped", AllowedAgents = [Fixture.Agent.Value] };
        (await fixture.ExecuteHttp(caller, input: input, agentId: agent, useDefaultContext: false)).ShouldBe(StatusCodes.Status200OK);
        fixture.Inspector.VerifyNoOtherCalls();
        fixture.Sessions.Verify(store => store.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Sessions.Verify(store => store.GetOrCreateAsync(It.IsAny<SessionId>(), Fixture.Agent, It.IsAny<CancellationToken>()),
            input == "/new" ? Times.Once : Times.Never);
    }

    private sealed class BlockingAction : ICronAction
    {
        public string ActionType => "agent-prompt";
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token { get; private set; }
        public async Task ExecuteAsync(CronExecutionContext context, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal static readonly AgentId Agent = AgentId.From("loop-agent");
        internal static readonly ConversationId ConversationKey = ConversationId.From("human-conversation");
        private static readonly SessionId SessionKey = SessionId.From("current-session");
        private readonly string directory = Path.Combine(Path.GetTempPath(), "loop-command-tests", Guid.NewGuid().ToString("N"));
        private readonly ServiceProvider services;
        private readonly CommandRegistry registry;
        internal readonly Mock<IAgentSupervisor> Supervisor = new();
        internal readonly Mock<IAgentHandleInspector> Inspector;
        internal readonly Mock<IConversationStore> Conversations = new();
        internal readonly Mock<ISessionStore> Sessions = new();
        internal CronScheduler Scheduler => services.GetRequiredService<CronScheduler>();
        internal GatewaySession? Session = new() { SessionId = SessionKey, AgentId = Agent, ConversationId = ConversationKey };
        internal Conversation? Conversation = new() { ConversationId = ConversationKey, AgentId = Agent, ActiveSessionId = SessionKey, Kind = ConversationKind.HumanAgent, IsDefault = true };
        internal SqliteCronStore Store { get; }

        internal Fixture(CronOptions? options = null, ICronAction? action = null)
        {
            Inspector = Supervisor.As<IAgentHandleInspector>();
            Inspector.Setup(value => value.GetHandle(Agent, SessionKey)).Returns(new Mock<IAgentHandle>().Object);
            Store = new SqliteCronStore(Path.Combine(directory, "cron.sqlite"));
            var sessions = Sessions;
            sessions.Setup(store => store.GetAsync(SessionKey, It.IsAny<CancellationToken>())).ReturnsAsync(() => Session);
            sessions.Setup(store => store.ListAsync(It.IsAny<AgentId?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
            Conversations.Setup(store => store.GetAsync(ConversationKey, It.IsAny<CancellationToken>())).ReturnsAsync(() => Conversation);
            Conversations.Setup(store => store.ListAsync(Agent, It.IsAny<CancellationToken>())).ReturnsAsync(() => Conversation is { } value ? new[] { value } : []);
            var cronOptions = new Mock<IOptionsMonitor<CronOptions>>();
            cronOptions.SetupGet(value => value.CurrentValue).Returns(options ?? new CronOptions());
            var collection = new ServiceCollection();
            collection.AddSingleton(Conversations.Object);
            collection.AddSingleton(sessions.Object);
            collection.AddSingleton<ICronStore>(Store);
            collection.AddSingleton(cronOptions.Object);
            collection.AddSingleton(provider => new CronScheduler(Store, action is null ? [] : [action], provider.GetRequiredService<IServiceScopeFactory>(), cronOptions.Object, NullLogger<CronScheduler>.Instance));
            collection.AddSingleton<CommandRegistry>(_ => registry ?? throw new InvalidOperationException("Registry has not been initialized."));
            services = collection.BuildServiceProvider();
            var type = typeof(CommandRegistry).Assembly.GetType("BotNexus.Gateway.Commands.BuiltInCommandContributor") ?? throw new InvalidOperationException("Missing built-in contributor");
            var compaction = new Mock<IOptionsMonitor<CompactionOptions>>();
            compaction.SetupGet(value => value.CurrentValue).Returns(new CompactionOptions());
            var contributor = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                [new Mock<IAgentRegistry>().Object, new Mock<IAgentSupervisor>().Object, sessions.Object, compaction.Object, services, null], null) as ICommandContributor
                ?? throw new InvalidOperationException("Cannot construct built-in contributor");
            registry = new CommandRegistry([contributor], NullLogger<CommandRegistry>.Instance);
        }

        internal async Task<int> ExecuteHttp(GatewayCallerIdentity caller, string query = "", bool removeIdentity = false,
            string input = "/loop check queue", string? agentId = null, string? sessionId = null, bool useDefaultContext = true)
        {
            var http = new DefaultHttpContext();
            http.Request.Path = "/api/commands/execute";
            http.Request.Method = HttpMethods.Post;
            http.Request.QueryString = new QueryString(query);
            http.Request.ContentType = "application/json";
            http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                input, agentId = useDefaultContext ? Agent.Value : agentId, sessionId = useDefaultContext ? SessionKey.Value : sessionId
            })));
            http.Request.ContentLength = http.Request.Body.Length;
            http.Response.Body = new MemoryStream();
            var auth = new Mock<IGatewayAuthHandler>();
            auth.Setup(value => value.AuthenticateAsync(It.IsAny<GatewayAuthContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(GatewayAuthResult.Success(caller));
            var environment = new Mock<IWebHostEnvironment>();
            environment.SetupGet(value => value.WebRootFileProvider).Returns(new NullFileProvider());
            var middleware = new GatewayAuthMiddleware(async context =>
            {
                if (removeIdentity) context.Items.Remove(GatewayAuthHttpContext.CallerIdentityItemKey);
                var request = await JsonSerializer.DeserializeAsync<CommandExecuteRequest>(context.Request.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new InvalidOperationException("Missing command body");
                var controller = ActivatorUtilities.CreateInstance<CommandsController>(services, registry, Supervisor.Object, new BotNexusHome(directory));
                controller.ControllerContext = new ControllerContext { HttpContext = context };
                var result = await controller.Execute(request, CancellationToken.None);
                if (result.Result is ObjectResult objectResult)
                {
                    context.Response.StatusCode = objectResult.StatusCode ?? StatusCodes.Status200OK;
                    if (objectResult.Value is CommandResult command) command.IsError.ShouldBeFalse();
                }
                else if (result.Result is StatusCodeResult status)
                    context.Response.StatusCode = status.StatusCode;
                else
                    throw new InvalidOperationException("Unexpected command result");
            }, auth.Object, environment.Object, NullLogger<GatewayAuthMiddleware>.Instance);
            await middleware.InvokeAsync(http);
            await http.Request.Body.DisposeAsync();
            await http.Response.Body.DisposeAsync();
            return http.Response.StatusCode;
        }

        internal Task<CommandResult> Execute(string input, bool headless = false) => registry.ExecuteAsync(input, new CommandExecutionContext
        {
            RawInput = input, AgentId = Agent.Value, SessionId = headless ? null : SessionKey.Value, HomeDirectory = directory
        });

        internal void AssertNoSessionOrConversationWrites()
        {
            Sessions.Verify(store => store.SaveAsync(It.IsAny<GatewaySession>(), It.IsAny<CancellationToken>()), Times.Never);
            Sessions.Verify(store => store.GetOrCreateAsync(It.IsAny<SessionId>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()), Times.Never);
            Conversations.Verify(store => store.PatchOverrideAsync(It.IsAny<ConversationId>(), It.IsAny<ConversationOverridePatch>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        internal void BreakContext(string scenario)
        {
            switch (scenario)
            {
                case "ambiguous-fallback":
                    Session = null;
                    Conversations.Setup(store => store.ListAsync(Agent, It.IsAny<CancellationToken>())).ReturnsAsync(
                    [new Conversation { ConversationId = ConversationKey, AgentId = Agent, ActiveSessionId = SessionKey },
                     new Conversation { ConversationId = ConversationId.From("duplicate"), AgentId = Agent, ActiveSessionId = SessionKey }]);
                    break;
                case "wrong-conversation-id": if (Conversation is { } wrong) Conversation = wrong with { ConversationId = ConversationId.From("wrong-parent") }; break;
                case "foreign-unbound-session": Session = new GatewaySession { SessionId = SessionKey, AgentId = AgentId.From("foreign") }; break;
                case "wrong-id-unbound-session": Session = new GatewaySession { SessionId = SessionId.From("wrong-session"), AgentId = Agent }; break;
                case "missing-session": Session = null; Conversation = null; break;
                case "missing-conversation": Conversation = null; break;
                case "foreign-conversation": if (Conversation is { } foreign) Conversation = foreign with { AgentId = AgentId.From("foreign") }; break;
                case "foreign-session": Session = new GatewaySession { SessionId = SessionKey, AgentId = AgentId.From("foreign"), ConversationId = ConversationKey }; break;
                case "archived": if (Conversation is { } archived) archived.Status = ConversationStatus.Archived; break;
                case "stale-session": if (Conversation is { } stale) stale.ActiveSessionId = SessionId.From("newer-session"); break;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            SqlitePoolCleanup.ClearPoolFor(Path.Combine(directory, "cron.sqlite"));
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
