using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Extensions.Channels.Telegram;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using FluentAssertions;
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

        var adapter = CreateAdapter(new TelegramOptions
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
            .Should()
            .ContainSingle(m => m.ConversationId == "42" && m.Content == "hello");
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

        var adapter = CreateAdapter(new TelegramOptions
        {
            BotToken = "token",
            AllowedChatIds = { 42 },
            MaxMessageLength = 4096
        }, handler);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ConversationId = "42",
            Content = new string('a', 5000)
        });

        var messageCalls = calls.Where(c => c.MethodName == "sendMessage").ToList();
        messageCalls.Should().HaveCount(2);
        messageCalls[0].Text.Should().HaveLength(4096);
        messageCalls[1].Text.Should().HaveLength(904);
    }

    [Fact]
    public async Task SendAsync_EscapesMarkdown()
    {
        ApiCall? sendCall = null;
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var call = await ApiCall.FromRequestAsync(request, cancellationToken);
            if (call.MethodName == "sendMessage")
                sendCall = call;
            return JsonOk(new TelegramMessage { MessageId = 1, Chat = new TelegramChat { Id = 42 } });
        });

        var adapter = CreateAdapter(new TelegramOptions
        {
            BotToken = "token",
            AllowedChatIds = { 42 }
        }, handler);

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ConversationId = "42",
            Content = "_*[]()~`>#+-=|{}.!\\"
        });

        sendCall.Should().NotBeNull();
        sendCall!.Text.Should().Be("\\_\\*\\[\\]\\(\\)\\~\\`\\>\\#\\+\\-\\=\\|\\{\\}\\.\\!\\\\");
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

        var adapter = CreateAdapter(new TelegramOptions
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

        offsets.Count.Should().BeGreaterThanOrEqualTo(2);
        offsets[0].Should().BeNull();
        offsets[1].Should().Be(101);
        dispatcher.Invocations.Count(i => i.Method.Name == nameof(IChannelDispatcher.DispatchAsync)).Should().Be(1);
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

        var adapter = CreateAdapter(new TelegramOptions
        {
            BotToken = "token",
            PollingTimeoutSeconds = 1
        }, handler);

        await adapter.StartAsync(Mock.Of<IChannelDispatcher>(), CancellationToken.None);

        var stopTask = adapter.StopAsync(CancellationToken.None);
        var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().Be(stopTask);
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

        var adapter = CreateAdapter(new TelegramOptions
        {
            BotToken = "token",
            WebhookUrl = "https://example.test/hook"
        }, handler);

        await adapter.StartAsync(Mock.Of<IChannelDispatcher>(), CancellationToken.None);
        await adapter.StopAsync(CancellationToken.None);

        calls.Select(c => c.MethodName).Should().ContainSingle("setWebhook");
        calls.Select(c => c.MethodName).Should().NotContain("deleteWebhook");
        calls.Select(c => c.MethodName).Should().NotContain("getUpdates");
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

        var adapter = CreateAdapter(new TelegramOptions
        {
            BotToken = "token",
            PollingTimeoutSeconds = 1
        }, handler);

        await adapter.StartAsync(Mock.Of<IChannelDispatcher>(), CancellationToken.None);
        await pollingSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await adapter.StopAsync(CancellationToken.None);

        calls.Select(c => c.MethodName).Should().Contain("deleteWebhook");
        calls.Select(c => c.MethodName).Should().Contain("getUpdates");
        calls.Select(c => c.MethodName).Should().NotContain("setWebhook");
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

        var adapter = CreateAdapter(new TelegramOptions
        {
            BotToken = "token",
            AllowedChatIds = { 42 },
            StreamingBufferMs = 60000
        }, handler);

        await adapter.SendStreamEventAsync("42", new AgentStreamEvent { Type = AgentStreamEventType.MessageStart });
        await adapter.SendStreamEventAsync("42", new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = new string('a', 101) });
        await adapter.SendStreamEventAsync("42", new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = new string('b', 101) });

        calls.Count(c => c.MethodName == "sendMessage").Should().Be(1);
        calls.Count(c => c.MethodName == "editMessageText").Should().BeGreaterThan(0);
        calls.Where(c => c.MethodName == "editMessageText").Last().Text.Should().Contain(new string('a', 101));
        calls.Where(c => c.MethodName == "editMessageText").Last().Text.Should().Contain(new string('b', 101));
    }

    [Fact]
    public async Task StartAsync_WithoutBotToken_Throws()
    {
        var adapter = CreateAdapter(new TelegramOptions(), new StubHttpMessageHandler((_, _) => Task.FromResult(JsonOk(true))));

        Func<Task> act = () => adapter.StartAsync(Mock.Of<IChannelDispatcher>(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*BotToken*");
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

        var adapter = CreateAdapter(new TelegramOptions
        {
            BotToken = "token",
            PollingTimeoutSeconds = -5
        }, handler);

        await adapter.StartAsync(Mock.Of<IChannelDispatcher>(), CancellationToken.None);
        await pollSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await adapter.StopAsync(CancellationToken.None);

        pollingCall.Should().NotBeNull();
        pollingCall!.Timeout.Should().Be(1);
    }

    [Fact]
    public async Task SendAsync_WhenChatNotAllowed_Throws()
    {
        var adapter = CreateAdapter(new TelegramOptions
        {
            BotToken = "token",
            AllowedChatIds = { 42 }
        }, new StubHttpMessageHandler((_, _) => Task.FromResult(JsonOk(new TelegramMessage { MessageId = 1 }))));

        Func<Task> act = () => adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ConversationId = "99",
            Content = "blocked"
        });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not allowed*");
    }

    private static TelegramChannelAdapter CreateAdapter(TelegramOptions options, HttpMessageHandler handler)
    {
        var client = new HttpClient(handler);
        var apiClient = new TelegramBotApiClient(client, Options.Create(options), NullLogger<TelegramBotApiClient>.Instance);
        return new TelegramChannelAdapter(
            NullLogger<TelegramChannelAdapter>.Instance,
            Options.Create(options),
            apiClient);
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

    private sealed record ApiCall(string MethodName, string Body)
    {
        public string? Text => TryGetString("text");
        public long? Offset => TryGetLong("offset");
        public int? Timeout => TryGetInt("timeout");

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
    }
}

