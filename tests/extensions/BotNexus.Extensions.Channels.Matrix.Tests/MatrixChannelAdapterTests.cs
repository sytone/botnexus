using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.Matrix.Tests.Fakes;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Extensions.Channels.Matrix.Tests;

/// <summary>
/// Behavioural tests for <see cref="MatrixChannelAdapter"/>: account materialisation, inbound
/// dispatch from <c>/sync</c>, outbound send, auto-join, and streaming edits. All tests run against
/// <see cref="FakeMatrixClient"/> — no network.
/// </summary>
public sealed class MatrixChannelAdapterTests
{
    private const string Room = "!room1:example.com";
    private const string AgentUser = "@farnsworth:example.com";
    private const string HumanUser = "@jon:example.com";

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static MatrixChannelOptions BuildOptions(Action<MatrixAccountConfig>? tweak = null)
    {
        var account = new MatrixAccountConfig
        {
            UserId = AgentUser,
            AccessToken = "syt_fake_token",
            AgentId = "farnsworth",
        };

        tweak?.Invoke(account);

        var options = new MatrixChannelOptions { Homeserver = "https://matrix.example.com" };
        options.Agents["farnsworth"] = account;
        return options;
    }

    private static MatrixChannelAdapter CreateAdapter(
        MatrixChannelOptions options,
        FakeMatrixClientFactory factory) =>
        new(
            NullLogger<MatrixChannelAdapter>.Instance,
            new OptionsWrapper<MatrixChannelOptions>(options),
            factory);

    private static Mock<IChannelDispatcher> CreateDispatcher()
    {
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return dispatcher;
    }

    private static List<InboundMessage> Dispatched(Mock<IChannelDispatcher> dispatcher) =>
        dispatcher.Invocations
            .Where(i => i.Method.Name == nameof(IChannelDispatcher.DispatchAsync))
            .Select(i => (InboundMessage)i.Arguments[0])
            .ToList();

    private static MatrixSyncResponse SyncWithMessage(
        string sender,
        string body,
        string msgType = "m.text",
        string room = Room,
        MatrixRelatesTo? relatesTo = null,
        string nextBatch = "batch-1") =>
        new()
        {
            NextBatch = nextBatch,
            Rooms = new MatrixSyncRooms
            {
                Join = new Dictionary<string, MatrixJoinedRoom>
                {
                    [room] = new()
                    {
                        Timeline = new MatrixTimeline
                        {
                            Events =
                            [
                                new MatrixEvent
                                {
                                    Type = "m.room.message",
                                    Sender = sender,
                                    EventId = "$evt1",
                                    OriginServerTs = 1_700_000_000_000,
                                    Content = new MatrixMessageContent
                                    {
                                        MsgType = msgType,
                                        Body = body,
                                        RelatesTo = relatesTo,
                                    },
                                },
                            ],
                        },
                    },
                },
            },
        };

    /// <summary>
    /// Drives one sync batch through the adapter's processing path without starting the background
    /// loop, so assertions are deterministic rather than timing-dependent.
    /// </summary>
    private static async Task<Mock<IChannelDispatcher>> ProcessAsync(
        MatrixChannelAdapter adapter,
        MatrixSyncResponse response,
        string accountName = "farnsworth")
    {
        var dispatcher = CreateDispatcher();
        await adapter.StartAsync(dispatcher.Object);

        var runtime = adapter.GetAccount(accountName);
        runtime.ShouldNotBeNull();

        await adapter.ProcessSyncResponseAsync(runtime!, response, CancellationToken.None);
        await adapter.StopAsync();

        return dispatcher;
    }

    // ── Account materialisation ────────────────────────────────────────────────

    [Fact]
    public void Accounts_CompleteConfig_IsMaterialisedWithItsCredentials()
    {
        var factory = new FakeMatrixClientFactory();
        var adapter = CreateAdapter(BuildOptions(), factory);

        adapter.GetAccountCount().ShouldBe(1);

        factory.Credentials.TryGetValue("farnsworth", out var creds).ShouldBeTrue();
        creds.Homeserver.ShouldBe("https://matrix.example.com");
        creds.UserId.ShouldBe(AgentUser);
        creds.AccessToken.Reveal().ShouldBe("syt_fake_token");
    }

