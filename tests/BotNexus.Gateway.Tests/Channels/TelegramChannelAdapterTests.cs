using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Extensions.Channels.Telegram;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
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
            .Where(m => m.ChannelAddress == "42" && m.Content == "hello")
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
            ChannelAddress = "42",
            Content = new string('a', 5000)
        });

        var messageCalls = calls.Where(c => c.MethodName == "sendMessage").ToList();
        messageCalls.Count().ShouldBe(2);
        messageCalls[0].Text.Length.ShouldBe(4096);
        messageCalls[1].Text.Length.ShouldBe(904);
    }

    [Fact]
    public async Task SendAsync_EscapesHtml()
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
            ChannelAddress = "42",
            Content = "a < b && c > d"
        });

        sendCall.ShouldNotBeNull();
        sendCall!.Text.ShouldBe("a &lt; b &amp;&amp; c &gt; d");
        sendCall.ParseMode.ShouldBe("HTML");
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
                                Text = "once"
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
        calls.Where(c => c.MethodName == "editMessageText").Last().Text.ShouldContain(new string('a', 101));
        calls.Where(c => c.MethodName == "editMessageText").Last().Text.ShouldContain(new string('b', 101));
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
            ChannelAddress = "42",
            ThreadId = "1",
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
            ChannelAddress = "42",
            ThreadId = "42",
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
    public async Task SendAsync_HtmlParseMode_used()
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
            ChannelAddress = "42",
            Content = "hello"
        });

        sendCall.ShouldNotBeNull();
        sendCall!.ParseMode.ShouldBe("HTML");
    }

    [Fact]
    public async Task SendAsync_HtmlSendFails_FallsBackToPlainText()
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
            ChannelAddress = "42",
            Content = "hello"
        });

        sendCalls.Count.ShouldBe(2);
        sendCalls[0].ParseMode.ShouldBe("HTML");
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
        sendCalls[0].Text.ShouldContain("first");
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
            ChannelAddress = "99",
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
            AgentId = "larry",
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

        message.TargetAgentId.ShouldBe("larry");
        message.ChannelAddress.ShouldBe("42");
        message.Content.ShouldBe("hello");
    }

    private static TelegramChannelAdapter CreateAdapter(TelegramGatewayOptions options, HttpMessageHandler handler)
    {
        var factory = new StubHttpClientFactory(_ => new HttpClient(handler));
        return new TelegramChannelAdapter(
            NullLogger<TelegramChannelAdapter>.Instance,
            Options.Create(options),
            factory);
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

