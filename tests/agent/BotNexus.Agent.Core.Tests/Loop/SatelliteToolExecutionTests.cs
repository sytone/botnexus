using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Hooks;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests.Loop;

public sealed class SatelliteToolExecutionTests
{
    [Fact]
    public async Task ExecuteAsync_RemoteCapableToolsShareOneSatelliteExecutionScope()
    {
        var localTools = new[]
        {
            new TrackingTool("write"),
            new TrackingTool("read"),
            new TrackingTool("exec"),
            new TrackingTool("process")
        };
        var remote = new CoherentSatelliteExecutor();
        var scope = CreateScope();
        var config = CreateConfig(scope, remote, localTools.Select(tool => tool.Name));
        var context = new AgentContext(null, [], localTools);

        await ExecuteAsync(context, config, "write", new Dictionary<string, object?>
        {
            ["path"] = "generated.txt",
            ["content"] = "from satellite"
        });
        var readResult = await ExecuteAsync(context, config, "read", new Dictionary<string, object?>
        {
            ["path"] = "generated.txt"
        });
        await ExecuteAsync(context, config, "exec", new Dictionary<string, object?>
        {
            ["command"] = "start-worker"
        });
        var processResult = await ExecuteAsync(context, config, "process", new Dictionary<string, object?>
        {
            ["action"] = "status"
        });

        readResult.IsError.ShouldBeFalse();
        readResult.Result.Content.Single().Value.ShouldBe("from satellite");
        processResult.Result.Content.Single().Value.ShouldBe("running");
        remote.Requests.Count.ShouldBe(4);
        remote.Requests.ShouldAllBe(request => request.Scope == scope);
        remote.Requests.Select(request => request.ToolCallId).ShouldBe(["call-write", "call-read", "call-exec", "call-process"]);
        remote.Requests.ShouldAllBe(request => request.Environment.Count == 1 && request.Environment["GRANTED_VALUE"] == "allowed");
        localTools.ShouldAllBe(tool => tool.ExecuteCount == 0);
    }