    [Fact]
    public void Accounts_MissingAccessToken_IsSkippedRatherThanStartedWithoutCredentials()
    {
        var options = BuildOptions(a => a.AccessToken = null);
        var adapter = CreateAdapter(options, new FakeMatrixClientFactory());

        adapter.GetAccountCount().ShouldBe(0);
    }

    [Fact]
    public void Accounts_MissingUserId_IsSkipped()
    {
        var options = BuildOptions(a => a.UserId = null);
        var adapter = CreateAdapter(options, new FakeMatrixClientFactory());

        adapter.GetAccountCount().ShouldBe(0);
    }

    [Fact]
    public void Accounts_NoHomeserverAnywhere_IsSkipped()
    {
        var options = BuildOptions();
        options.Homeserver = null;

        var adapter = CreateAdapter(options, new FakeMatrixClientFactory());

        adapter.GetAccountCount().ShouldBe(0);
    }

    [Fact]
    public void Accounts_PerAccountHomeserverOverridesTheSharedOne()
    {
        var factory = new FakeMatrixClientFactory();
        var options = BuildOptions(a => a.Homeserver = "https://other.example.com");

        CreateAdapter(options, factory).GetAccountCount().ShouldBe(1);

        factory.Credentials["farnsworth"].Homeserver.ShouldBe("https://other.example.com");
    }

    [Fact]
    public void Accounts_AgentIdDefaultsToTheConfigurationKey()
    {
        var options = BuildOptions(a => a.AgentId = null);
        var adapter = CreateAdapter(options, new FakeMatrixClientFactory());

        adapter.GetAccount("farnsworth")!.AgentId.ShouldBe("farnsworth");
    }

    // ── Inbound dispatch ───────────────────────────────────────────────────────

    [Fact]
    public async Task Inbound_UserTextMessage_IsDispatchedWithMatrixFieldsMapped()
    {
        var factory = new FakeMatrixClientFactory();
        var adapter = CreateAdapter(BuildOptions(), factory);

        var dispatcher = await ProcessAsync(adapter, SyncWithMessage(HumanUser, "hello agent"));

        var message = Dispatched(dispatcher).ShouldHaveSingleItem();
        message.ChannelType.ShouldBe(ChannelKey.From("matrix"));
        message.SenderId.ShouldBe(HumanUser);
        message.Content.ShouldBe("hello agent");
        message.ChannelAddress.Value.ShouldBe(Room);
        message.RoutingHints.ShouldNotBeNull();
        message.RoutingHints!.RequestedAgentId!.Value.Value.ShouldBe("farnsworth");
        message.Metadata["matrixRoomId"].ShouldBe(Room);
        message.Metadata["matrixEventId"].ShouldBe("$evt1");
        message.Timestamp.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
    }

    [Fact]
    public async Task Inbound_ThreadedMessage_EncodesThreadRootIntoTheChannelAddress()
    {
        var adapter = CreateAdapter(BuildOptions(), new FakeMatrixClientFactory());

        var dispatcher = await ProcessAsync(
            adapter,
            SyncWithMessage(
                HumanUser,
                "in thread",
                relatesTo: new MatrixRelatesTo { RelType = "m.thread", EventId = "$root9" }));

        Dispatched(dispatcher).ShouldHaveSingleItem()
            .ChannelAddress.Value.ShouldBe($"{Room}/thread:$root9");
    }

    [Fact]
    public async Task Inbound_OwnMessage_IsNotDispatched()
    {
        // The account's own sends echo back on the next sync; dispatching them would feed the agent
        // its own output and loop indefinitely.
        var adapter = CreateAdapter(BuildOptions(), new FakeMatrixClientFactory());

        var dispatcher = await ProcessAsync(adapter, SyncWithMessage(AgentUser, "my own reply"));

        Dispatched(dispatcher).ShouldBeEmpty();
    }

