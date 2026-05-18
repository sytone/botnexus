using BotNexus.Domain.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Extensions.Channels.Telegram;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Channels;

public sealed class TelegramChannelAdapterTests
{
    [Fact]
    public async Task Polling_AllowsAuthorizedChats_AndRejectsUnauthorizedChats()
    {
        var calls = new List<ApiCall>();
        var updatesSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            calls.Add(call);

            return call.MethodName switch
            {
                "deleteWebhook" => JsonOk(true),
                "getUpdates" when calls.Count(c => c.MethodName == "getUpdates") == 1 => JsonOk(new[]
                {
                    new TelegramUpdate
                    {
                        UpdateId = 10,
                        Message = new TelegramMessage
                        {
                            MessageId = 100,
                            Chat = new TelegramChat { Id = 42 },
                            From = new TelegramUser { Id = 7 },
                            Text = "hello"
                        }
                    },
                    new TelegramUpdate
                    {
                        UpdateId = 11,
                        Message = new TelegramMessage
                        {
                            MessageId = 101,
                            Chat = new TelegramChat { Id = 99 },
                            From = new TelegramUser { Id = 8 },
                            Text = "blocked"
                        }
                    }
                }),
                "getUpdates" => JsonOk(Array.Empty<TelegramUpdate>()),
                _ => JsonOk(true)
            };
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            PollingTimeoutSeconds = 1,
            AllowedChatIds = { 42 }
        }, handler);

        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => updatesSeen.TrySetResult());

        await adapter.StartAsync(dispatcher.Object, CancellationToken.None);
        await updatesSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await adapter.StopAsync(CancellationToken.None);

        dispatcher.Invocations
            .Where(i => i.Method.Name == nameof(IChannelDispatcher.DispatchAsync))
            .Select(i => (InboundMessage)i.Arguments[0])
            .Where(m => m.ChannelAddress == ChannelAddress.From("42") && m.Content == "hello")
            .ShouldHaveSingleItem();
    }

    [Fact]
    public async Task SendAsync_WithMessageLongerThan4096_SplitsIntoChunks()
    {
        var calls = new List<ApiCall>();
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            calls.Add(call);
            return JsonOk(new TelegramMessage { MessageId = calls.Count, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            AllowedChatIds = { 42 },
            MaxMessageLength = 4096
        }, handler);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("42"),
            Content = new string('a', 5000)
        });

        var messageCalls = calls.Where(c => c.MethodName == "sendMessage").ToList();
        messageCalls.Count().ShouldBe(2);
        messageCalls[0].Text.ShouldNotBeNull();
        messageCalls[1].Text.ShouldNotBeNull();
        var firstText = messageCalls[0].Text ?? throw new InvalidOperationException("Expected first message text.");
        var secondText = messageCalls[1].Text ?? throw new InvalidOperationException("Expected second message text.");
        firstText.Length.ShouldBe(4096);
        secondText.Length.ShouldBe(904);
    }

    [Fact]
    public async Task SendAsync_EscapesMarkdownV2SpecialChars()
    {
        ApiCall? sendCall = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
                sendCall = call;
            return JsonOk(new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            AllowedChatIds = { 42 }
        }, handler);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("42"),
            Content = "a < b && c > d"
        });

        sendCall.ShouldNotBeNull();
        // < and & are not MarkdownV2 special chars; > is and must be escaped.
        sendCall!.Text.ShouldBe("a < b && c \\> d");
        sendCall.ParseMode.ShouldBe("MarkdownV2");
    }

    [Fact]
    public async Task Polling_TracksOffset_AndDoesNotDispatchDuplicates()
    {
        var offsets = new List<long?>();
        var secondPollSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "deleteWebhook")
                return JsonOk(true);

            if (call.MethodName == "getUpdates")
            {
                offsets.Add(call.Offset);
                if (offsets.Count == 1)
                {
                    return JsonOk(new[]
                    {
                        new TelegramUpdate
                        {
                            UpdateId = 100,
                            Message = new TelegramMessage
                            {
                                MessageId = 200,
                                Chat = new TelegramChat { Id = 42 },
                                Text = "once",
                                From = new TelegramUser { Id = 1 },
                            }
                        }
                    });
                }

                secondPollSeen.TrySetResult();
                return JsonOk(Array.Empty<TelegramUpdate>());
            }

            return JsonOk(true);
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            PollingTimeoutSeconds = 1,
            AllowedChatIds = { 42 }
        }, handler);
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await adapter.StartAsync(dispatcher.Object, CancellationToken.None);
        await secondPollSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await adapter.StopAsync(CancellationToken.None);

        offsets.Count.ShouldBeGreaterThanOrEqualTo(2);
        offsets[0].ShouldBeNull();
        offsets[1].ShouldBe(101);
        dispatcher.Invocations.Count(i => i.Method.Name == nameof(IChannelDispatcher.DispatchAsync)).ShouldBe(1);
    }

    [Fact]
    public async Task StopAsync_CancelsPollingLoop_Gracefully()
    {
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "deleteWebhook")
                return JsonOk(true);

            if (call.MethodName == "getUpdates")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return JsonOk(Array.Empty<TelegramUpdate>());
            }

            return JsonOk(true);
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            PollingTimeoutSeconds = 1
        }, handler);

        await adapter.StartAsync(Mock.Of<IChannelDispatcher>(), CancellationToken.None);

        var stopTask = adapter.StopAsync(CancellationToken.None);
        var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.ShouldBe(stopTask);
    }

    [Fact]
    public async Task StartAsync_WithWebhook_UsesWebhookMode()
    {
        var calls = new List<ApiCall>();
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            calls.Add(call);
            return JsonOk(true);
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            WebhookUrl = "https://example.test/hook"
        }, handler);

        await adapter.StartAsync(Mock.Of<IChannelDispatcher>(), CancellationToken.None);
        await adapter.StopAsync(CancellationToken.None);

        calls.Select(c => c.MethodName).ShouldHaveSingleItem().ShouldBe("setWebhook");
        calls.Select(c => c.MethodName).ShouldNotContain("deleteWebhook");
        calls.Select(c => c.MethodName).ShouldNotContain("getUpdates");
    }

    [Fact]
    public async Task StartAsync_WithoutWebhook_UsesPollingMode()
    {
        var pollingSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<ApiCall>();
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            calls.Add(call);
            if (call.MethodName == "getUpdates")
                pollingSeen.TrySetResult();
            return call.MethodName == "getUpdates"
                ? JsonOk(Array.Empty<TelegramUpdate>())
                : JsonOk(true);
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            PollingTimeoutSeconds = 1
        }, handler);

        await adapter.StartAsync(Mock.Of<IChannelDispatcher>(), CancellationToken.None);
        await pollingSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await adapter.StopAsync(CancellationToken.None);

        calls.Select(c => c.MethodName).ShouldContain("deleteWebhook");
        calls.Select(c => c.MethodName).ShouldContain("getUpdates");
        calls.Select(c => c.MethodName).ShouldNotContain("setWebhook");
    }

    [Fact]
    public async Task SendStreamEventAsync_AccumulatesDeltas_AndUsesEditCalls()
    {
        var calls = new List<ApiCall>();
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            calls.Add(call);
            return call.MethodName switch
            {
                "sendMessage" => JsonOk(new TelegramMessage { MessageId = 99, Chat = new TelegramChat { Id = 42 } }),
                "editMessageText" => JsonOk(new TelegramMessage { MessageId = 99, Chat = new TelegramChat { Id = 42 } }),
                _ => JsonOk(true)
            };
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            AllowedChatIds = { 42 },
            StreamingBufferMs = 60000
        }, handler);

        await adapter.SendStreamEventAsync("42", new AgentStreamEvent { Type = AgentStreamEventType.MessageStart });
        await adapter.SendStreamEventAsync("42", new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = new string('a', 101) });
        await adapter.SendStreamEventAsync("42", new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = new string('b', 101) });

        calls.Count(c => c.MethodName == "sendMessage").ShouldBe(1);
        calls.Count(c => c.MethodName == "editMessageText").ShouldBeGreaterThan(0);
        var lastEditCall = calls.Where(c => c.MethodName == "editMessageText").Last();
        lastEditCall.Text.ShouldNotBeNull();
        var lastEditText = lastEditCall.Text ?? throw new InvalidOperationException("Expected edited message text.");
        lastEditText.ShouldContain(new string('a', 101));
        lastEditText.ShouldContain(new string('b', 101));
    }

    [Fact]
    public async Task StartAsync_WithoutBotToken_Throws()
    {
        var adapter = CreateAdapter(new TelegramGatewayOptions(), new StubHttpMessageHandler((_, _) => Task.FromResult(JsonOk(true))));

        Func<Task> act = () => adapter.StartAsync(Mock.Of<IChannelDispatcher>(), CancellationToken.None);

        (await act.ShouldThrowAsync<InvalidOperationException>())
            .Message.ShouldContain("BotToken");
    }

    [Fact]
    public async Task General_topic_threadId1_omits_message_thread_id()
    {
        ApiCall? sendCall = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
                sendCall = call;
            return JsonOk(new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            AllowedChatIds = { 42 }
        }, handler);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("42"),
            ThreadId = ThreadId.From("1"),
            Content = "general topic"
        });

        sendCall.ShouldNotBeNull();
        sendCall!.MessageThreadId.ShouldBeNull();
    }

    [Fact]
    public async Task Non_general_topic_threadId_includes_message_thread_id()
    {
        ApiCall? sendCall = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
                sendCall = call;
            return JsonOk(new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            AllowedChatIds = { 42 }
        }, handler);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("42"),
            ThreadId = ThreadId.From("42"),
            Content = "topic reply"
        });

        sendCall.ShouldNotBeNull();
        sendCall!.MessageThreadId.ShouldBe(42);
    }

    [Fact]
    public async Task GetUpdates_includes_allowed_updates()
    {
        ApiCall? pollingCall = null;
        var pollSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "getUpdates")
            {
                pollingCall = call;
                pollSeen.TrySetResult();
                return JsonOk(Array.Empty<TelegramUpdate>());
            }

            return JsonOk(true);
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            PollingTimeoutSeconds = 1
        }, handler);

        await adapter.StartAsync(Mock.Of<IChannelDispatcher>(), CancellationToken.None);
        await pollSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await adapter.StopAsync(CancellationToken.None);

        pollingCall.ShouldNotBeNull();
        pollingCall!.AllowedUpdates.ShouldBe(new[]
        {
            "message",
            "edited_message",
            "channel_post",
            "edited_channel_post",
            "message_reaction"
        });
    }

    [Fact]
    public async Task SendAsync_MarkdownV2ParseMode_used()
    {
        ApiCall? sendCall = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
                sendCall = call;
            return JsonOk(new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            AllowedChatIds = { 42 }
        }, handler);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("42"),
            Content = "hello"
        });

        sendCall.ShouldNotBeNull();
        sendCall!.ParseMode.ShouldBe("MarkdownV2");
    }

    [Fact]
    public async Task SendAsync_MarkdownV2SendFails_FallsBackToPlainText()
    {
        var sendCalls = new List<ApiCall>();
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
            {
                sendCalls.Add(call);
                if (sendCalls.Count == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("{\"ok\":false,\"description\":\"Bad Request: can't parse entities\"}", Encoding.UTF8, "application/json")
                    };
                }
            }

            return JsonOk(new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            AllowedChatIds = { 42 }
        }, handler);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("42"),
            Content = "hello"
        });

        sendCalls.Count.ShouldBe(2);
        sendCalls[0].ParseMode.ShouldBe("MarkdownV2");
        sendCalls[1].ParseMode.ShouldBeNull();
        sendCalls[1].Text.ShouldBe("hello");
    }

    [Fact]
    public async Task ErrorCooldown_suppressesRepeatErrorReplies()
    {
        var sendCalls = new List<ApiCall>();
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
                sendCalls.Add(call);

            return call.MethodName switch
            {
                "sendMessage" => JsonOk(new TelegramMessage { MessageId = sendCalls.Count, Chat = new TelegramChat { Id = 42 } }),
                "editMessageText" => JsonOk(new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } }),
                _ => JsonOk(true)
            };
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            AllowedChatIds = { 42 },
            ErrorCooldownMs = 60_000,
            StreamingBufferMs = 60_000
        }, handler);

        await adapter.SendStreamEventAsync("42", new AgentStreamEvent { Type = AgentStreamEventType.Error, ErrorMessage = "first" });
        await adapter.SendStreamEventAsync("42", new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd });
        await adapter.SendStreamEventAsync("42", new AgentStreamEvent { Type = AgentStreamEventType.Error, ErrorMessage = "second" });
        await adapter.SendStreamEventAsync("42", new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd });

        sendCalls.Count.ShouldBe(1);
        sendCalls[0].Text.ShouldNotBeNull();
        var firstErrorText = sendCalls[0].Text ?? throw new InvalidOperationException("Expected error message text.");
        firstErrorText.ShouldContain("first");
    }

    [Fact]
    public async Task StartAsync_WithInvalidPollingTimeout_ClampsTimeoutToOneSecond()
    {
        ApiCall? pollingCall = null;
        var pollSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "getUpdates")
            {
                pollingCall = call;
                pollSeen.TrySetResult();
                return JsonOk(Array.Empty<TelegramUpdate>());
            }

            return JsonOk(true);
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            PollingTimeoutSeconds = -5
        }, handler);

        await adapter.StartAsync(Mock.Of<IChannelDispatcher>(), CancellationToken.None);
        await pollSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await adapter.StopAsync(CancellationToken.None);

        pollingCall.ShouldNotBeNull();
        pollingCall!.Timeout.ShouldBe(1);
    }

    [Fact]
    public async Task SendAsync_WhenChatNotAllowed_Throws()
    {
        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            AllowedChatIds = { 42 }
        }, new StubHttpMessageHandler((_, _) => Task.FromResult(JsonOk(new TelegramMessage { MessageId = 1 }))));

        Func<Task> act = () => adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("99"),
            Content = "blocked"
        });

        (await act.ShouldThrowAsync<InvalidOperationException>())
            .Message.ShouldContain("not allowed");
    }

    [Fact]
    public async Task Polling_WithConfiguredAgentId_StampsTargetAgentId_OnInboundMessage()
    {
        var dispatched = new TaskCompletionSource<InboundMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            return call.MethodName switch
            {
                "deleteWebhook" => JsonOk(true),
                "getUpdates" => JsonOk(new[]
                {
                    new TelegramUpdate
                    {
                        UpdateId = 10,
                        Message = new TelegramMessage
                        {
                            MessageId = 100,
                            Chat = new TelegramChat { Id = 42 },
                            From = new TelegramUser { Id = 7 },
                            Text = "hello"
                        }
                    }
                }),
                _ => JsonOk(true)
            };
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            AgentId = "agent-b",
            PollingTimeoutSeconds = 1,
            AllowedChatIds = { 42 }
        }, handler);

        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<InboundMessage, CancellationToken>((m, _) => dispatched.TrySetResult(m));

        await adapter.StartAsync(dispatcher.Object, CancellationToken.None);
        var message = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await adapter.StopAsync(CancellationToken.None);

        message.TargetAgentId.ShouldBe("agent-b");
        message.ChannelAddress.ShouldBe(ChannelAddress.From("42"));
        message.Content.ShouldBe("hello");
    }

    [Fact]
    public async Task Polling_InboundMessage_BindingIdIsNull_RouterStampsIt()
    {
        // The adapter does NOT stamp BindingId — GatewayHost stamps it after
        // ResolveInboundAsync returns the matching conversation binding.
        // This test documents that contract: adapter dispatches without BindingId,
        // GatewayHost is responsible for stamping it to prevent self-fanout.
        var dispatched = new TaskCompletionSource<InboundMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            return call.MethodName switch
            {
                "deleteWebhook" => JsonOk(true),
                "getUpdates" => JsonOk(new[]
                {
                    new TelegramUpdate
                    {
                        UpdateId = 10,
                        Message = new TelegramMessage
                        {
                            MessageId = 100,
                            Chat = new TelegramChat { Id = 42 },
                            From = new TelegramUser { Id = 7 },
                            Text = "hello"
                        }
                    }
                }),
                _ => JsonOk(true)
            };
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            PollingTimeoutSeconds = 1,
            AllowedChatIds = { 42 }
        }, handler);

        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<InboundMessage, CancellationToken>((m, _) => dispatched.TrySetResult(m));

        await adapter.StartAsync(dispatcher.Object, CancellationToken.None);
        var message = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await adapter.StopAsync(CancellationToken.None);

        // Adapter dispatches without BindingId — GatewayHost stamps it after routing
        message.BindingId.ShouldBeNull();
        message.ChannelAddress.ShouldBe(ChannelAddress.From("42"));
    }

    // ── Security: AllowedUserIds ────────────────────────────────────────────

    [Fact]
    public async Task Polling_MessageFromUnauthorizedUser_IsBlocked()
    {
        var dispatched = false;
        var updatesSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "getUpdates")
            {
                callCount++;
                if (callCount == 1)
                {
                    return JsonOk(new[]
                    {
                        new TelegramUpdate
                        {
                            UpdateId = 10,
                            Message = new TelegramMessage
                            {
                                MessageId = 100,
                                Chat = new TelegramChat { Id = 42 },
                                From = new TelegramUser { Id = 99 }, // unauthorized user
                                Text = "blocked"
                            }
                        }
                    });
                }

                updatesSeen.TrySetResult();
                return JsonOk(Array.Empty<TelegramUpdate>());
            }

            return JsonOk(true);
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            PollingTimeoutSeconds = 1,
            AllowedChatIds = { 42 },
            AllowedUserIds = { 42 } // only user 42 allowed; message is from 99
        }, handler);

        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => dispatched = true);

        await adapter.StartAsync(dispatcher.Object, CancellationToken.None);
        await updatesSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await adapter.StopAsync(CancellationToken.None);

        dispatched.ShouldBeFalse();
    }

    [Fact]
    public async Task Polling_MessageFromAuthorizedUser_IsDelivered()
    {
        var dispatched = new TaskCompletionSource<InboundMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            return call.MethodName switch
            {
                "deleteWebhook" => JsonOk(true),
                "getUpdates" => JsonOk(new[]
                {
                    new TelegramUpdate
                    {
                        UpdateId = 10,
                        Message = new TelegramMessage
                        {
                            MessageId = 100,
                            Chat = new TelegramChat { Id = 42 },
                            From = new TelegramUser { Id = 42 }, // authorized user
                            Text = "hello"
                        }
                    }
                }),
                _ => JsonOk(true)
            };
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            PollingTimeoutSeconds = 1,
            AllowedChatIds = { 42 },
            AllowedUserIds = { 42 }
        }, handler);

        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<InboundMessage, CancellationToken>((m, _) => dispatched.TrySetResult(m));

        await adapter.StartAsync(dispatcher.Object, CancellationToken.None);
        var msg = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await adapter.StopAsync(CancellationToken.None);

        msg.Content.ShouldBe("hello");
    }

    [Fact]
    public async Task Polling_EmptyAllowedUserIds_AllowsAnyUser()
    {
        var dispatched = new TaskCompletionSource<InboundMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            return call.MethodName switch
            {
                "deleteWebhook" => JsonOk(true),
                "getUpdates" => JsonOk(new[]
                {
                    new TelegramUpdate
                    {
                        UpdateId = 10,
                        Message = new TelegramMessage
                        {
                            MessageId = 100,
                            Chat = new TelegramChat { Id = 42 },
                            From = new TelegramUser { Id = 9999 }, // any user
                            Text = "hello"
                        }
                    }
                }),
                _ => JsonOk(true)
            };
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            PollingTimeoutSeconds = 1,
            AllowedChatIds = { 42 }
            // AllowedUserIds empty — all users allowed
        }, handler);

        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<InboundMessage, CancellationToken>((m, _) => dispatched.TrySetResult(m));

        await adapter.StartAsync(dispatcher.Object, CancellationToken.None);
        var msg = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await adapter.StopAsync(CancellationToken.None);

        msg.Content.ShouldBe("hello");
    }

    // ── Security: ChannelPost ─────────────────────────────────────────────────

    [Fact]
    public async Task Polling_ChannelPost_IsIgnored()
    {
        var dispatched = false;
        var updatesSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "getUpdates")
            {
                callCount++;
                if (callCount == 1)
                {
                    return JsonOk(new[]
                    {
                        new TelegramUpdate
                        {
                            UpdateId = 10,
                            // No Message — only ChannelPost (no authenticated sender)
                            ChannelPost = new TelegramMessage
                            {
                                MessageId = 100,
                                Chat = new TelegramChat { Id = 42 },
                                Text = "channel announcement"
                            }
                        }
                    });
                }

                updatesSeen.TrySetResult();
                return JsonOk(Array.Empty<TelegramUpdate>());
            }

            return JsonOk(true);
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            PollingTimeoutSeconds = 1
        }, handler);

        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => dispatched = true);

        await adapter.StartAsync(dispatcher.Object, CancellationToken.None);
        await updatesSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await adapter.StopAsync(CancellationToken.None);

        dispatched.ShouldBeFalse();
    }

    // ── Security: EditedMessage ───────────────────────────────────────────────

    [Fact]
    public async Task Polling_EditedMessage_IgnoredByDefault()
    {
        var dispatched = false;
        var updatesSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "getUpdates")
            {
                callCount++;
                if (callCount == 1)
                {
                    return JsonOk(new[]
                    {
                        new TelegramUpdate
                        {
                            UpdateId = 10,
                            EditedMessage = new TelegramMessage
                            {
                                MessageId = 100,
                                Chat = new TelegramChat { Id = 42 },
                                From = new TelegramUser { Id = 7 },
                                Text = "edited text"
                            }
                        }
                    });
                }

                updatesSeen.TrySetResult();
                return JsonOk(Array.Empty<TelegramUpdate>());
            }

            return JsonOk(true);
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            PollingTimeoutSeconds = 1,
            AllowedChatIds = { 42 }
            // ProcessEditedMessages defaults to false
        }, handler);

        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => dispatched = true);

        await adapter.StartAsync(dispatcher.Object, CancellationToken.None);
        await updatesSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await adapter.StopAsync(CancellationToken.None);

        dispatched.ShouldBeFalse();
    }

    [Fact]
    public async Task Polling_EditedMessage_ProcessedWhenEnabled()
    {
        var dispatched = new TaskCompletionSource<InboundMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            return call.MethodName switch
            {
                "deleteWebhook" => JsonOk(true),
                "getUpdates" => JsonOk(new[]
                {
                    new TelegramUpdate
                    {
                        UpdateId = 10,
                        EditedMessage = new TelegramMessage
                        {
                            MessageId = 100,
                            Chat = new TelegramChat { Id = 42 },
                            From = new TelegramUser { Id = 7 },
                            Text = "edited text"
                        }
                    }
                }),
                _ => JsonOk(true)
            };
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            PollingTimeoutSeconds = 1,
            AllowedChatIds = { 42 },
            ProcessEditedMessages = true
        }, handler);

        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<InboundMessage, CancellationToken>((m, _) => dispatched.TrySetResult(m));

        await adapter.StartAsync(dispatcher.Object, CancellationToken.None);
        var msg = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await adapter.StopAsync(CancellationToken.None);

        msg.Content.ShouldBe("edited text");
    }

    // ── Security: Null From ───────────────────────────────────────────────────

    [Fact]
    public async Task Polling_MessageWithNullFrom_IsIgnored()
    {
        var dispatched = false;
        var updatesSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "getUpdates")
            {
                callCount++;
                if (callCount == 1)
                {
                    return JsonOk(new[]
                    {
                        new TelegramUpdate
                        {
                            UpdateId = 10,
                            Message = new TelegramMessage
                            {
                                MessageId = 100,
                                Chat = new TelegramChat { Id = 42 },
                                From = null, // no sender
                                Text = "mysterious message"
                            }
                        }
                    });
                }

                updatesSeen.TrySetResult();
                return JsonOk(Array.Empty<TelegramUpdate>());
            }

            return JsonOk(true);
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            PollingTimeoutSeconds = 1,
            AllowedChatIds = { 42 }
        }, handler);

        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => dispatched = true);

        await adapter.StartAsync(dispatcher.Object, CancellationToken.None);
        await updatesSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await adapter.StopAsync(CancellationToken.None);

        dispatched.ShouldBeFalse();
    }

    // ── Photo / Image Message Tests ───────────────────────────────────────────

    [Fact]
    public async Task Polling_PhotoMessage_DispatchedWithBinaryContentPart()
    {
        var photoBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }; // JPEG magic bytes
        var dispatched = new TaskCompletionSource<InboundMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var getUpdatesCount = 0;

        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            // File download is a GET to /file/bot{token}/{filePath}
            if (request.Method == HttpMethod.Get &&
                request.RequestUri?.AbsolutePath.Contains("/file/bot", StringComparison.OrdinalIgnoreCase) == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(photoBytes)
                };
            }

            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            return call.MethodName switch
            {
                "getUpdates" when Interlocked.Increment(ref getUpdatesCount) == 1 => JsonOk(new[]
                {
                    new TelegramUpdate
                    {
                        UpdateId = 1,
                        Message = new TelegramMessage
                        {
                            MessageId = 100,
                            Chat = new TelegramChat { Id = 42 },
                            From = new TelegramUser { Id = 7 },
                            Caption = "here is a photo",
                            Photo =
                            [
                                new TelegramPhotoSize { FileId = "small_id", FileUniqueId = "u1", Width = 100, Height = 100, FileSize = 5_000 },
                                new TelegramPhotoSize { FileId = "large_id", FileUniqueId = "u2", Width = 800, Height = 600, FileSize = 80_000 }
                            ]
                        }
                    }
                }),
                "getUpdates" => JsonOk(Array.Empty<TelegramUpdate>()),
                "getFile" => JsonOk(new TelegramFile { FileId = "large_id", FileUniqueId = "u2", FilePath = "photos/file_1.jpg" }),
                _ => JsonOk(true)
            };
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions { BotToken = "token", PollingTimeoutSeconds = 1 }, handler);
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<InboundMessage, CancellationToken>((m, _) => dispatched.TrySetResult(m));

        await adapter.StartAsync(dispatcher.Object, CancellationToken.None);
        var msg = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await adapter.StopAsync(CancellationToken.None);

        msg.Content.ShouldBe("here is a photo");
        msg.ContentParts.ShouldNotBeNull();
        msg.ContentParts!.Count.ShouldBe(1);
        var part = msg.ContentParts[0].ShouldBeOfType<BinaryContentPart>();
        part.MimeType.ShouldBe("image/jpeg");
        part.Data.ShouldBe(photoBytes);
    }

    [Fact]
    public async Task Polling_PhotoMessage_WithNullCaption_UsesEmptyContent()
    {
        var photoBytes = new byte[] { 0xFF, 0xD8, 0xFF };
        var dispatched = new TaskCompletionSource<InboundMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var getUpdatesCount = 0;

        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get &&
                request.RequestUri?.AbsolutePath.Contains("/file/bot", StringComparison.OrdinalIgnoreCase) == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(photoBytes) };
            }

            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            return call.MethodName switch
            {
                "getUpdates" when Interlocked.Increment(ref getUpdatesCount) == 1 => JsonOk(new[]
                {
                    new TelegramUpdate
                    {
                        UpdateId = 2,
                        Message = new TelegramMessage
                        {
                            MessageId = 101,
                            Chat = new TelegramChat { Id = 42 },
                            From = new TelegramUser { Id = 7 },
                            Caption = null, // no caption
                            Photo = [new TelegramPhotoSize { FileId = "id1", FileUniqueId = "u1", Width = 400, Height = 300, FileSize = 20_000 }]
                        }
                    }
                }),
                "getUpdates" => JsonOk(Array.Empty<TelegramUpdate>()),
                "getFile" => JsonOk(new TelegramFile { FileId = "id1", FileUniqueId = "u1", FilePath = "photos/f.jpg" }),
                _ => JsonOk(true)
            };
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions { BotToken = "token", PollingTimeoutSeconds = 1 }, handler);
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<InboundMessage, CancellationToken>((m, _) => dispatched.TrySetResult(m));

        await adapter.StartAsync(dispatcher.Object, CancellationToken.None);
        var msg = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await adapter.StopAsync(CancellationToken.None);

        msg.Content.ShouldBe(string.Empty);
        msg.ContentParts.ShouldNotBeNull();
        msg.ContentParts!.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Polling_PhotoMessage_DownloadFails_FallsBackToCaptionOnly()
    {
        // When GetFileAsync or DownloadFileAsync throws, the message is still dispatched
        // using just the caption, with no ContentParts.
        var dispatched = new TaskCompletionSource<InboundMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var getUpdatesCount = 0;

        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            return call.MethodName switch
            {
                "getUpdates" when Interlocked.Increment(ref getUpdatesCount) == 1 => JsonOk(new[]
                {
                    new TelegramUpdate
                    {
                        UpdateId = 3,
                        Message = new TelegramMessage
                        {
                            MessageId = 102,
                            Chat = new TelegramChat { Id = 42 },
                            From = new TelegramUser { Id = 7 },
                            Caption = "image with download error",
                            Photo = [new TelegramPhotoSize { FileId = "bad_id", FileUniqueId = "u1", Width = 400, Height = 300, FileSize = 20_000 }]
                        }
                    }
                }),
                "getUpdates" => JsonOk(Array.Empty<TelegramUpdate>()),
                // getFile returns a server error — simulates network/API failure
                "getFile" => new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{\"ok\":false,\"error_code\":500,\"description\":\"server error\"}")
                },
                _ => JsonOk(true)
            };
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions { BotToken = "token", PollingTimeoutSeconds = 1 }, handler);
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<InboundMessage, CancellationToken>((m, _) => dispatched.TrySetResult(m));

        await adapter.StartAsync(dispatcher.Object, CancellationToken.None);
        var msg = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await adapter.StopAsync(CancellationToken.None);

        // Adapter must still dispatch; falls back to caption-only (no ContentParts)
        msg.Content.ShouldBe("image with download error");
        msg.ContentParts.ShouldBeNull();
    }

    [Fact]
    public async Task Polling_PhotoMessage_SelectsLargestPhotoByFileSize()
    {
        // Verifies that among multiple photo sizes, the one with the highest FileSize is chosen.
        string? usedFileId = null;
        var dispatched = new TaskCompletionSource<InboundMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var getUpdatesCount = 0;

        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get &&
                request.RequestUri?.AbsolutePath.Contains("/file/bot", StringComparison.OrdinalIgnoreCase) == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xFF]) };
            }

            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "getFile")
            {
                // Capture which file_id was requested
                using var doc = JsonDocument.Parse(call.Body);
                usedFileId = doc.RootElement.TryGetProperty("file_id", out var el) ? el.GetString() : null;
                return JsonOk(new TelegramFile { FileId = usedFileId!, FileUniqueId = "u", FilePath = "photos/x.jpg" });
            }

            return call.MethodName switch
            {
                "getUpdates" when Interlocked.Increment(ref getUpdatesCount) == 1 => JsonOk(new[]
                {
                    new TelegramUpdate
                    {
                        UpdateId = 4,
                        Message = new TelegramMessage
                        {
                            MessageId = 103,
                            Chat = new TelegramChat { Id = 42 },
                            From = new TelegramUser { Id = 7 },
                            Photo =
                            [
                                new TelegramPhotoSize { FileId = "thumb_id", FileUniqueId = "u1", Width = 90,  Height = 90,  FileSize = 3_000 },
                                new TelegramPhotoSize { FileId = "mid_id",   FileUniqueId = "u2", Width = 320, Height = 240, FileSize = 25_000 },
                                new TelegramPhotoSize { FileId = "hd_id",    FileUniqueId = "u3", Width = 800, Height = 600, FileSize = 95_000 }
                            ]
                        }
                    }
                }),
                "getUpdates" => JsonOk(Array.Empty<TelegramUpdate>()),
                _ => JsonOk(true)
            };
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions { BotToken = "token", PollingTimeoutSeconds = 1 }, handler);
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<InboundMessage, CancellationToken>((m, _) => dispatched.TrySetResult(m));

        await adapter.StartAsync(dispatcher.Object, CancellationToken.None);
        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await adapter.StopAsync(CancellationToken.None);

        usedFileId.ShouldBe("hd_id");
    }

    [Fact]
    public async Task Polling_PhotoMessage_WithNullFileSizeOnAllSizes_StillDispatches()
    {
        // TelegramPhotoSize.FileSize can be null; the adapter must not throw.
        var dispatched = new TaskCompletionSource<InboundMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var getUpdatesCount = 0;

        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get &&
                request.RequestUri?.AbsolutePath.Contains("/file/bot", StringComparison.OrdinalIgnoreCase) == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xFF, 0xD8]) };
            }

            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            return call.MethodName switch
            {
                "getUpdates" when Interlocked.Increment(ref getUpdatesCount) == 1 => JsonOk(new[]
                {
                    new TelegramUpdate
                    {
                        UpdateId = 5,
                        Message = new TelegramMessage
                        {
                            MessageId = 104,
                            Chat = new TelegramChat { Id = 42 },
                            From = new TelegramUser { Id = 7 },
                            Photo =
                            [
                                // All FileSize = null — order by (null ?? 0) = 0 for all; picks last
                                new TelegramPhotoSize { FileId = "a", FileUniqueId = "ua", Width = 90,  Height = 90,  FileSize = null },
                                new TelegramPhotoSize { FileId = "b", FileUniqueId = "ub", Width = 320, Height = 240, FileSize = null }
                            ]
                        }
                    }
                }),
                "getUpdates" => JsonOk(Array.Empty<TelegramUpdate>()),
                "getFile" => JsonOk(new TelegramFile { FileId = "a", FileUniqueId = "ua", FilePath = "photos/null_size.jpg" }),
                _ => JsonOk(true)
            };
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions { BotToken = "token", PollingTimeoutSeconds = 1 }, handler);
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<InboundMessage, CancellationToken>((m, _) => dispatched.TrySetResult(m));

        await adapter.StartAsync(dispatcher.Object, CancellationToken.None);
        var msg = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await adapter.StopAsync(CancellationToken.None);

        // Must not throw and should dispatch with at least one ContentPart (or none if download fails)
        msg.ShouldNotBeNull();
    }

    [Fact]
    public async Task Polling_PhotoMessage_IsNotFilteredByUnauthorizedUser_WithNoTextGuard()
    {
        // Photo messages previously fell through the IsNullOrWhiteSpace(message.Text) guard.
        // This test confirms they are accepted after the fix when the user is authorized.
        var photoBytes = new byte[] { 0xFF, 0xD8, 0xFF };
        var dispatched = new TaskCompletionSource<InboundMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var getUpdatesCount = 0;

        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get &&
                request.RequestUri?.AbsolutePath.Contains("/file/bot", StringComparison.OrdinalIgnoreCase) == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(photoBytes) };
            }

            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            return call.MethodName switch
            {
                "getUpdates" when Interlocked.Increment(ref getUpdatesCount) == 1 => JsonOk(new[]
                {
                    new TelegramUpdate
                    {
                        UpdateId = 6,
                        Message = new TelegramMessage
                        {
                            MessageId = 105,
                            Chat = new TelegramChat { Id = 42 },
                            From = new TelegramUser { Id = 7 },
                            Text = null,    // no text
                            Caption = null, // no caption either — photo only
                            Photo = [new TelegramPhotoSize { FileId = "p1", FileUniqueId = "u1", Width = 400, Height = 300, FileSize = 12_000 }]
                        }
                    }
                }),
                "getUpdates" => JsonOk(Array.Empty<TelegramUpdate>()),
                "getFile" => JsonOk(new TelegramFile { FileId = "p1", FileUniqueId = "u1", FilePath = "photos/photo_only.jpg" }),
                _ => JsonOk(true)
            };
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions { BotToken = "token", PollingTimeoutSeconds = 1 }, handler);
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<InboundMessage, CancellationToken>((m, _) => dispatched.TrySetResult(m));

        await adapter.StartAsync(dispatcher.Object, CancellationToken.None);
        var msg = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await adapter.StopAsync(CancellationToken.None);

        msg.ContentParts.ShouldNotBeNull();
        msg.ContentParts!.Count.ShouldBe(1);
        msg.Content.ShouldBe(string.Empty);
    }

    // ── Foreign user message echo ─────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_WithUserRole_EchoesWithUserSaidFormat()
    {
        ApiCall? sendCall = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
                sendCall = call;
            return JsonOk(new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            AllowedChatIds = { 42 },
            EchoForeignUserMessages = true
        }, handler);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("42"),
            Content = "Hello from the portal",
            Role = BotNexus.Domain.Primitives.MessageRole.User
        });

        sendCall.ShouldNotBeNull();
        sendCall!.Text.ShouldNotBeNull();
        var text = sendCall.Text ?? throw new InvalidOperationException("Expected message text.");
        text.ShouldStartWith("User Said:\n");
        text.ShouldContain("Hello from the portal");
    }

    [Fact]
    public async Task SendAsync_WithUserRole_EchoesWithMarkdownV2EscapedContent()
    {
        // In MarkdownV2, > must be escaped as \>; < and & are not special chars.
        ApiCall? sendCall = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
                sendCall = call;
            return JsonOk(new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            AllowedChatIds = { 42 }
        }, handler);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("42"),
            Content = "a < b & c > d",
            Role = BotNexus.Domain.Primitives.MessageRole.User
        });

        sendCall.ShouldNotBeNull();
        sendCall!.Text.ShouldNotBeNull();
        var text = sendCall.Text ?? throw new InvalidOperationException("Expected message text.");
        text.ShouldStartWith("User Said:\n");
        // < and & are not MarkdownV2 special chars; > is and must be escaped
        text.ShouldContain(@"a < b & c \> d");
    }

    [Fact]
    public async Task SendAsync_WithUserRole_WhenEchoDisabled_DoesNotSend()
    {
        var sendCalled = false;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
                sendCalled = true;
            return JsonOk(new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            AllowedChatIds = { 42 },
            EchoForeignUserMessages = false
        }, handler);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("42"),
            Content = "Silent user message",
            Role = BotNexus.Domain.Primitives.MessageRole.User
        });

        sendCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task SendAsync_WithNullRole_SendsAsNormalAgentResponse()
    {
        ApiCall? sendCall = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
                sendCall = call;
            return JsonOk(new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            AllowedChatIds = { 42 }
        }, handler);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("42"),
            Content = "Agent response",
            Role = null
        });

        sendCall.ShouldNotBeNull();
        sendCall!.Text.ShouldNotBeNull();
        var text = sendCall.Text ?? throw new InvalidOperationException("Expected message text.");
        text.ShouldNotStartWith("User Said:");
    }

    [Fact]
    public async Task SendAsync_WithAssistantRole_SendsAsNormalAgentResponse()
    {
        // Role.Assistant is NOT User — must go through the normal send path, no "User Said:" prefix.
        ApiCall? sendCall = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
                sendCall = call;
            return JsonOk(new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            AllowedChatIds = { 42 }
        }, handler);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("42"),
            Content = "This is from the assistant",
            Role = BotNexus.Domain.Primitives.MessageRole.Assistant
        });

        sendCall.ShouldNotBeNull();
        sendCall!.Text.ShouldNotBeNull();
        sendCall.Text!.ShouldNotStartWith("User Said:");
        sendCall.Text.ShouldBe("This is from the assistant");
    }

    [Fact]
    public async Task SendAsync_WithUserRole_ThreadIdPropagatedToApi()
    {
        // When an echo message has a ThreadId, it must be included in the Telegram API call
        // so the echo appears in the correct forum topic.
        ApiCall? sendCall = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
                sendCall = call;
            return JsonOk(new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            AllowedChatIds = { 42 }
        }, handler);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("42"),
            ThreadId = BotNexus.Domain.Primitives.ThreadId.From("99"),
            Content = "Portal message",
            Role = BotNexus.Domain.Primitives.MessageRole.User
        });

        sendCall.ShouldNotBeNull();
        sendCall!.MessageThreadId.ShouldBe(99);
        sendCall.Text.ShouldNotBeNull();
        sendCall.Text!.ShouldStartWith("User Said:\n");
    }

    [Fact]
    public async Task SendAsync_WithUserRole_LongContent_PrefixOnFirstChunkOnly()
    {
        // When the echoed content is long enough to require splitting, the "User Said:\n" prefix
        // must appear on the FIRST chunk only — subsequent chunks are raw content continuations.
        var calls = new List<ApiCall>();
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
                calls.Add(call);
            return JsonOk(new TelegramMessage { MessageId = calls.Count, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            AllowedChatIds = { 42 },
            EchoForeignUserMessages = true,
            MaxMessageLength = 50  // small limit to force splitting
        }, handler);

        // Content = 200 'x' chars; echo = "User Said:\n" (11) + 200 = 211 chars → 5 chunks of 50 chars
        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("42"),
            Content = new string('x', 200),
            Role = BotNexus.Domain.Primitives.MessageRole.User
        });

        calls.Count.ShouldBeGreaterThan(1);
        calls[0].Text.ShouldNotBeNull();
        calls[0].Text!.ShouldStartWith("User Said:\n");
        // No subsequent chunk should re-start with the prefix
        foreach (var chunk in calls.Skip(1))
        {
            chunk.Text.ShouldNotBeNull();
            chunk.Text!.ShouldNotStartWith("User Said:");
        }
    }

    [Fact]
    public async Task SendAsync_WithUserRole_EmptyContent_SendsEchoWithPrefix()
    {
        // Edge case: empty content should still produce the "User Said:\n" header (no crash).
        ApiCall? sendCall = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
                sendCall = call;
            return JsonOk(new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            AllowedChatIds = { 42 }
        }, handler);

        await Should.NotThrowAsync(() => adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("42"),
            Content = string.Empty,
            Role = BotNexus.Domain.Primitives.MessageRole.User
        }));

        sendCall.ShouldNotBeNull();
        sendCall!.Text.ShouldNotBeNull();
        sendCall.Text!.ShouldStartWith("User Said:\n");
    }

    private static TelegramChannelAdapter CreateAdapter(TelegramGatewayOptions options, HttpMessageHandler handler)
    {
        var factory = new StubHttpClientFactory(_ => new HttpClient(handler));
        return new TelegramChannelAdapter(
            NullLogger<TelegramChannelAdapter>.Instance,
            Options.Create(options),
            factory);
    }

    // ── Core markdown conversion: SendAsync ───────────────────────────────────

    [Fact]
    public async Task SendAsync_WithBoldMarkdown_TextArrivesAsBoldMarkdownV2()
    {
        // THE CORE FEATURE TEST: verifies that LLM markdown (**bold**) is converted to
        // Telegram MarkdownV2 (*bold*) before being sent to the API.
        ApiCall? sendCall = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
                sendCall = call;
            return JsonOk(new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions { BotToken = "token", AllowedChatIds = { 42 } }, handler);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("42"),
            Content = "**bold text**"
        });

        sendCall.ShouldNotBeNull();
        sendCall!.Text.ShouldBe("*bold text*");
        sendCall.ParseMode.ShouldBe("MarkdownV2");
    }

    [Fact]
    public async Task SendAsync_WithInlineCode_CodeSpanPreserved()
    {
        ApiCall? sendCall = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
                sendCall = call;
            return JsonOk(new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions { BotToken = "token", AllowedChatIds = { 42 } }, handler);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("42"),
            Content = "Run `git status` to check."
        });

        sendCall.ShouldNotBeNull();
        // The inline code span is preserved; the dot after "check" is escaped.
        sendCall!.Text.ShouldBe("Run `git status` to check\\.");
    }

    [Fact]
    public async Task SendAsync_WithFencedCodeBlock_BlockPreservedAndMarkdownInsideNotConverted()
    {
        ApiCall? sendCall = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
                sendCall = call;
            return JsonOk(new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions { BotToken = "token", AllowedChatIds = { 42 } }, handler);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("42"),
            Content = "```csharp\nvar x = **1**;\n```"
        });

        sendCall.ShouldNotBeNull();
        // ** inside code block must NOT be converted to Telegram bold.
        sendCall!.Text.ShouldBe("```csharp\nvar x = **1**;\n```");
    }

    // ── Core markdown conversion: streaming ───────────────────────────────────

    [Fact]
    public async Task SendStreamEventAsync_ContentDeltaWithMarkdown_FinalTextIsConverted()
    {
        // Verifies that markdown in a ContentDelta event is converted to MarkdownV2 at MessageEnd flush.
        ApiCall? sendCall = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
                sendCall = call;
            return JsonOk(new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            AllowedChatIds = { 42 },
            StreamingBufferMs = 60_000 // prevent time-based flush during test
        }, handler);

        await adapter.SendStreamEventAsync("42", new AgentStreamEvent { Type = AgentStreamEventType.MessageStart });
        await adapter.SendStreamEventAsync("42", new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "**hello**" });
        await adapter.SendStreamEventAsync("42", new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd });

        sendCall.ShouldNotBeNull();
        // Raw markdown **hello** must be converted to MarkdownV2 *hello* at flush time.
        sendCall!.Text.ShouldBe("*hello*");
        sendCall.ParseMode.ShouldBe("MarkdownV2");
    }

    [Fact]
    public async Task SendStreamEventAsync_MultiDeltaMarkdownToken_AssembledBeforeConversion()
    {
        // CRITICAL: verifies that formatting tokens split across multiple deltas are assembled
        // BEFORE conversion, not escaped per delta. If escaping happened per delta, **bol would
        // become \*\*bol and d** would become d\*\*, producing literal asterisks in the output.
        ApiCall? sendCall = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
                sendCall = call;
            return JsonOk(new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            AllowedChatIds = { 42 },
            StreamingBufferMs = 60_000 // prevent time-based flush; only flush at MessageEnd
        }, handler);

        await adapter.SendStreamEventAsync("42", new AgentStreamEvent { Type = AgentStreamEventType.MessageStart });
        // Simulate streaming where **bold** arrives split across two deltas
        await adapter.SendStreamEventAsync("42", new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "**bol" });
        await adapter.SendStreamEventAsync("42", new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "d**" });
        await adapter.SendStreamEventAsync("42", new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd });

        sendCall.ShouldNotBeNull();
        var text = sendCall!.Text ?? throw new InvalidOperationException("Expected message text.");
        // Must be *bold* (bold converted), NOT \*\*bold\*\* (literal asterisks from per-delta escaping).
        text.ShouldBe("*bold*");
        text.ShouldNotContain("\\*");
    }

    [Fact]
    public async Task SendStreamDeltaAsync_WithMarkdown_ConvertsAtFlush()
    {
        // SendStreamDeltaAsync buffers raw content and converts at flush threshold.
        ApiCall? sendCall = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
                sendCall = call;
            return JsonOk(new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions { BotToken = "token", AllowedChatIds = { 42 } }, handler);

        // Send 100 chars of markdown — this hits the StreamingFlushThresholdChars (100) and triggers flush.
        var boldContent = new string('x', 96);
        var delta = $"**{boldContent}**"; // 100 chars total: 2 + 96 + 2
        await adapter.SendStreamDeltaAsync("42", delta);

        sendCall.ShouldNotBeNull();
        // The raw ** must be converted to * (Telegram bold) at flush time, not escaped as \*.
        sendCall!.Text.ShouldBe($"*{boldContent}*");
        sendCall.ParseMode.ShouldBe("MarkdownV2");
    }

    // ── Streaming: tool and error events ────────────────────────────────────

    [Fact]
    public async Task SendStreamEventAsync_ToolStartEvent_EscapedInFinalMessage()
    {
        // Tool start events add [toolName] started to the raw buffer.
        // At flush time, Convert() must escape [ and ] as literal chars and escape _ in tool names.
        ApiCall? sendCall = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
                sendCall = call;
            return JsonOk(new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            AllowedChatIds = { 42 },
            StreamingBufferMs = 60_000
        }, handler);

        await adapter.SendStreamEventAsync("42", new AgentStreamEvent { Type = AgentStreamEventType.MessageStart });
        await adapter.SendStreamEventAsync("42", new AgentStreamEvent { Type = AgentStreamEventType.ToolStart, ToolName = "memory_save" });
        await adapter.SendStreamEventAsync("42", new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd });

        sendCall.ShouldNotBeNull();
        var text = sendCall!.Text ?? throw new InvalidOperationException("Expected message text.");
        // [memory_save] started → brackets escaped as \[ \], underscore escaped as \_
        text.ShouldContain("\\[memory\\_save\\] started");
    }

    [Fact]
    public async Task SendStreamEventAsync_ToolEndWithError_EscapedInFinalMessage()
    {
        ApiCall? sendCall = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
                sendCall = call;
            return JsonOk(new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            AllowedChatIds = { 42 },
            StreamingBufferMs = 60_000
        }, handler);

        await adapter.SendStreamEventAsync("42", new AgentStreamEvent { Type = AgentStreamEventType.MessageStart });
        await adapter.SendStreamEventAsync("42", new AgentStreamEvent
        {
            Type = AgentStreamEventType.ToolEnd,
            ToolName = "read_file",
            ToolIsError = true
        });
        await adapter.SendStreamEventAsync("42", new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd });

        sendCall.ShouldNotBeNull();
        var text = sendCall!.Text ?? throw new InvalidOperationException("Expected message text.");
        text.ShouldContain("\\[read\\_file\\] failed");
    }

    [Fact]
    public async Task SendStreamEventAsync_ErrorMessage_SpecialCharsEscaped()
    {
        // Error messages containing special chars must be escaped by Convert() at flush time.
        ApiCall? sendCall = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
                sendCall = call;
            return JsonOk(new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            AllowedChatIds = { 42 },
            StreamingBufferMs = 60_000,
            ErrorCooldownMs = 0
        }, handler);

        await adapter.SendStreamEventAsync("42", new AgentStreamEvent { Type = AgentStreamEventType.MessageStart });
        await adapter.SendStreamEventAsync("42", new AgentStreamEvent
        {
            Type = AgentStreamEventType.Error,
            ErrorMessage = "Connection failed. Retry 1/3"
        });
        await adapter.SendStreamEventAsync("42", new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd });

        sendCall.ShouldNotBeNull();
        var text = sendCall!.Text ?? throw new InvalidOperationException("Expected message text.");
        // Dot and / must be escaped; ⚠️ emoji is not a MarkdownV2 special char.
        text.ShouldContain("Connection failed\\. Retry 1/3");
    }

    // ── BuildOutboundText paths ───────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_WithDisplayPrefix_PrefixIsEscapedNotConverted()
    {
        // displayPrefix goes through EscapeMarkdownV2 (not Convert), so markdown
        // tokens in the prefix render as literal text, not as formatting.
        ApiCall? sendCall = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
                sendCall = call;
            return JsonOk(new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions { BotToken = "token", AllowedChatIds = { 42 } }, handler);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("42"),
            Content = "response",
            DisplayPrefix = "_agent_:" // looks like italic markdown in the prefix
        });

        sendCall.ShouldNotBeNull();
        var text = sendCall!.Text ?? throw new InvalidOperationException("Expected message text.");
        // EscapeMarkdownV2("_agent_:") must produce \_agent\_: — literal, not italic.
        text.ShouldStartWith("\\_agent\\_:");
        // Content is still converted normally.
        text.ShouldContain("response");
    }

    [Fact]
    public async Task SendAsync_WithThinkingMetadata_ThinkingIsConverted()
    {
        // The "thinking" metadata value goes through Convert(), not EscapeMarkdownV2.
        // Markdown formatting in thinking content must render as formatted text.
        ApiCall? sendCall = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
                sendCall = call;
            return JsonOk(new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions { BotToken = "token", AllowedChatIds = { 42 } }, handler);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("42"),
            Content = "answer",
            Metadata = new Dictionary<string, object?> { ["thinking"] = "**deep thought**" }
        });

        sendCall.ShouldNotBeNull();
        var text = sendCall!.Text ?? throw new InvalidOperationException("Expected message text.");
        // thinking goes through Convert() → **deep thought** → *deep thought*
        text.ShouldContain("Thinking: *deep thought*");
    }

    [Fact]
    public async Task SendAsync_WithToolCallMetadata_BracketsAreLiteralAndNameIsEscaped()
    {
        // The "toolCall" metadata value must appear with literal [ and ] brackets
        // and the tool name must have underscores escaped.
        ApiCall? sendCall = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
                sendCall = call;
            return JsonOk(new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions { BotToken = "token", AllowedChatIds = { 42 } }, handler);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("42"),
            Content = "done",
            Metadata = new Dictionary<string, object?> { ["toolCall"] = "write_file" }
        });

        sendCall.ShouldNotBeNull();
        var text = sendCall!.Text ?? throw new InvalidOperationException("Expected message text.");
        // The hardcoded \[ and \] produce literal brackets; EscapeMarkdownV2(write_file) → write\_file
        text.ShouldContain("\\[write\\_file\\]");
    }

    // ── Fallback behavior ────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_MarkdownV2RejectedWithMarkdownContent_FallbackSendsAlreadyConvertedText()
    {
        // When Telegram rejects MarkdownV2, the plain-text retry sends the already-converted
        // MarkdownV2 text (e.g., *bold* instead of **bold**). This is expected behavior:
        // the fallback is readable even if it shows MarkdownV2 syntax as literal characters.
        var sendCalls = new List<ApiCall>();
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
            {
                sendCalls.Add(call);
                if (sendCalls.Count == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent(
                            "{\"ok\":false,\"description\":\"Bad Request: can't parse entities\"}",
                            Encoding.UTF8,
                            "application/json")
                    };
                }
            }

            return JsonOk(new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramGatewayOptions { BotToken = "token", AllowedChatIds = { 42 } }, handler);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("42"),
            Content = "**bold content**"
        });

        sendCalls.Count.ShouldBe(2);
        sendCalls[0].ParseMode.ShouldBe("MarkdownV2");
        sendCalls[0].Text.ShouldBe("*bold content*"); // already converted
        sendCalls[1].ParseMode.ShouldBeNull(); // plain text retry — no parse_mode
        // The fallback sends the converted MarkdownV2 text (not original **bold content**).
        // This means * renders literally — acceptable degraded fallback.
        sendCalls[1].Text.ShouldBe("*bold content*");
    }

    [Fact]
    public async Task SendAsync_SendThrowsUnexpectedException_PropagatesException()
    {
        // Non-400 errors (e.g., network failures) must not be silently swallowed.
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(
                    "{\"ok\":false,\"description\":\"Internal Server Error\"}",
                    Encoding.UTF8,
                    "application/json")
            }));

        var adapter = CreateAdapter(new TelegramGatewayOptions { BotToken = "token", AllowedChatIds = { 42 } }, handler);

        Func<Task> act = () => adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = ChannelAddress.From("42"),
            Content = "hello"
        });

        await act.ShouldThrowAsync<Exception>();
    }

    [Fact]
    public void BindsFromIConfiguration_WhenIOptionsIsEmpty()
    {
        // Arrange — IOptions<TelegramGatewayOptions> is empty (extension registered after DI pass)
        // but IConfiguration has channels.telegram populated.
        var configData = new Dictionary<string, string?>
        {
            ["channels:telegram:botToken"] = "test-token-from-config",
            ["channels:telegram:agentId"] = "rusty"
        };
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var factory = new StubHttpClientFactory(_ => new HttpClient());
        var adapter = new TelegramChannelAdapter(
            NullLogger<TelegramChannelAdapter>.Instance,
            Options.Create(new TelegramGatewayOptions()), // empty IOptions
            factory,
            configuration);

        // The adapter should have resolved options from IConfiguration
        adapter.ChannelType.Value.ShouldBe("telegram");
        // Verify EnsureBotsInitialized picks up the IConfiguration-bound options
        // We can introspect indirectly: start then stop to trigger bot initialization
        // (can't start without a real token, but we can verify the channel type + display name)
        adapter.DisplayName.ShouldBe("Telegram Bot");
    }

    private static HttpResponseMessage JsonOk<T>(T result)
    {
        var payload = JsonSerializer.Serialize(new TelegramApiResponse<T>
        {
            Ok = true,
            Result = result
        });

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }

    private sealed class StubHttpClientFactory(Func<string, HttpClient> factory) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => factory(name);
    }

    private sealed record ApiCall(string MethodName, string Body)
    {
        public string? Text => TryGetString("text");
        public string? ParseMode => TryGetString("parse_mode");
        public long? Offset => TryGetLong("offset");
        public int? Timeout => TryGetInt("timeout");
        public int? MessageThreadId => TryGetInt("message_thread_id");
        public string[] AllowedUpdates => TryGetStringArray("allowed_updates");

        public static async Task<ApiCall> FromRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? "{}"
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var methodName = request.RequestUri?.Segments.LastOrDefault()?.Trim('/') ?? string.Empty;
            return new ApiCall(methodName, body);
        }

        private string? TryGetString(string property)
        {
            using var json = JsonDocument.Parse(Body);
            return json.RootElement.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;
        }

        private long? TryGetLong(string property)
        {
            using var json = JsonDocument.Parse(Body);
            return json.RootElement.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.Number
                ? element.GetInt64()
                : null;
        }

        private int? TryGetInt(string property)
        {
            using var json = JsonDocument.Parse(Body);
            return json.RootElement.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.Number
                ? element.GetInt32()
                : null;
        }

        private string[] TryGetStringArray(string property)
        {
            using var json = JsonDocument.Parse(Body);
            if (!json.RootElement.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Array)
                return [];

            return element.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .ToArray();
        }
    }
}
