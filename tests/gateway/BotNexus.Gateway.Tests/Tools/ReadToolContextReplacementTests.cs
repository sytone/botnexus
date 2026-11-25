using System.Text;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Tools;
using System.IO.Abstractions.TestingHelpers;
using Xunit.Abstractions;

namespace BotNexus.Gateway.Tests.Tools;

using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

public sealed class ReadToolContextReplacementTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RunAsync_AfterContextReplacement_RestoresElidedReadContent()
    {
        var workingDirectory = Path.GetFullPath("read-tool-context-replacement");
        var fileSystem = new MockFileSystem();
        fileSystem.Directory.CreateDirectory(workingDirectory);
        var fileContent = string.Join('\n', Enumerable.Range(1, 120)
            .Select(line => $"line-{line}-{new string('x', 100)}"));
        fileSystem.File.WriteAllText(Path.Combine(workingDirectory, "large.txt"), fileContent);
        var readTool = new ReadTool(workingDirectory, fileSystem);
        var provider = new ReadScriptProvider();
        var refreshedContext = new AgentContext("compacted", [], [readTool]);
        var compactionCalls = 0;
        var config = CreateConfig(provider, _ =>
        {
            compactionCalls++;
            return Task.FromResult<AgentContext?>(compactionCalls == 3 ? refreshedContext : null);
        });

        var results = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("read the file")],
            new AgentContext("initial", [], [readTool]),
            config,
            _ => Task.CompletedTask,
            CancellationToken.None);

        var readResults = results.OfType<ToolResultAgentMessage>().ToArray();
        readResults.Length.ShouldBe(3);
        var firstRead = readResults[0].Result;
        var sameContextRepeat = readResults[1].Result;
        var afterReplacement = readResults[2].Result;
        var firstText = firstRead.Content.ShouldHaveSingleItem().Value;
        var repeatText = sameContextRepeat.Content.ShouldHaveSingleItem().Value;
        var restoredText = afterReplacement.Content.ShouldHaveSingleItem().Value;

        repeatText.ShouldContain("Unchanged since your earlier read");
        repeatText.ShouldNotContain("line-1-");
        restoredText.ShouldBe(firstText);
        restoredText.ShouldContain("line-120-");
        firstRead.Details.ShouldBeOfType<ReadResultDetails>().ConcurrencyToken
            .ShouldBe(sameContextRepeat.Details.ShouldBeOfType<ReadResultDetails>().ConcurrencyToken);
        firstRead.Details.ShouldBeOfType<ReadResultDetails>().ConcurrencyToken
            .ShouldBe(afterReplacement.Details.ShouldBeOfType<ReadResultDetails>().ConcurrencyToken);

        var bytesAvoided = Encoding.UTF8.GetByteCount(firstText) - Encoding.UTF8.GetByteCount(repeatText);
        var bytesRestored = Encoding.UTF8.GetByteCount(restoredText);
        bytesAvoided.ShouldBeGreaterThan(0);
        bytesRestored.ShouldBe(Encoding.UTF8.GetByteCount(firstText));
        output.WriteLine($"ReadTool context replacement bytes avoided={bytesAvoided}; restored={bytesRestored}.");
    }

    [Fact]
    public async Task RunAsync_WhenCompactionFails_PreservesSameContextSuppression()
    {
        var workingDirectory = Path.GetFullPath("read-tool-failed-context-replacement");
        var fileSystem = new MockFileSystem();
        fileSystem.Directory.CreateDirectory(workingDirectory);
        fileSystem.File.WriteAllText(Path.Combine(workingDirectory, "large.txt"), "keep the content");
        var readTool = new ReadTool(workingDirectory, fileSystem);
        var compactionCalls = 0;
        var config = CreateConfig(new ReadScriptProvider(readsBeforeFinal: 2), _ =>
        {
            compactionCalls++;
            if (compactionCalls == 2)
            {
                throw new InvalidOperationException("compaction unavailable");
            }

            return Task.FromResult<AgentContext?>(null);
        });

        var results = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("read the file")],
            new AgentContext("initial", [], [readTool]),
            config,
            _ => Task.CompletedTask,
            CancellationToken.None);

        var readResults = results.OfType<ToolResultAgentMessage>().ToArray();
        readResults.Length.ShouldBe(2);
        readResults[0].Result.Content.ShouldHaveSingleItem().Value.ShouldBe("keep the content");
        readResults[1].Result.Content.ShouldHaveSingleItem().Value
            .ShouldContain("Unchanged since your earlier read");
    }

    [Fact]
    public async Task ExecuteAsync_OnLinux_DistinctCaseSensitivePathsDoNotShareSuppressionState()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var workingDirectory = Path.Combine(AppContext.BaseDirectory, $"read-tool-case-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var content = "same content\nwith two lines";
            await File.WriteAllTextAsync(Path.Combine(workingDirectory, "Case.txt"), content);
            await File.WriteAllTextAsync(Path.Combine(workingDirectory, "case.txt"), content);
            var readTool = new ReadTool(workingDirectory);

            var first = await readTool.ExecuteAsync("read-upper", new Dictionary<string, object?>
            {
                ["path"] = "Case.txt"
            });
            var second = await readTool.ExecuteAsync("read-lower", new Dictionary<string, object?>
            {
                ["path"] = "case.txt"
            });

            first.Content[0].Value.ShouldBe($"same content{Environment.NewLine}with two lines");
            second.Content[0].Value.ShouldBe(first.Content[0].Value);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_OnWindows_CaseAliasesShareSuppressionState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var workingDirectory = Path.Combine(AppContext.BaseDirectory, $"read-tool-case-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workingDirectory, "Case.txt"), "same content");
            var readTool = new ReadTool(workingDirectory);

            _ = await readTool.ExecuteAsync("read-first", new Dictionary<string, object?>
            {
                ["path"] = "Case.txt"
            });
            var second = await readTool.ExecuteAsync("read-case-alias", new Dictionary<string, object?>
            {
                ["path"] = "case.txt"
            });

            second.Content[0].Value.ShouldContain("Unchanged since your earlier read");
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private sealed class ReadScriptProvider : IApiProvider
    {
        private int _turn;

        public ReadScriptProvider(int readsBeforeFinal = 3)
        {
            ReadsBeforeFinal = readsBeforeFinal;
        }

        private int ReadsBeforeFinal { get; }

        public string Api => "read-context-test";

        public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
            => StreamSimple(model, context, null);

        public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
        {
            var turn = Interlocked.Increment(ref _turn);
            if (turn <= ReadsBeforeFinal)
            {
                return CreateToolCallResponse(model, $"read-{turn}");
            }

            return CreateTextResponse(model, "done");
        }

        private static LlmStream CreateToolCallResponse(LlmModel model, string callId)
        {
            var call = new ToolCallContent(callId, "read", new Dictionary<string, object?>
            {
                ["path"] = "large.txt"
            });
            var message = CreateAssistantMessage(model, StopReason.ToolUse, [call]);
            var stream = new LlmStream();
            stream.Push(new StartEvent(message));
            stream.Push(new ToolCallStartEvent(0, message));
            stream.Push(new ToolCallDeltaEvent(0, "{}", message));
            stream.Push(new ToolCallEndEvent(0, call, message));
            stream.Push(new DoneEvent(StopReason.ToolUse, message));
            stream.End(message);
            return stream;
        }

        private static LlmStream CreateTextResponse(LlmModel model, string text)
        {
            var message = CreateAssistantMessage(model, StopReason.Stop, [new TextContent(text)]);
            var stream = new LlmStream();
            stream.Push(new StartEvent(message));
            stream.Push(new TextStartEvent(0, message));
            stream.Push(new TextDeltaEvent(0, text, message));
            stream.Push(new TextEndEvent(0, text, message));
            stream.Push(new DoneEvent(StopReason.Stop, message));
            stream.End(message);
            return stream;
        }

        private static AssistantMessage CreateAssistantMessage(
            LlmModel model,
            StopReason stopReason,
            IReadOnlyList<ContentBlock> content)
            => new(
                Content: content,
                Api: model.Api,
                Provider: model.Provider,
                ModelId: model.Id,
                Usage: Usage.Empty(),
                StopReason: stopReason,
                ErrorMessage: null,
                ResponseId: null,
                Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static AgentLoopConfig CreateConfig(
        ReadScriptProvider provider,
        Func<CancellationToken, Task<AgentContext?>> contextCompactionService)
    {
        var providers = new ApiProviderRegistry();
        providers.Register(provider);
        var modelRegistry = new ModelRegistry();
        var model = new LlmModel(
            Id: "read-context-test",
            Name: "Read context test",
            Api: provider.Api,
            Provider: "read-context-test",
            BaseUrl: "http://localhost",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 8192,
            MaxTokens: 1024);
        modelRegistry.Register(model.Provider, model);
        return new AgentLoopConfig(
            Model: model,
            LlmClient: new LlmClient(providers, modelRegistry),
            ProviderMessageTransformer: (_, _) => Task.FromResult<IReadOnlyList<Message>>([]),
            AgentContextTransformer: (messages, _) => Task.FromResult(messages),
            ProviderExecutionOptionsProvider: (_, _) => Task.FromResult<ProviderExecutionOptions?>(null),
            SteeringMessageProvider: null,
            FollowUpMessageProvider: null,
            ToolExecutionMode: ToolExecutionMode.Sequential,
            ToolExecutionPolicy: null,
            ToolResultTransformer: null,
            GenerationSettings: new GenerationOptions(),
            ContextCompactionService: contextCompactionService);
    }
}
