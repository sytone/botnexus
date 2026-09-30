using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.A2A;
using BotNexus.Gateway.Abstractions.A2A;

namespace BotNexus.Extensions.A2A.Tests;

public sealed class DelegateA2ATaskToolTests
{
    [Fact]
    public void Definition_ExposesProfileAndObjectiveButNoEndpointOrCredential()
    {
        using var tool = new DelegateA2ATaskTool(
            new[] { new StubProfile("ready") },
            new StubClientFactory(new A2ATaskResult(A2ATerminalOutcome.Completed, null, null, "ok", [], null, null, null)));

        var schema = tool.Definition.Parameters.GetRawText();
        schema.ShouldContain("profileId");
        schema.ShouldContain("objective");
        schema.ShouldNotContain("endpoint", Case.Insensitive);
        schema.ShouldNotContain("credential", Case.Insensitive);
        schema.ShouldNotContain("token", Case.Insensitive);
        tool.DefaultTimeout.ShouldBe(TimeSpan.FromSeconds(60));
        tool.TimeoutArgument.ShouldNotBeNull().ArgumentName.ShouldBe("deadlineSeconds");
        tool.TimeoutArgument.Unit.ShouldBe(ToolTimeoutUnit.Seconds);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsOneCompactTypedTerminalJsonResult()
    {
        var expected = new A2ATaskResult(
            A2ATerminalOutcome.Completed,
            "context-1",
            "task-1",
            "finished",
            [],
            new A2AProvenance("fixture", new Uri("https://agents.example.test/.well-known/agent-card.json"), new Uri("https://agents.example.test/a2a"), "1.0"),
            new A2AUsage(3, 5),
            null);
        var factory = new StubClientFactory(expected);
        using var tool = new DelegateA2ATaskTool(new[] { new StubProfile("ready") }, factory);
        var prepared = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["profileId"] = "ready",
            ["objective"] = "Do the bounded task",
            ["deadlineSeconds"] = 30
        });

        var result = await tool.ExecuteAsync("call-1", prepared);

        factory.CallCount.ShouldBe(1);
        result.Content.ShouldHaveSingleItem().Type.ShouldBe(AgentToolContentType.Text);
        using var json = JsonDocument.Parse(result.Content[0].Value);
        json.RootElement.GetProperty("outcome").GetString().ShouldBe("completed");
        json.RootElement.GetProperty("summary").GetString().ShouldBe("finished");
        json.RootElement.GetProperty("usage").GetProperty("inputTokens").GetInt64().ShouldBe(3);
    }

    [Fact]
    public async Task Dispose_DisposesOwnedClientExactlyOnce()
    {
        var factory = new StubClientFactory(new A2ATaskResult(A2ATerminalOutcome.Completed, null, null, "ok", [], null, null, null));
        var tool = new DelegateA2ATaskTool(new[] { new StubProfile("ready") }, factory);

        tool.Dispose();
        tool.Dispose();

        factory.DisposeCount.ShouldBe(1);
        await Should.ThrowAsync<ObjectDisposedException>(() => tool.ExecuteAsync(
            "call",
            new Dictionary<string, object?> { ["profileId"] = "ready", ["objective"] = "x", ["deadlineSeconds"] = 30 }));
    }

    private sealed class StubProfile(string id) : IA2AServiceProfile
    {
        public string Id => id;
        public string DisplayName => id;
        public bool IsReady => true;
        public ValueTask<A2AServiceConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new A2AServiceConnection(new Uri("https://agents.example.test/")));
    }

    private sealed class StubClientFactory(A2ATaskResult result) : IA2AClientFactory
    {
        public int CallCount { get; private set; }
        public int DisposeCount { get; private set; }
        public IA2AClient Create() => new Client(this, result);

        private sealed class Client(StubClientFactory owner, A2ATaskResult result) : IA2AClient
        {
            public Task<A2ATaskResult> SendMessageAsync(A2AClientOptions options, A2AMessage message, DateTimeOffset deadline, CancellationToken cancellationToken = default)
            {
                owner.CallCount++;
                return Task.FromResult(result);
            }
            public void Dispose() => owner.DisposeCount++;
        }
    }
}