    [Fact]
    public async Task ExecuteAsync_LocalOnlyToolRemainsOnPrimaryGateway()
    {
        var localTool = new TrackingTool("conversation");
        var remote = new CoherentSatelliteExecutor();
        var scope = CreateScope();
        var config = TestHelpers.CreateTestConfig(satelliteToolExecution: new SatelliteToolExecutionOptions(
            Scope: scope,
            Executor: remote,
            ClassifyTool: _ => SatelliteToolClass.LocalOnly,
            Environment: new Dictionary<string, string>()));
        var context = new AgentContext(null, [], [localTool]);

        var result = await ExecuteAsync(context, config, "conversation", new Dictionary<string, object?>());

        result.IsError.ShouldBeFalse();
        localTool.ExecuteCount.ShouldBe(1);
        remote.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(SatelliteToolClass.Unsupported)]
    [InlineData(SatelliteToolClass.RemoteCapable)]
    public async Task ExecuteAsync_RequiredRemoteToolCannotFallBackToLocalExecution(SatelliteToolClass toolClass)
    {
        var localTool = new TrackingTool("exec");
        var remote = new CoherentSatelliteExecutor { Available = false };
        var scope = CreateScope();
        var config = TestHelpers.CreateTestConfig(satelliteToolExecution: new SatelliteToolExecutionOptions(
            Scope: scope,
            Executor: remote,
            ClassifyTool: _ => toolClass,
            Environment: new Dictionary<string, string>()));
        var context = new AgentContext(null, [], [localTool]);

        var result = await ExecuteAsync(context, config, "exec", new Dictionary<string, object?>());

        result.IsError.ShouldBeTrue();
        result.Result.Content.Single().Value.ShouldContain(
            toolClass == SatelliteToolClass.Unsupported ? "unsupported" : "unavailable",
            Case.Insensitive);
        localTool.ExecuteCount.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_RemoteRequestCarriesCallerAndWorkingDirectory()
    {
        var localTool = new TrackingTool("read");
        var remote = new CoherentSatelliteExecutor();
        var scope = CreateScope();
        var config = CreateConfig(scope, remote, [localTool.Name]);
        var context = new AgentContext(null, [], [localTool]);

        await ExecuteAsync(context, config, "read", new Dictionary<string, object?>
        {
            ["path"] = "generated.txt"
        });

        var request = remote.Requests.ShouldHaveSingleItem();
        request.ProtocolVersion.ShouldBe(2);
        request.Scope.CallerIdentity.ShouldBe("agent:test-agent");
        request.Scope.WorkingDirectory.ShouldBe("workspace/project");
    }

    [Fact]
    public async Task ExecuteAsync_RemoteResultPreservesOutcomeAndBoundedMetadata()
    {
        var localTool = new TrackingTool("read");
        var artifact = new SatelliteArtifactReference(
            ArtifactId: "artifact-1",
            MediaType: "text/plain",
            Length: 4096,
            Sha256: "abc123");
        var remoteDetails = new Dictionary<string, object?> { ["encoding"] = "utf-8" };
        var remote = new FixedSatelliteExecutor(SatelliteToolResult.Completed(
            new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "bounded")], remoteDetails),
            new SatelliteToolResultMetadata(
                IsTruncated: true,
                IsIncomplete: false,
                Artifacts: [artifact])));
        var config = CreateConfig(CreateScope(), remote, [localTool.Name]);
        var context = new AgentContext(null, [], [localTool]);

        var result = await ExecuteAsync(context, config, "read", new Dictionary<string, object?>
        {
            ["path"] = "large.txt"
        });

        result.IsError.ShouldBeFalse();
        result.Result.Details.ShouldBeSameAs(remoteDetails);
        var delivery = result.Result.DeliveryDetails.ShouldBeOfType<SatelliteToolResultDetails>();
        delivery.Outcome.ShouldBe(SatelliteToolOutcome.Completed);
        delivery.IsTruncated.ShouldBeTrue();
        delivery.IsIncomplete.ShouldBeFalse();
        delivery.Artifacts.ShouldBe([artifact]);
        delivery.PriorDeliveryDetails.ShouldBeNull();
        localTool.ExecuteCount.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_AfterHookPreservesSatelliteDeliveryMetadataAndCanReplaceToolDetails()
    {
        var originalDetails = new object();
        var replacementDetails = new object();
        var remote = new FixedSatelliteExecutor(SatelliteToolResult.Completed(
            new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "bounded")], originalDetails),
            new SatelliteToolResultMetadata(true, false, [])));
        var baseConfig = CreateConfig(CreateScope(), remote, ["read"]);
        var config = baseConfig with
        {
            AfterToolCall = (_, _) => Task.FromResult<AfterToolCallResult?>(new AfterToolCallResult(Details: replacementDetails))
        };
        var context = new AgentContext(null, [], [new TrackingTool("read")]);

        var result = await ExecuteAsync(context, config, "read", new Dictionary<string, object?> { ["path"] = "large.txt" });

        result.Result.Details.ShouldBeSameAs(replacementDetails);
        var delivery = result.Result.DeliveryDetails.ShouldBeOfType<SatelliteToolResultDetails>();
        delivery.IsTruncated.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_MissingRemoteMetadataRemainsUnknown()
    {
        var remote = new FixedSatelliteExecutor(SatelliteToolResult.Completed(Text("legacy")));
        var config = CreateConfig(CreateScope(), remote, ["read"]);
        var context = new AgentContext(null, [], [new TrackingTool("read")]);

        var result = await ExecuteAsync(context, config, "read", new Dictionary<string, object?> { ["path"] = "legacy.txt" });

        var delivery = result.Result.DeliveryDetails.ShouldBeOfType<SatelliteToolResultDetails>();
        delivery.IsTruncated.ShouldBeNull();
        delivery.IsIncomplete.ShouldBeNull();
        delivery.Artifacts.ShouldBeEmpty();
    }

    [Fact]
    public void ExistingPublicConstructorsRemainAvailable()
    {
        var scope = new SatelliteExecutionScope("satellite-1", "workspace-1", "run-1", "attempt-1", 7);
        var result = new SatelliteToolResult(SatelliteToolOutcome.Completed, Text("ok"), false);

        scope.CallerIdentity.ShouldBeNull();
        scope.WorkingDirectory.ShouldBeNull();
        result.Metadata.ShouldBeNull();
    }

    [Fact]
    public void ErrorFactory_RejectsCompletedOutcomeAndDoesNotInventIncompleteMetadata()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => SatelliteToolResult.Error(SatelliteToolOutcome.Completed, "invalid"));

        var failed = SatelliteToolResult.Error(SatelliteToolOutcome.Failed, "failed");
        failed.Metadata.ShouldBeNull();
        failed.IsError.ShouldBeTrue();
    }

    private static SatelliteExecutionScope CreateScope() => new(
        SatelliteId: "satellite-1",
        WorkspaceId: "workspace-1",
        RunId: "run-1",
        AttemptId: "attempt-1",
        FencingGeneration: 7)
    {
        CallerIdentity = "agent:test-agent",
        WorkingDirectory = "workspace/project"
    };

    private static AgentLoopConfig CreateConfig(
        SatelliteExecutionScope scope,
        ISatelliteToolExecutor executor,
        IEnumerable<string> remoteToolNames)
    {
        var names = remoteToolNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return TestHelpers.CreateTestConfig(satelliteToolExecution: new SatelliteToolExecutionOptions(
            Scope: scope,
            Executor: executor,
            ClassifyTool: name => names.Contains(name) ? SatelliteToolClass.RemoteCapable : SatelliteToolClass.LocalOnly,
            Environment: new Dictionary<string, string> { ["GRANTED_VALUE"] = "allowed" }));
    }

    private static async Task<ToolResultAgentMessage> ExecuteAsync(
        AgentContext context,
        AgentLoopConfig config,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments)
    {
        var assistant = new AssistantAgentMessage(
            Content: string.Empty,
            ToolCalls: [new ToolCallContent($"call-{toolName}", toolName, new Dictionary<string, object?>(arguments))],
            FinishReason: StopReason.ToolUse);

        var results = await ToolExecutor.ExecuteAsync(
            context,
            assistant,
            config,
            _ => Task.CompletedTask,
            CancellationToken.None);

        return results.ShouldHaveSingleItem();
    }

    private sealed class TrackingTool(string name) : IAgentTool
    {
        private static readonly System.Text.Json.JsonElement EmptySchema =
            System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone();

        public string Name { get; } = name;
        public string Label => Name;
        public Tool Definition => new(Name, Name, EmptySchema);
        public int ExecuteCount { get; private set; }

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default) => Task.FromResult(arguments);

        public Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default,
            AgentToolUpdateCallback? onUpdate = null)
        {
            ExecuteCount++;
            return Task.FromResult(Text("local"));
        }
    }

    private sealed class FixedSatelliteExecutor(SatelliteToolResult result) : ISatelliteToolExecutor
    {
        public Task<SatelliteToolResult> ExecuteAsync(
            SatelliteToolRequest request,
            CancellationToken cancellationToken = default,
            AgentToolUpdateCallback? onUpdate = null) => Task.FromResult(result);
    }

    private sealed class CoherentSatelliteExecutor : ISatelliteToolExecutor
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
        private bool _workerRunning;

        public bool Available { get; init; } = true;
        public List<SatelliteToolRequest> Requests { get; } = [];

        public Task<SatelliteToolResult> ExecuteAsync(
            SatelliteToolRequest request,
            CancellationToken cancellationToken = default,
            AgentToolUpdateCallback? onUpdate = null)
        {
            Requests.Add(request);
            if (!Available)
            {
                return Task.FromResult(SatelliteToolResult.Unavailable("satellite unavailable"));
            }

            AgentToolResult result = request.ToolName switch
            {
                "write" => Write(request.Arguments),
                "read" => Read(request.Arguments),
                "exec" => StartWorker(),
                "process" => Text(_workerRunning ? "running" : "stopped"),
                _ => Text("unexpected")
            };
            return Task.FromResult(SatelliteToolResult.Completed(result));
        }

        private AgentToolResult Write(IReadOnlyDictionary<string, object?> arguments)
        {
            _files[arguments["path"]!.ToString()!] = arguments["content"]!.ToString()!;
            return Text("written");
        }

        private AgentToolResult Read(IReadOnlyDictionary<string, object?> arguments) =>
            Text(_files[arguments["path"]!.ToString()!]);

        private AgentToolResult StartWorker()
        {
            _workerRunning = true;
            return Text("started");
        }
    }

    private static AgentToolResult Text(string value) =>
        new([new AgentToolContent(AgentToolContentType.Text, value)]);
}
