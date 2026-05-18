using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway;
using BotNexus.Gateway.Abstractions.Activity;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Routing;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Conversations.Tests.Conversations;

public sealed class MultiChannelFanOutTests
{
    private const string AgentName = "agent-a";

    [Fact]
    public async Task SingleChannel_InboundMessage_ResponseSentOnce()
    {
        var harness = CreateHarness(responseContent: "single-channel-response");

        await harness.Host.DispatchAsync(harness.CreateMessage("hello", "signalr", "chat-1"));

        harness.SignalR.Messages.ShouldHaveSingleItem();
        harness.SignalR.Messages[0].Content.ShouldBe("single-channel-response");
        harness.SignalR.Messages[0].ChannelType.ShouldBe(ChannelKey.From("signalr"));
        harness.Telegram.Messages.ShouldBeEmpty();
        harness.Tui.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task SingleChannel_InboundMessage_BindingId_StampedFromConversation()
    {
        var harness = CreateHarness();
        var inbound = harness.CreateMessage("hello", "signalr", "chat-1");

        await harness.Host.DispatchAsync(inbound);

        var conversation = (await harness.Conversations.ListAsync(BotNexus.Domain.Primitives.AgentId.From(AgentName))).ShouldHaveSingleItem();
        conversation.ChannelBindings.ShouldHaveSingleItem();
        inbound.BindingId.ShouldBeNull();

        var session = (await harness.Sessions.ListAsync()).ShouldHaveSingleItem();
        session.Session.ConversationId.ShouldBe(conversation.ConversationId);

        var outboundBindings = await harness.Router.GetOutboundBindingsAsync(session.SessionId, originatingBindingId: null);
        outboundBindings.ShouldHaveSingleItem();
        outboundBindings[0].BindingId.ShouldBe(conversation.ChannelBindings[0].BindingId);
    }

    [Fact]
    public async Task TwoChannels_MessageFromSignalR_FansOutToTelegram()
    {
        var harness = CreateHarness(responseContent: "fanout");
        await harness.SeedSharedConversationAsync(("signalr", "chat-1"), ("telegram", "chat-100"));
        harness.ClearOutbound();

        await harness.Host.DispatchAsync(harness.CreateMessage("hello", "signalr", "chat-1"));

        // SignalR (originating): receives only the direct agent response
        harness.SignalR.Messages.Count.ShouldBe(1);
        harness.SignalR.Messages[0].ChannelAddress.ShouldBe(ChannelAddress.From("chat-1"));
        // Telegram (non-originating): receives user echo + agent response fan-out
        harness.Telegram.Messages.Count.ShouldBe(2);
        harness.Telegram.Messages.ShouldContain(m => m.ChannelAddress == ChannelAddress.From("chat-100"));
        harness.Tui.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task TwoChannels_MessageFromTelegram_FansOutToSignalR()
    {
        var harness = CreateHarness(responseContent: "fanout");
        await harness.SeedSharedConversationAsync(("signalr", "chat-1"), ("telegram", "chat-100"));
        harness.ClearOutbound();

        await harness.Host.DispatchAsync(harness.CreateMessage("hello", "telegram", "chat-100"));

        // SignalR (non-originating): receives user echo + agent response fan-out
        harness.SignalR.Messages.Count.ShouldBe(2);
        harness.SignalR.Messages.ShouldContain(m => m.ChannelAddress == ChannelAddress.From("chat-1"));
        // Telegram (originating): receives only the direct agent response
        harness.Telegram.Messages.Count.ShouldBe(1);
        harness.Telegram.Messages[0].ChannelAddress.ShouldBe(ChannelAddress.From("chat-100"));
    }

    [Fact]
    public async Task TwoChannels_MessageFromTelegram_TelegramReceivesExactlyOnce()
    {
        var harness = CreateHarness(responseContent: "fanout");
        await harness.SeedSharedConversationAsync(("signalr", "chat-1"), ("telegram", "chat-100"));
        harness.ClearOutbound();

        await harness.Host.DispatchAsync(harness.CreateMessage("hello", "telegram", "chat-100"));

        // Telegram (originating): only the direct agent response — no self-echo
        harness.Telegram.Messages.Count.ShouldBe(1);
        // SignalR (non-originating): user echo + agent response fan-out
        harness.SignalR.Messages.Count.ShouldBe(2);
    }

    [Fact]
    public async Task TwoChannels_NewConversation_BothChannelsGetSubsequentMessages()
    {
        // Explicitly bind both channels to the same conversation, then verify fan-out
        var harness = CreateHarness(responseContent: "follow-up");
        await harness.SeedSharedConversationAsync(("signalr", "chat-1"), ("telegram", "chat-100"));
        harness.ClearOutbound();

        await harness.Host.DispatchAsync(harness.CreateMessage("second", "signalr", "chat-1"));

        // SignalR (originating): direct agent response only
        harness.SignalR.Messages.Count.ShouldBe(1);
        harness.SignalR.Messages[0].Content.ShouldBe("follow-up");
        // Telegram (non-originating): user echo [0] + agent response fan-out [1]
        harness.Telegram.Messages.Count.ShouldBe(2);
        harness.Telegram.Messages.ShouldContain(m => m.Content == "follow-up");
    }

    [Fact]
    public async Task ThreeChannels_MessageFromA_FansOutToBAndC()
    {
        var harness = CreateHarness(responseContent: "matrix");
        await harness.SeedSharedConversationAsync(("signalr", "chat-1"), ("telegram", "chat-100"), ("tui", "terminal-1"));
        harness.ClearOutbound();

        await harness.Host.DispatchAsync(harness.CreateMessage("hello", "signalr", "chat-1"));

        // SignalR (originating): direct agent response only
        harness.SignalR.Messages.Count.ShouldBe(1);
        // Telegram and Tui (non-originating): user echo + agent response fan-out each
        harness.Telegram.Messages.Count.ShouldBe(2);
        harness.Tui.Messages.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ThreeChannels_MessageFromB_FansOutToAAndC()
    {
        var harness = CreateHarness(responseContent: "matrix");
        await harness.SeedSharedConversationAsync(("signalr", "chat-1"), ("telegram", "chat-100"), ("tui", "terminal-1"));
        harness.ClearOutbound();

        await harness.Host.DispatchAsync(harness.CreateMessage("hello", "telegram", "chat-100"));

        // SignalR and Tui (non-originating): user echo + agent response fan-out each
        harness.SignalR.Messages.Count.ShouldBe(2);
        // Telegram (originating): direct agent response only
        harness.Telegram.Messages.Count.ShouldBe(1);
        harness.Tui.Messages.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ThreeChannels_EachChannelSends_OthersTwoReceive_NoDuplicates()
    {
        var harness = CreateHarness(responseContent: "matrix");
        await harness.SeedSharedConversationAsync(("signalr", "chat-1"), ("telegram", "chat-100"), ("tui", "terminal-1"));

        await AssertPerSendAsync(harness, harness.CreateMessage("from-a", "signalr", "chat-1"), originatingChannel: "signalr");
        await AssertPerSendAsync(harness, harness.CreateMessage("from-b", "telegram", "chat-100"), originatingChannel: "telegram");
        await AssertPerSendAsync(harness, harness.CreateMessage("from-c", "tui", "terminal-1"), originatingChannel: "tui");
    }

    [Fact]
    public async Task TwoThreads_SameChat_IndependentConversations()
    {
        var harness = CreateHarness(responseContent: "threaded");
        await harness.SeedBindingAsync("telegram", "100", threadId: null);
        await harness.SeedBindingAsync("telegram", "100", threadId: "42");
        harness.ClearOutbound();

        await harness.Host.DispatchAsync(harness.CreateMessage("root", "telegram", "100", threadId: null));
        harness.Telegram.Messages.Count.ShouldBe(1);
        harness.Telegram.Messages[0].ThreadId.ShouldBeNull(); // root chat, no thread

        harness.ClearOutbound();
        await harness.Host.DispatchAsync(harness.CreateMessage("topic", "telegram", "100", threadId: "42"));
        harness.Telegram.Messages.Count.ShouldBe(1);
        harness.Telegram.Messages[0].ThreadId.ShouldBe(ThreadId.From("42")); // fix #126: ThreadId now correctly set on direct send

        var conversations = await harness.Conversations.ListAsync(BotNexus.Domain.Primitives.AgentId.From(AgentName));
        conversations.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Thread_MessageFromTelegram_FansOutWithCorrectThreadId()
    {
        var harness = CreateHarness(responseContent: "threaded");
        await harness.SeedBindingAsync("telegram", "100", threadId: "42");
        await harness.AttachBindingToConversationAsync("signalr", "chat-1", harness.GetConversationFor("telegram", "100", "42").ConversationId, threadId: "42");
        harness.ClearOutbound();

        await harness.Host.DispatchAsync(harness.CreateMessage("topic", "telegram", "100", threadId: "42"));

        // SignalR (non-originating): user echo [0] + agent response fan-out [1] — both carry ThreadId
        harness.SignalR.Messages.Count.ShouldBe(2);
        harness.SignalR.Messages.ShouldAllBe(m => m.ThreadId == ThreadId.From("42")); // all carry thread
        // Telegram (originating): direct agent response only
        harness.Telegram.Messages.Count.ShouldBe(1);
        harness.Telegram.Messages[0].ThreadId.ShouldBe(ThreadId.From("42")); // fix #126: direct send now carries ThreadId
    }

    [Fact]
    public async Task MutedBinding_DoesNotReceiveFanOut()
    {
        var harness = CreateHarness(responseContent: "muted");
        await harness.SeedBindingAsync("signalr", "chat-1");
        await harness.SeedBindingAsync("telegram", "chat-100");
        await harness.SetBindingModeAsync("telegram", "chat-100", null, BindingMode.Muted);
        harness.ClearOutbound();

        await harness.Host.DispatchAsync(harness.CreateMessage("hello", "signalr", "chat-1"));

        harness.SignalR.Messages.Count.ShouldBe(1);
        harness.Telegram.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task EmptyConversation_NoBindings_NoFanOut()
    {
        var harness = CreateHarness(responseContent: "orphan");
        var session = await harness.Sessions.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("session-empty"), BotNexus.Domain.Primitives.AgentId.From(AgentName));
        var conversation = new Conversation
        {
            ConversationId = BotNexus.Domain.Primitives.ConversationId.Create(),
            AgentId = BotNexus.Domain.Primitives.AgentId.From(AgentName),
            Title = "Empty",
            ActiveSessionId = session.SessionId
        };
        session.Session.ConversationId = conversation.ConversationId;
        await harness.Sessions.SaveAsync(session);
        await harness.Conversations.CreateAsync(conversation);

        await harness.Host.DispatchAsync(harness.CreateMessage("hello", "signalr", "chat-1", sessionId: "session-empty"));

        harness.SignalR.Messages.Count.ShouldBe(1);
        harness.Telegram.Messages.ShouldBeEmpty();
        harness.Tui.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Conversation_WhenOriginatingBindingIdIsNull_FansOutToAll()
    {
        var harness = CreateHarness(responseContent: "legacy-broken");
        var conversation = new Conversation
        {
            ConversationId = BotNexus.Domain.Primitives.ConversationId.Create(),
            AgentId = BotNexus.Domain.Primitives.AgentId.From(AgentName),
            Title = "Legacy",
            ActiveSessionId = BotNexus.Domain.Primitives.SessionId.From("legacy-session"),
            ChannelBindings =
            [
                new ChannelBinding { BindingId = BindingId.From("sig"), ChannelType = ChannelKey.From("signalr"), ChannelAddress = ChannelAddress.From("chat-1") },
                new ChannelBinding { BindingId = BindingId.From("tel"), ChannelType = ChannelKey.From("telegram"), ChannelAddress = ChannelAddress.From("chat-100") }
            ]
        };
        await harness.Conversations.CreateAsync(conversation);

        var session = await harness.Sessions.GetOrCreateAsync(BotNexus.Domain.Primitives.SessionId.From("legacy-session"), BotNexus.Domain.Primitives.AgentId.From(AgentName));
        session.Session.ConversationId = conversation.ConversationId;
        await harness.Sessions.SaveAsync(session);
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "legacy-broken" });
        await harness.Sessions.SaveAsync(session);

        await InvokeFanOutAsync(harness.Host, harness.CreateMessage("hello", "telegram", "chat-100"), "legacy-session");

        harness.SignalR.Messages.Count.ShouldBe(1);
        harness.Telegram.Messages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task DifferentChannelAddresses_GetSeparateConversations()
    {
        // Regression for #138: a Teams DM and a Telegram DM must not share the default conversation
        var harness = CreateHarness(responseContent: "isolated");

        await harness.Host.DispatchAsync(harness.CreateMessage("hello", "telegram", "chat-100"));
        await harness.Host.DispatchAsync(harness.CreateMessage("hello", "signalr", "browser-1"));

        var conversations = await harness.Conversations.ListAsync(BotNexus.Domain.Primitives.AgentId.From(AgentName));
        conversations.Count.ShouldBe(2, "each channel address must get its own conversation");

        var telegramConv = conversations.FirstOrDefault(c =>
            c.ChannelBindings.Any(b => b.ChannelType == ChannelKey.From("telegram")));
        var signalrConv = conversations.FirstOrDefault(c =>
            c.ChannelBindings.Any(b => b.ChannelType == ChannelKey.From("signalr")));

        telegramConv.ShouldNotBeNull();
        signalrConv.ShouldNotBeNull();
        telegramConv!.ConversationId.ShouldNotBe(signalrConv!.ConversationId);
    }

    [Fact]
    public async Task SameChannelAddress_SecondMessage_ReusesConversation()
    {
        // Same (channelType, channelAddress) must always route to the same conversation
        var harness = CreateHarness(responseContent: "reuse");

        await harness.Host.DispatchAsync(harness.CreateMessage("first", "telegram", "chat-100"));
        await harness.Host.DispatchAsync(harness.CreateMessage("second", "telegram", "chat-100"));

        var conversations = await harness.Conversations.ListAsync(BotNexus.Domain.Primitives.AgentId.From(AgentName));
        conversations.Count.ShouldBe(1, "same address must reuse the same conversation");
    }

    [Fact]
    public async Task GroupChat_AndDM_SameChannelType_GetSeparateConversations()
    {
        // A Telegram group (chat-id: -1001234) and a DM (chat-id: 1234567) must be separate
        var harness = CreateHarness(responseContent: "group-isolated");

        await harness.Host.DispatchAsync(harness.CreateMessage("hi from DM", "telegram", "1234567"));
        await harness.Host.DispatchAsync(harness.CreateMessage("hi from group", "telegram", "-1001234567"));

        var conversations = await harness.Conversations.ListAsync(BotNexus.Domain.Primitives.AgentId.From(AgentName));
        conversations.Count.ShouldBe(2, "DM and group chat must have separate conversations");
    }

    [Fact]
    public async Task NoChannelAddress_CreatesOwnConversation()
    {
        // Empty-address messages now get their own conversation like any other channel.
        // Multiple messages from the same empty-address channel reuse that conversation.
        var harness = CreateHarness(responseContent: "default");

        var msg1 = harness.CreateMessage("first", "signalr", "");
        var msg2 = harness.CreateMessage("second", "signalr", "");
        await harness.Host.DispatchAsync(msg1);
        await harness.Host.DispatchAsync(msg2);

        var conversations = await harness.Conversations.ListAsync(BotNexus.Domain.Primitives.AgentId.From(AgentName));
        conversations.Count.ShouldBe(1, "addressless messages from the same channel reuse the same conversation");
    }


    [Fact]
    public async Task CronSession_InSharedConversation_DoesNotFanOutUserEcho()
    {
        // Regression for SessionType guard: cron sessions must NOT echo user messages.
        // Before the fix, any inbound message (including cron jobs) would call FanOutUserMessageAsync.
        var harness = CreateHarness(responseContent: "cron-result");
        await harness.SeedSharedConversationAsync(("signalr", "chat-1"), ("telegram", "chat-100"));

        // Add a cron binding to the shared conversation so the router routes the cron
        // message to it. ResolveSessionType sees ChannelType == "cron" → SessionType.Cron → no user echo.
        var conversation = (await harness.Conversations.ListAsync(
            BotNexus.Domain.Primitives.AgentId.From(AgentName))).First();
        conversation.ChannelBindings.Add(new ChannelBinding
        {
            ChannelType = ChannelKey.From("cron"),
            ChannelAddress = ChannelAddress.From("cron-0"),
            Mode = BindingMode.Interactive
        });
        await harness.Conversations.SaveAsync(conversation);
        harness.ClearOutbound();

        await harness.Host.DispatchAsync(
            harness.CreateMessage("cron-task", "cron", "cron-0"));

        // Both bindings receive the agent response fan-out only (1 each) — NOT 2.
        harness.SignalR.Messages.Count.ShouldBe(1);
        harness.Telegram.Messages.Count.ShouldBe(1);
        harness.SignalR.Messages.ShouldNotContain(m => m.Role == MessageRole.User);
        harness.Telegram.Messages.ShouldNotContain(m => m.Role == MessageRole.User);
    }

    [Fact]
    public async Task SoulSession_InSharedConversation_DoesNotFanOutUserEcho()
    {
        // Regression for SessionType guard: soul sessions must NOT echo user messages.
        // To trigger the soul code path the router must return a soul session ID.
        // We achieve this by stamping the conversation's ActiveSessionId to a soul session
        // before dispatch — the back-compat router reuses it without recreating.
        var harness = CreateHarness(responseContent: "soul-result");
        await harness.SeedSharedConversationAsync(("signalr", "chat-1"), ("telegram", "chat-100"));

        var seededSession = (await harness.Sessions.ListAsync()).First();
        var soulId = seededSession.SessionId.Value + "::soul::eval";

        var soulSession = await harness.Sessions.GetOrCreateAsync(
            BotNexus.Domain.Primitives.SessionId.From(soulId),
            BotNexus.Domain.Primitives.AgentId.From(AgentName));
        soulSession.Session.ConversationId = seededSession.Session.ConversationId;
        await harness.Sessions.SaveAsync(soulSession);

        // Point the conversation at the soul session so the router returns it on next dispatch.
        var conversation = (await harness.Conversations.GetAsync(seededSession.Session.ConversationId!.Value))!;
        conversation.ActiveSessionId = BotNexus.Domain.Primitives.SessionId.From(soulId);
        await harness.Conversations.SaveAsync(conversation);
        harness.ClearOutbound();

        // Router finds the soul session as ActiveSessionId → ResolveSessionType returns Soul → no echo.
        await harness.Host.DispatchAsync(
            harness.CreateMessage("soul-turn", "signalr", "chat-1"));

        // Telegram (non-originating) should only receive agent response — no user echo
        harness.Telegram.Messages.Count.ShouldBe(1);
        harness.Telegram.Messages.ShouldNotContain(m => m.Role == MessageRole.User);
    }

    [Fact]
    public async Task SubAgentSession_InSharedConversation_DoesNotFanOutUserEcho()
    {
        // Regression for SessionType guard: sub-agent sessions must NOT echo user messages.
        // Same approach as SoulSession: stamp the conversation's ActiveSessionId so the
        // back-compat router reuses a sub-agent session on the next dispatch.
        var harness = CreateHarness(responseContent: "sub-result");
        await harness.SeedSharedConversationAsync(("signalr", "chat-1"), ("telegram", "chat-100"));

        var seededSession = (await harness.Sessions.ListAsync()).First();
        var subId = seededSession.SessionId.Value + "::subagent::helper";

        var subSession = await harness.Sessions.GetOrCreateAsync(
            BotNexus.Domain.Primitives.SessionId.From(subId),
            BotNexus.Domain.Primitives.AgentId.From(AgentName));
        subSession.Session.ConversationId = seededSession.Session.ConversationId;
        await harness.Sessions.SaveAsync(subSession);

        var conversation = (await harness.Conversations.GetAsync(seededSession.Session.ConversationId!.Value))!;
        conversation.ActiveSessionId = BotNexus.Domain.Primitives.SessionId.From(subId);
        await harness.Conversations.SaveAsync(conversation);
        harness.ClearOutbound();

        // Router finds the sub-agent session as ActiveSessionId → ResolveSessionType returns SubAgent → no echo.
        await harness.Host.DispatchAsync(
            harness.CreateMessage("sub-turn", "signalr", "chat-1"));

        // Telegram (non-originating) should only receive agent response — no user echo
        harness.Telegram.Messages.Count.ShouldBe(1);
        harness.Telegram.Messages.ShouldNotContain(m => m.Role == MessageRole.User);
    }

    [Fact]
    public async Task TwoChannels_UserEchoMessage_HasRoleUserAndOriginalContent()
    {
        // The mocked tests only count messages. This test verifies WHAT the echo contains.
        var harness = CreateHarness(responseContent: "agent-reply");
        await harness.SeedSharedConversationAsync(("signalr", "chat-1"), ("telegram", "chat-100"));
        harness.ClearOutbound();

        await harness.Host.DispatchAsync(harness.CreateMessage("user typed this", "signalr", "chat-1"));

        harness.Telegram.Messages.Count.ShouldBe(2);
        var echo = harness.Telegram.Messages.Single(m => m.Role == MessageRole.User);
        echo.Content.ShouldBe("user typed this");
        echo.ChannelAddress.ShouldBe(ChannelAddress.From("chat-100"));

        var response = harness.Telegram.Messages.Single(m => m.Role != MessageRole.User);
        response.Content.ShouldBe("agent-reply");
    }

    [Fact]
    public async Task TwoChannels_UserEchoDeliveredBeforeAgentResponse()
    {
        // Echo must arrive first so conversations appear in chronological order.
        var harness = CreateHarness(responseContent: "response");
        await harness.SeedSharedConversationAsync(("signalr", "chat-1"), ("telegram", "chat-100"));
        harness.ClearOutbound();

        await harness.Host.DispatchAsync(harness.CreateMessage("hi", "signalr", "chat-1"));

        harness.Telegram.Messages.Count.ShouldBe(2);
        harness.Telegram.Messages[0].Role.ShouldBe(MessageRole.User);
        harness.Telegram.Messages[1].Role.ShouldBeNull();
    }

    [Fact]
    public async Task FanOut_WhenAdapterMissingForBinding_OtherBindingsStillReceiveEcho()
    {
        // When a binding exists for a channel type with no registered adapter, the fan-out
        // skips that binding gracefully and continues to deliver to the remaining bindings.
        var harness = CreateHarness(responseContent: "response");
        await harness.SeedSharedConversationAsync(("signalr", "chat-1"), ("telegram", "chat-100"));

        // Inject a binding for "sms" — an unregistered channel type
        var conversation = (await harness.Conversations.ListAsync(
            BotNexus.Domain.Primitives.AgentId.From(AgentName))).First();
        conversation.ChannelBindings.Add(new ChannelBinding
        {
            ChannelType = ChannelKey.From("sms"),
            ChannelAddress = ChannelAddress.From("+15551234567"),
            Mode = BindingMode.Interactive
        });
        await harness.Conversations.SaveAsync(conversation);
        harness.ClearOutbound();

        await Should.NotThrowAsync(() =>
            harness.Host.DispatchAsync(harness.CreateMessage("hello", "signalr", "chat-1")));

        // Telegram still receives both user echo and agent response despite "sms" having no adapter
        harness.Telegram.Messages.Count.ShouldBe(2);
        harness.Telegram.Messages.ShouldContain(m => m.Role == MessageRole.User);
    }

    [Fact]
    public async Task FanOut_WhenOneAdapterThrowsOnEcho_OtherBindingsStillReceive()
    {
        // Per-binding try/catch: if one adapter throws during echo, others must still receive it.
        var harness = CreateHarness(responseContent: "response");
        await harness.SeedSharedConversationAsync(("signalr", "chat-1"), ("telegram", "chat-100"), ("tui", "terminal-1"));

        // Override telegram to always throw — simulates a flaky adapter
        harness.Telegram.Adapter
            .Setup(c => c.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Telegram API failure"));
        harness.ClearOutbound();

        // Must NOT propagate — per-binding errors are caught and logged
        await Should.NotThrowAsync(() =>
            harness.Host.DispatchAsync(harness.CreateMessage("hi", "signalr", "chat-1")));

        // Tui (non-throwing, non-originating) still receives both user echo and agent response
        harness.Tui.Messages.Count.ShouldBe(2);
        harness.Tui.Messages.ShouldContain(m => m.Role == MessageRole.User);
    }

    private static async Task AssertPerSendAsync(TestHarness harness, InboundMessage message, string originatingChannel)
    {
        harness.ClearOutbound();
        await harness.Host.DispatchAsync(message);
        // Originating channel receives only the direct agent response (1 message).
        // Non-originating channels receive the user echo + agent response fan-out (2 messages each).
        harness.SignalR.Messages.Count.ShouldBe(originatingChannel == "signalr" ? 1 : 2);
        harness.Telegram.Messages.Count.ShouldBe(originatingChannel == "telegram" ? 1 : 2);
        harness.Tui.Messages.Count.ShouldBe(originatingChannel == "tui" ? 1 : 2);
    }

    private static TestHarness CreateHarness(string responseContent = "agent-response")
    {
        var messageRouter = new Mock<IMessageRouter>();
        messageRouter.Setup(r => r.ResolveAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([AgentName]);

        var handle = new Mock<IAgentHandle>();
        handle.SetupGet(h => h.AgentId).Returns(AgentName);
        handle.SetupGet(h => h.SessionId).Returns("dynamic");
        handle.Setup(h => h.IsRunning).Returns(false);
        handle.Setup(h => h.PromptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = responseContent });
        handle.Setup(h => h.PromptAsync(It.IsAny<UserMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = responseContent });

        var supervisor = new Mock<IAgentSupervisor>();
        supervisor.Setup(s => s.GetOrCreateAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handle.Object);
        supervisor.Setup(s => s.StopAsync(It.IsAny<BotNexus.Domain.Primitives.AgentId>(), It.IsAny<BotNexus.Domain.Primitives.SessionId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sessions = new InMemorySessionStore();
        var conversations = new InMemoryConversationStore();
        var router = new DefaultConversationRouter(conversations, sessions, NullLogger<DefaultConversationRouter>.Instance);

        var signalr = CreateChannelRecorder("signalr");
        var telegram = CreateChannelRecorder("telegram");
        var tui = CreateChannelRecorder("tui");
        var manager = CreateChannelManager(signalr.Adapter.Object, telegram.Adapter.Object, tui.Adapter.Object);

        var host = new GatewayHost(
            supervisor.Object,
            messageRouter.Object,
            sessions,
            new RecordingActivityBroadcaster(),
            manager,
            Mock.Of<BotNexus.Gateway.Abstractions.Sessions.ISessionCompactor>(),
            new TestOptionsMonitor<BotNexus.Gateway.Abstractions.Sessions.CompactionOptions>(new BotNexus.Gateway.Abstractions.Sessions.CompactionOptions()),
            NullLogger<GatewayHost>.Instance,
            conversationRouter: router);

        return new TestHarness(host, router, sessions, conversations, signalr, telegram, tui);
    }

    private static ChannelRecorder CreateChannelRecorder(string channelType)
    {
        var messages = new List<OutboundMessage>();
        var adapter = new Mock<IChannelAdapter>();
        adapter.SetupGet(c => c.ChannelType).Returns(ChannelKey.From(channelType));
        adapter.SetupGet(c => c.DisplayName).Returns(channelType);
        adapter.SetupGet(c => c.SupportsStreaming).Returns(false);
        adapter.Setup(c => c.SendAsync(It.IsAny<OutboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutboundMessage, CancellationToken>((m, _) => messages.Add(m))
            .Returns(Task.CompletedTask);
        adapter.Setup(c => c.SendStreamDeltaAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return new ChannelRecorder(adapter, messages);
    }

    private static IChannelManager CreateChannelManager(params IChannelAdapter[] adapters)
    {
        var manager = new Mock<IChannelManager>();
        manager.SetupGet(m => m.Adapters).Returns(adapters);
        manager.Setup(m => m.Get(It.IsAny<ChannelKey>()))
            .Returns((ChannelKey key) => adapters.FirstOrDefault(a => a.ChannelType == key));
        return manager.Object;
    }

    private sealed record ChannelRecorder(Mock<IChannelAdapter> Adapter, List<OutboundMessage> Messages);

    private sealed class TestHarness(
        GatewayHost host,
        DefaultConversationRouter router,
        InMemorySessionStore sessions,
        InMemoryConversationStore conversations,
        ChannelRecorder signalr,
        ChannelRecorder telegram,
        ChannelRecorder tui)
    {
        public GatewayHost Host { get; } = host;
        public DefaultConversationRouter Router { get; } = router;
        public InMemorySessionStore Sessions { get; } = sessions;
        public InMemoryConversationStore Conversations { get; } = conversations;
        public ChannelRecorder SignalR { get; } = signalr;
        public ChannelRecorder Telegram { get; } = telegram;
        public ChannelRecorder Tui { get; } = tui;

        public InboundMessage CreateMessage(string content, string channelType, string channelAddress, string? threadId = null, string? sessionId = null)
            => new()
            {
                ChannelType = ChannelKey.From(channelType),
                SenderId = $"sender-{channelType}",
                ChannelAddress = ChannelAddress.From(channelAddress),
                Content = content,
                SessionId = sessionId,
                ThreadId = ThreadId.FromNullable(threadId),
                Metadata = new Dictionary<string, object?>()
            };

        public void ClearOutbound()
        {
            SignalR.Messages.Clear();
            Telegram.Messages.Clear();
            Tui.Messages.Clear();
        }

        public async Task SeedBindingAsync(string channelType, string channelAddress, string? threadId = null)
        {
            // Dispatch a seed message — the router will find or create the correct conversation
            // per (channelType, channelAddress, threadId). For fan-out tests that need multiple
            // channels in one conversation, use SeedSharedConversationAsync or AttachBindingToConversationAsync directly.
            await Host.DispatchAsync(CreateMessage($"seed-{channelType}", channelType, channelAddress, threadId));
        }

        /// <summary>
        /// Creates a shared conversation with all given (channelType, channelAddress) bindings.
        /// The first address seeds the conversation via dispatch; the rest are attached directly.
        /// Use this for fan-out tests where multiple channels must share one conversation.
        /// </summary>
        public async Task SeedSharedConversationAsync(params (string channelType, string channelAddress)[] bindings)
        {
            if (bindings.Length == 0) return;
            // First binding seeds the conversation
            await Host.DispatchAsync(CreateMessage($"seed-{bindings[0].channelType}", bindings[0].channelType, bindings[0].channelAddress));
            var conversations = await Conversations.ListAsync(BotNexus.Domain.Primitives.AgentId.From(AgentName));
            var conversation = conversations.Last();
            // Remaining bindings attached directly to the same conversation
            foreach (var (channelType, channelAddress) in bindings.Skip(1))
            {
                conversation.ChannelBindings.Add(new ChannelBinding
                {
                    ChannelType = ChannelKey.From(channelType),
                    ChannelAddress = ChannelAddress.From(channelAddress),
                    Mode = BindingMode.Interactive
                });
            }
            await Conversations.SaveAsync(conversation);
        }

        public Conversation GetConversationFor(string channelType, string channelAddress, string? threadId)
            => Conversations.ListAsync(BotNexus.Domain.Primitives.AgentId.From(AgentName)).Result
                .Single(c => c.ChannelBindings.Any(b =>
                    b.ChannelType == ChannelKey.From(channelType) &&
                    b.ChannelAddress == ChannelAddress.From(channelAddress) &&
                    b.ThreadId == ThreadId.FromNullable(threadId)));

        public async Task AttachBindingToConversationAsync(string channelType, string channelAddress, BotNexus.Domain.Primitives.ConversationId conversationId, string? threadId = null)
        {
            var conversation = (await Conversations.GetAsync(conversationId))!;
            conversation.ChannelBindings.Add(new ChannelBinding
            {
                ChannelType = ChannelKey.From(channelType),
                ChannelAddress = ChannelAddress.From(channelAddress),
                ThreadId = ThreadId.FromNullable(threadId),
                Mode = BindingMode.Interactive
            });
            await Conversations.SaveAsync(conversation);
        }

        public async Task SetBindingModeAsync(string channelType, string channelAddress, string? threadId, BindingMode mode)
        {
            var conversation = GetConversationFor(channelType, channelAddress, threadId);
            var binding = conversation.ChannelBindings.Single(b =>
                b.ChannelType == ChannelKey.From(channelType) &&
                b.ChannelAddress == ChannelAddress.From(channelAddress) &&
                b.ThreadId == ThreadId.FromNullable(threadId));
            binding.Mode = mode;
            await Conversations.SaveAsync(conversation);
        }
    }

    private static Task InvokeFanOutAsync(GatewayHost host, InboundMessage message, string sessionId)
    {
        var method = typeof(GatewayHost).GetMethod("FanOutResponseAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (Task)method.Invoke(host, [message, sessionId, CancellationToken.None])!;
    }

    private sealed class RecordingActivityBroadcaster : IActivityBroadcaster
    {
        public ValueTask PublishAsync(GatewayActivity activity, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<GatewayActivity> SubscribeAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (!cancellationToken.IsCancellationRequested)
                await Task.Delay(10, cancellationToken);

            yield break;
        }
    }
}