    [Fact]
    public async Task Inbound_EditEvent_IsNotDispatchedAsANewTurn()
    {
        var adapter = CreateAdapter(BuildOptions(), new FakeMatrixClientFactory());

        var dispatcher = await ProcessAsync(
            adapter,
            SyncWithMessage(
                HumanUser,
                "* corrected",
                relatesTo: new MatrixRelatesTo { RelType = "m.replace", EventId = "$evt0" }));

        Dispatched(dispatcher).ShouldBeEmpty();
    }

    [Fact]
    public async Task Inbound_ImageMessage_IsSkippedBecauseMediaIsDeferred()
    {
        var adapter = CreateAdapter(BuildOptions(), new FakeMatrixClientFactory());

        var dispatcher = await ProcessAsync(adapter, SyncWithMessage(HumanUser, "photo.png", msgType: "m.image"));

        Dispatched(dispatcher).ShouldBeEmpty();
    }

    [Fact]
    public async Task Inbound_EmptyBody_IsNotDispatched()
    {
        var adapter = CreateAdapter(BuildOptions(), new FakeMatrixClientFactory());

        var dispatcher = await ProcessAsync(adapter, SyncWithMessage(HumanUser, "   "));

        Dispatched(dispatcher).ShouldBeEmpty();
    }

    [Fact]
    public async Task Inbound_NonMessageEvent_IsIgnored()
    {
        var adapter = CreateAdapter(BuildOptions(), new FakeMatrixClientFactory());

        var response = new MatrixSyncResponse
        {
            NextBatch = "b1",
            Rooms = new MatrixSyncRooms
            {
                Join = new Dictionary<string, MatrixJoinedRoom>
                {
                    [Room] = new()
                    {
                        Timeline = new MatrixTimeline
                        {
                            Events =
                            [
                                new MatrixEvent
                                {
                                    Type = "m.room.member",
                                    Sender = HumanUser,
                                    Content = new MatrixMessageContent { Body = "joined" },
                                },
                            ],
                        },
                    },
                },
            },
        };

        Dispatched(await ProcessAsync(adapter, response)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Inbound_SenderNotInTheUserAllowList_IsNotDispatched()
    {
        var options = BuildOptions(a => a.AllowedUserIds.Add("@someone-else:example.com"));
        var adapter = CreateAdapter(options, new FakeMatrixClientFactory());

        var dispatcher = await ProcessAsync(adapter, SyncWithMessage(HumanUser, "let me in"));

        Dispatched(dispatcher).ShouldBeEmpty();
    }

    [Fact]
    public async Task Inbound_RoomNotInTheRoomAllowList_IsNotDispatched()
    {
        var options = BuildOptions(a => a.AllowedRoomIds.Add("!other:example.com"));
        var adapter = CreateAdapter(options, new FakeMatrixClientFactory());

        var dispatcher = await ProcessAsync(adapter, SyncWithMessage(HumanUser, "wrong room"));

        Dispatched(dispatcher).ShouldBeEmpty();
    }

    [Fact]
    public async Task Inbound_DispatchSetsTypingIndicatorOn()
    {
        var factory = new FakeMatrixClientFactory();
        var adapter = CreateAdapter(BuildOptions(), factory);

        await ProcessAsync(adapter, SyncWithMessage(HumanUser, "hello"));

        factory.ClientFor("farnsworth").TypingCalls
            .ShouldContain(c => c.RoomId == Room && c.Typing);
    }

    [Fact]
    public async Task Inbound_TypingFailureDoesNotPreventDispatch()
    {
        // The indicator is cosmetic. A homeserver that rejects it must never cost the user their
        // message.
        var factory = new FakeMatrixClientFactory();
        factory.ClientFor("farnsworth").TypingFailure = new InvalidOperationException("typing rejected");

        var adapter = CreateAdapter(BuildOptions(), factory);
        var dispatcher = await ProcessAsync(adapter, SyncWithMessage(HumanUser, "still arrives"));

        Dispatched(dispatcher).ShouldHaveSingleItem().Content.ShouldBe("still arrives");
    }

    // ── Auto-join ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Invite_WithAutoJoinEnabled_JoinsTheRoom()
    {
        var factory = new FakeMatrixClientFactory();
        var adapter = CreateAdapter(BuildOptions(a => a.AutoJoin = true), factory);

        var response = new MatrixSyncResponse
        {
            NextBatch = "b1",
            Rooms = new MatrixSyncRooms
            {
                Invite = new Dictionary<string, MatrixInvitedRoom> { [Room] = new() },
            },
        };

        await ProcessAsync(adapter, response);

        factory.ClientFor("farnsworth").JoinedRooms.ShouldBe([Room]);
    }

    [Fact]
    public async Task Invite_WithAutoJoinDisabled_DoesNotJoin()
    {
        var factory = new FakeMatrixClientFactory();
        var adapter = CreateAdapter(BuildOptions(a => a.AutoJoin = false), factory);

        var response = new MatrixSyncResponse
        {
            NextBatch = "b1",
            Rooms = new MatrixSyncRooms
            {
                Invite = new Dictionary<string, MatrixInvitedRoom> { [Room] = new() },
            },
        };

        await ProcessAsync(adapter, response);

        factory.ClientFor("farnsworth").JoinedRooms.ShouldBeEmpty();
    }

    [Fact]
    public async Task Invite_FailedJoin_DoesNotAbortTheRestOfTheBatch()
    {
        // A joined-room timeline in the same response carries real user messages; letting a failed
        // join throw would drop them.
        var factory = new FakeMatrixClientFactory();
        factory.ClientFor("farnsworth").JoinFailure = new InvalidOperationException("join refused");

        var adapter = CreateAdapter(BuildOptions(), factory);

        var response = SyncWithMessage(HumanUser, "message in a joined room");
        response.Rooms!.Invite = new Dictionary<string, MatrixInvitedRoom> { ["!invited:example.com"] = new() };

        var dispatcher = await ProcessAsync(adapter, response);

        Dispatched(dispatcher).ShouldHaveSingleItem()
            .Content.ShouldBe("message in a joined room");
    }

    // ── Outbound send ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Send_PlainMessage_IsSentToTheDecodedRoom()
    {
        var factory = new FakeMatrixClientFactory();
        var adapter = CreateAdapter(BuildOptions(), factory);
        await adapter.StartAsync(CreateDispatcher().Object);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("matrix"),
            ChannelAddress = MatrixChannelAddress.Encode(Room),
            Content = "the answer",
        });

        var sent = factory.ClientFor("farnsworth").SentMessages.ShouldHaveSingleItem();
        sent.RoomId.ShouldBe(Room);
        sent.Content.Body.ShouldBe("the answer");
        sent.Content.RelatesTo.ShouldBeNull();
    }

