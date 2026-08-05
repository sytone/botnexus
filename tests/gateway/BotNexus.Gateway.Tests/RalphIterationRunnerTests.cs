using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Ralph;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Pins the iteration model of a ralph loop: each iteration is a FRESH session in the SAME
/// conversation, and it inherits none of the previous iteration's transcript (issue #2818, AC3).
/// </summary>
/// <remarks>
/// This is the load-bearing decision of the feature. Appending turns to one growing session would
/// make the loop's behaviour a function of accumulated context, so it would drift as it runs and
/// eventually compact - which is precisely how loop state gets silently lost. Continuity must come
/// from durable state (memory, checklist, repo), never from an inherited transcript.
/// </remarks>
public sealed class RalphIterationRunnerTests
{
    private static readonly AgentId Agent = AgentId.From("ralph-agent");

    /// <summary>A handle that answers with a canned reply.</summary>
    private static IAgentHandle StubHandle(AgentId agentId, SessionId sessionId, string reply)
    {
        var handle = Substitute.For<IAgentHandle>();
        handle.AgentId.Returns(agentId);
        handle.SessionId.Returns(sessionId);
        handle.PromptAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new AgentResponse { Content = reply }));
        return handle;
    }

    private sealed class StubSupervisor(Func<string> reply) : IAgentSupervisor
    {
        public List<SessionId> Sessions { get; } = [];

        public Task<IAgentHandle> GetOrCreateAsync(AgentId agentId, SessionId sessionId, CancellationToken cancellationToken = default)
        {
            Sessions.Add(sessionId);
            return Task.FromResult(StubHandle(agentId, sessionId, reply()));
        }

        public Task StopAsync(AgentId agentId, SessionId sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public AgentInstance? GetInstance(AgentId agentId, SessionId sessionId) => null;

        public IAgentHandle? GetHandle(AgentId agentId, SessionId sessionId) => null;

        public IReadOnlyList<AgentInstance> GetAllInstances() => [];

        public Task StopAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task EachIteration_RunsInAFreshSessionThatInheritsNoPriorTranscript()
    {
        const string SecretOnlyStatedInIterationOne = "the deploy key is rotated on tuesdays";

        var conversations = new InMemoryConversationStore();
        var sessions = new InMemorySessionStore();
        var replies = new Queue<string>([SecretOnlyStatedInIterationOne, "second turn"]);
        var supervisor = new StubSupervisor(replies.Dequeue);
        var runner = new RalphIterationRunner(supervisor, sessions, conversations, NullLogger<RalphIterationRunner>.Instance);

        var conversation = ConversationFactory.CreateForRalph(
            ConversationId.From("conv:ralph-fresh"), Agent, "do the work");
        await conversations.CreateAsync(conversation);

        (await runner.RunIterationAsync(conversation, "do the work", 1)).ShouldBeTrue();
        (await runner.RunIterationAsync(conversation, "do the work", 2)).ShouldBeTrue();

        supervisor.Sessions.Count.ShouldBe(2);
        supervisor.Sessions[0].ShouldNotBe(supervisor.Sessions[1]);

        var first = await sessions.GetAsync(supervisor.Sessions[0]);
        var second = await sessions.GetAsync(supervisor.Sessions[1]);

        // Both sessions live in the same conversation ...
        first!.ConversationId.ShouldBe(conversation.ConversationId);
        second!.ConversationId.ShouldBe(conversation.ConversationId);

        // ... but a fact stated only in iteration 1's transcript is absent from iteration 2's.
        first.GetHistorySnapshot()
            .ShouldContain(entry => entry.Content != null && entry.Content.Contains(SecretOnlyStatedInIterationOne));
        second.GetHistorySnapshot()
            .ShouldNotContain(entry => entry.Content != null && entry.Content.Contains(SecretOnlyStatedInIterationOne));
        second.GetHistorySnapshot().Count.ShouldBe(2);
    }

    [Fact]
    public async Task EachIteration_BindsTheConversationsActiveSessionToTheNewSession()
    {
        var conversations = new InMemoryConversationStore();
        var sessions = new InMemorySessionStore();
        var supervisor = new StubSupervisor(() => "ok");
        var runner = new RalphIterationRunner(supervisor, sessions, conversations, NullLogger<RalphIterationRunner>.Instance);

        var conversation = ConversationFactory.CreateForRalph(
            ConversationId.From("conv:ralph-active"), Agent, "do the work");
        await conversations.CreateAsync(conversation);

        await runner.RunIterationAsync(conversation, "do the work", 1);

        var reloaded = await conversations.GetAsync(conversation.ConversationId);
        reloaded!.ActiveSessionId.ShouldBe(supervisor.Sessions[0]);
    }
}