    [Fact]
    public async Task Send_ToAThreadAddress_CarriesTheThreadRelation()
    {
        var factory = new FakeMatrixClientFactory();
        var adapter = CreateAdapter(BuildOptions(), factory);
        await adapter.StartAsync(CreateDispatcher().Object);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("matrix"),
            ChannelAddress = MatrixChannelAddress.Encode(Room, "$root5"),
            Content = "threaded reply",
        });

        var sent = factory.ClientFor("farnsworth").SentMessages.ShouldHaveSingleItem();
        sent.Content.RelatesTo!.RelType.ShouldBe("m.thread");
        sent.Content.RelatesTo.EventId.ShouldBe("$root5");
    }

    [Fact]
    public async Task Send_AppliesTheDisplayPrefix()
    {
        var factory = new FakeMatrixClientFactory();
        var adapter = CreateAdapter(BuildOptions(), factory);
        await adapter.StartAsync(CreateDispatcher().Object);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("matrix"),
            ChannelAddress = MatrixChannelAddress.Encode(Room),
            Content = "body",
            DisplayPrefix = "[farnsworth] ",
        });

        factory.ClientFor("farnsworth").SentMessages.ShouldHaveSingleItem()
            .Content.Body.ShouldBe("[farnsworth] body");
    }

    [Fact]
    public async Task Send_UndecodableAddress_IsDroppedWithoutCallingTheHomeserver()
    {
        var factory = new FakeMatrixClientFactory();
        var adapter = CreateAdapter(BuildOptions(), factory);
        await adapter.StartAsync(CreateDispatcher().Object);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("matrix"),
            ChannelAddress = ChannelAddress.From(string.Empty),
            Content = "nowhere to go",
        });

        factory.ClientFor("farnsworth").SentMessages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Send_ToARoomOutsideTheAllowList_IsRefused()
    {
        var factory = new FakeMatrixClientFactory();
        var options = BuildOptions(a => a.AllowedRoomIds.Add("!permitted:example.com"));
        var adapter = CreateAdapter(options, factory);
        await adapter.StartAsync(CreateDispatcher().Object);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("matrix"),
            ChannelAddress = MatrixChannelAddress.Encode(Room),
            Content = "should not arrive",
        });

        factory.ClientFor("farnsworth").SentMessages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Send_ClearsTheTypingIndicator()
    {
        var factory = new FakeMatrixClientFactory();
        var adapter = CreateAdapter(BuildOptions(), factory);
        await adapter.StartAsync(CreateDispatcher().Object);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("matrix"),
            ChannelAddress = MatrixChannelAddress.Encode(Room),
            Content = "done",
        });

        factory.ClientFor("farnsworth").TypingCalls
            .ShouldContain(c => c.RoomId == Room && !c.Typing);
    }

    // ── Streaming ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Streaming_FirstDelta_SendsANewMessage()
    {
        var factory = new FakeMatrixClientFactory();
        var adapter = CreateAdapter(BuildOptions(), factory);
        await adapter.StartAsync(CreateDispatcher().Object);

        await adapter.SendStreamDeltaAsync(Target(), "Hello");

        var sent = factory.ClientFor("farnsworth").SentMessages.ShouldHaveSingleItem();
        sent.Content.Body.ShouldBe("Hello");
        sent.Content.RelatesTo.ShouldBeNull();
    }

    [Fact]
    public async Task Streaming_SubsequentDelta_EditsTheSameEventInPlace()
    {
        var factory = new FakeMatrixClientFactory();
        var options = BuildOptions();
        options.StreamingBufferMs = 0; // Flush on every delta so the test is deterministic.
        var adapter = CreateAdapter(options, factory);
        await adapter.StartAsync(CreateDispatcher().Object);

        var target = Target();
        await adapter.SendStreamDeltaAsync(target, "Hello");
        await adapter.SendStreamDeltaAsync(target, " world");

        var sent = factory.ClientFor("farnsworth").SentMessages;
        sent.Count.ShouldBe(2);

        // The edit replaces the event the first send created, and carries the FULL accumulated text
        // rather than just the delta.
        sent[1].Content.RelatesTo!.RelType.ShouldBe("m.replace");
        sent[1].Content.RelatesTo!.EventId.ShouldBe("$event1");
        sent[1].Content.NewContent!.Body.ShouldBe("Hello world");
    }

    [Fact]
    public async Task Streaming_WithinTheBufferWindow_DoesNotEditAgain()
    {
        var factory = new FakeMatrixClientFactory();
        var options = BuildOptions();
        options.StreamingBufferMs = 60_000; // Nothing should flush a second time inside this window.
        var adapter = CreateAdapter(options, factory);
        await adapter.StartAsync(CreateDispatcher().Object);

        var target = Target();
        await adapter.SendStreamDeltaAsync(target, "Hello");
        await adapter.SendStreamDeltaAsync(target, " world");

        factory.ClientFor("farnsworth").SentMessages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Streaming_EmptyDelta_SendsNothing()
    {
        var factory = new FakeMatrixClientFactory();
        var adapter = CreateAdapter(BuildOptions(), factory);
        await adapter.StartAsync(CreateDispatcher().Object);

        await adapter.SendStreamDeltaAsync(Target(), string.Empty);

        factory.ClientFor("farnsworth").SentMessages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Streaming_RunEnded_FlushesBufferedTextAndClearsTyping()
    {
        var factory = new FakeMatrixClientFactory();
        var options = BuildOptions();
        options.StreamingBufferMs = 60_000; // Force the tail to remain unflushed until completion.
        var adapter = CreateAdapter(options, factory);
        await adapter.StartAsync(CreateDispatcher().Object);

        var target = Target();
        await adapter.SendStreamDeltaAsync(target, "Hello");
        await adapter.SendStreamDeltaAsync(target, " world");

        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.RunEnded });

        var client = factory.ClientFor("farnsworth");
        client.SentMessages.Count.ShouldBe(2);
        client.SentMessages[1].Content.NewContent!.Body.ShouldBe("Hello world");
        client.TypingCalls.ShouldContain(c => c.RoomId == Room && !c.Typing);
    }

    [Fact]
    public async Task Streaming_ContentDeltaEvent_IsRenderedLikeARawDelta()
    {
        var factory = new FakeMatrixClientFactory();
        var adapter = CreateAdapter(BuildOptions(), factory);
        await adapter.StartAsync(CreateDispatcher().Object);

        await adapter.SendStreamEventAsync(
            Target(),
            new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "streamed" });

        factory.ClientFor("farnsworth").SentMessages.ShouldHaveSingleItem()
            .Content.Body.ShouldBe("streamed");
    }

    [Fact]
    public async Task Streaming_ThinkingDeltaEvent_IsNotRenderedBecauseTheCapabilityIsFalse()
    {
        var factory = new FakeMatrixClientFactory();
        var adapter = CreateAdapter(BuildOptions(), factory);
        adapter.SupportsThinkingDisplay.ShouldBeFalse();
        await adapter.StartAsync(CreateDispatcher().Object);

        await adapter.SendStreamEventAsync(
            Target(),
            new AgentStreamEvent { Type = AgentStreamEventType.ThinkingDelta, ThinkingContent = "pondering" });

        factory.ClientFor("farnsworth").SentMessages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Streaming_ConcurrentRequestsInOneRoom_DoNotShareAnAccumulator()
    {
        // Two in-flight turns addressed to the same room must edit two different events; sharing an
        // accumulator would splice one agent's text into the other's message.
        var factory = new FakeMatrixClientFactory();
        var options = BuildOptions();
        options.StreamingBufferMs = 0;
        var adapter = CreateAdapter(options, factory);
        await adapter.StartAsync(CreateDispatcher().Object);

        await adapter.SendStreamDeltaAsync(Target("req-a"), "alpha");
        await adapter.SendStreamDeltaAsync(Target("req-b"), "beta");

        var sent = factory.ClientFor("farnsworth").SentMessages;
        sent.Count.ShouldBe(2);
        sent[0].Content.Body.ShouldBe("alpha");
        sent[1].Content.Body.ShouldBe("beta");
        sent[1].Content.RelatesTo.ShouldBeNull();
    }

    [Fact]
    public void CanSendStreamEvent_RejectsAnUndecodableTarget()
    {
        var adapter = CreateAdapter(BuildOptions(), new FakeMatrixClientFactory());

        adapter.CanSendStreamEvent(Target()).ShouldBeTrue();
        adapter.CanSendStreamEvent(new ChannelStreamTarget(
            ConversationId.From("c_1"),
            SessionId.From("s_1"),
            ChannelAddress.From(string.Empty))).ShouldBeFalse();
    }

    // ── Capabilities ───────────────────────────────────────────────────────────

    [Fact]
    public void Capabilities_ReflectWhatTheSliceActuallyImplements()
    {
        var adapter = CreateAdapter(BuildOptions(), new FakeMatrixClientFactory());

        adapter.ChannelType.ShouldBe(ChannelKey.From("matrix"));
        adapter.SupportsStreaming.ShouldBeTrue();

        // Inbound media needs the content repository, which is deferred out of this slice, so the
        // adapter must not advertise a capability it cannot honour.
        adapter.SupportsInboundImages.ShouldBeFalse();
        adapter.SupportsToolDisplay.ShouldBeFalse();
    }

    private static ChannelStreamTarget Target(string? requestId = null) =>
        new(
            ConversationId.From("c_matrix"),
            SessionId.From("s_matrix"),
            MatrixChannelAddress.Encode(Room),
            BindingId: null,
            ChannelRequestId: requestId);
}
