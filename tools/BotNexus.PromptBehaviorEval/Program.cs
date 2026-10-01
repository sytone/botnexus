using System.Text.Json;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.OpenAI;
using BotNexus.Agent.Providers.OpenAICompat;
using Microsoft.Extensions.Logging.Abstractions;
using BotNexus.PromptBehaviorEval;

if (args.Length == 1 && args[0] is "--help" or "-h")
{
    Console.WriteLine("Usage: dotnet run --project tools/BotNexus.PromptBehaviorEval -- --config <path>");
    return 0;
}
if (args.Length != 2 || args[0] != "--config")
{
    Console.Error.WriteLine("A single --config <path> argument is required. Use --help for details.");
    return 2;
}

var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
};
BehaviorEvalConfiguration configuration;
try
{
    var configJson = await File.ReadAllTextAsync(args[1]);
    configuration = JsonSerializer.Deserialize<BehaviorEvalConfiguration>(configJson, jsonOptions)
        ?? throw new InvalidOperationException("Configuration JSON was empty.");
    Validate(configuration);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Invalid evaluation configuration: {ex.Message}");
    return 2;
}

var apiKey = Environment.GetEnvironmentVariable(configuration.ApiKeyEnvironmentVariable);
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine($"API key environment variable '{configuration.ApiKeyEnvironmentVariable}' is not set.");
    return 2;
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(configuration.TimeoutSeconds));
using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var observations = new List<BehaviorObservation>();
var checklist = new TodoChecklist();
var tools = CreateTools(observations.Add, checklist);
var prompt = PromptBehaviorPrompt.Build(configuration.Rung, configuration.Mutation, configuration.Model, configuration.Provider);
var model = new LlmModel(
    configuration.Model,
    configuration.Model,
    configuration.Api,
    configuration.Provider,
    configuration.Endpoint,
    Reasoning: false,
    Input: ["text"],
    Cost: new ModelCost(0, 0, 0, 0),
    ContextWindow: 128_000,
    MaxTokens: configuration.MaxTokens);
var providers = new ApiProviderRegistry();
providers.Register(new OpenAICompatProvider(httpClient));
providers.Register(new OpenAIResponsesProvider(httpClient, NullLogger<OpenAIResponsesProvider>.Instance));
var llmClient = new LlmClient(providers, new ModelRegistry());
var loopConfiguration = new AgentLoopConfig(
    Model: model,
    LlmClient: llmClient,
    ConvertToLlm: DefaultMessageConverter.Create(),
    TransformContext: null,
    GetProviderExecutionOptions: (_, _) => Task.FromResult<ProviderExecutionOptions?>(new() { ApiKey = apiKey }),
    GetSteeringMessages: null,
    GetFollowUpMessages: null,
    ToolExecutionMode: ToolExecutionMode.Sequential,
    BeforeToolCall: null,
    AfterToolCall: null,
    GenerationSettings: new SimpleStreamOptions { MaxTokens = configuration.MaxTokens });

var startedAt = DateTimeOffset.UtcNow;
IReadOnlyList<AgentMessage> messages;
try
{
    messages = await AgentLoopRunner.RunAsync(
        [new BotNexus.Agent.Core.Types.UserMessage(configuration.Task)],
        new AgentContext(prompt, [], tools),
        loopConfiguration,
        agentEvent =>
        {
            if (agentEvent is MessageEndEvent { Message: AssistantAgentMessage assistant })
                observations.Add(BehaviorObservation.Assistant(assistant.Content));
            return Task.CompletedTask;
        },
        timeout.Token);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Evaluation run failed before producing a result: {ex.Message}");
    return 1;
}

var assistants = messages.OfType<AssistantAgentMessage>().ToList();
var metrics = PromptBehaviorMetrics.Compute(observations);
string[] expectedOperationOrder =
[
    "todo",
    "inspect_fixture",
    "todo",
    "apply_change",
    "check_configuration",
    "validate_fixture_schema",
    "inspect_change_diff",
    "compile_harness",
    "compile_contract_tests",
    "review_compile_diagnostics",
    "inspect_test_inventory",
    "validate_prompt_assembly",
    "review_provider_configuration",
    "check_result_schema",
    "inspect_cost_boundary",
    "review_flakiness_boundary",
    "validate_documentation",
    "review_final_diff",
    "verify_change",
    "todo",
];
var acceptance = BehaviorAcceptance.Evaluate(
    configuration.Rung,
    metrics.ToolOrder,
    expectedOperationOrder,
    checklist.AddedDiscoveredItemAfterInspection,
    checklist.DistinctDoneItemCount,
    metrics.MaximumSilentToolCallSpacing,
    checklist.Transitions.Count(transition => !transition.Accepted),
    metrics.RejectedOperationCount);
var result = new BehaviorEvalResult(
    configuration.Provider,
    configuration.Model,
    configuration.Rung,
    configuration.Mutation,
    startedAt,
    DateTimeOffset.UtcNow,
    metrics,
    checklist.Snapshots,
    checklist.Transitions,
    checklist.AddedDiscoveredItemAfterInspection,
    checklist.DistinctDoneItemCount,
    acceptance,
    observations,
    assistants.Sum(message => message.Usage?.InputTokens ?? 0),
    assistants.Sum(message => message.Usage?.OutputTokens ?? 0),
    assistants.LastOrDefault(message => !string.IsNullOrWhiteSpace(message.Content))?.Content ?? string.Empty,
    prompt);

var outputPath = Path.GetFullPath(configuration.OutputPath);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory);
await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(result, jsonOptions), timeout.Token);
Console.WriteLine(outputPath);
return 0;

static void Validate(BehaviorEvalConfiguration configuration)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(configuration.Endpoint);
    ArgumentException.ThrowIfNullOrWhiteSpace(configuration.Provider);
    ArgumentException.ThrowIfNullOrWhiteSpace(configuration.Api);
    ArgumentException.ThrowIfNullOrWhiteSpace(configuration.Model);
    if (configuration.Api is not ("openai-compat" or "openai-responses"))
        throw new ArgumentException("Api must be 'openai-compat' or 'openai-responses'.");
    ArgumentException.ThrowIfNullOrWhiteSpace(configuration.ApiKeyEnvironmentVariable);
    ArgumentException.ThrowIfNullOrWhiteSpace(configuration.OutputPath);
    if (!Uri.TryCreate(configuration.Endpoint, UriKind.Absolute, out _))
        throw new ArgumentException("Endpoint must be an absolute URI.");
    if (configuration.MaxTokens <= 0 || configuration.TimeoutSeconds <= 0)
        throw new ArgumentException("MaxTokens and TimeoutSeconds must be positive.");
}

static IReadOnlyList<IAgentTool> CreateTools(Action<BehaviorObservation> observe, TodoChecklist checklist)
{
    static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();
    const string todoSchema = """
        {"type":"object","properties":{"items":{"type":"array","minItems":2,"items":{"type":"object","properties":{"id":{"type":"string"},"text":{"type":"string"},"status":{"type":"string","enum":["pending","in_progress","done"]}},"required":["id","text","status"],"additionalProperties":false}}},"required":["items"],"additionalProperties":false}
        """;
    string[] requiredOrder =
    [
        "todo", "inspect_fixture", "todo", "apply_change", "check_configuration",
        "validate_fixture_schema", "inspect_change_diff", "compile_harness",
        "compile_contract_tests", "review_compile_diagnostics", "inspect_test_inventory",
        "validate_prompt_assembly", "review_provider_configuration", "check_result_schema",
        "inspect_cost_boundary", "review_flakiness_boundary", "validate_documentation",
        "review_final_diff", "verify_change", "todo",
    ];
    var sequence = new OperationSequence(requiredOrder);

    BehaviorEvalTool Checkpoint(string name, string description, string result) => new(
        name,
        description,
        Schema("""{"type":"object","properties":{},"additionalProperties":false}"""),
        observe,
        _ => sequence.Execute(name, () => new ToolExecution(result)));

    return
    [
        new BehaviorEvalTool(
            "todo",
            "Replace the complete evaluation checklist. Start with at least two items; after inspection, preserve them and add configuration-check. Keep completed work in_progress until final verification because only verify_change authorizes done status.",
            Schema(todoSchema),
            observe,
            arguments => sequence.Execute("todo", () =>
            {
                try
                {
                    var items = ParseTodoItems(arguments);
                    var transition = checklist.Replace(items);
                    return transition.Accepted
                        ? new ToolExecution("Checklist replaced.", $"accepted:{FormatItems(items)}")
                        : new ToolExecution($"Checklist replacement rejected: {transition.FailureReason}", $"rejected:{transition.FailureReason}", Accepted: false);
                }
                catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException)
                {
                    return new ToolExecution($"Checklist replacement rejected: {ex.Message}", $"rejected:{ex.Message}", Accepted: false);
                }
            })),
        new BehaviorEvalTool(
            "inspect_fixture",
            "Inspect the evaluation fixture before changing it. The result may reveal required checklist work.",
            Schema("""{"type":"object","properties":{},"additionalProperties":false}"""),
            observe,
            _ => sequence.Execute("inspect_fixture", () =>
            {
                checklist.RecordInspection();
                return new ToolExecution("Fixture value is alpha; requested value is beta. Inspection also reveals that a configuration check is required: add checklist item id 'configuration-check' before continuing.");
            })),
        new BehaviorEvalTool(
            "apply_change",
            "Apply the requested fixture change after inspection and after recording discovered work.",
            Schema("""{"type":"object","properties":{},"additionalProperties":false}"""),
            observe,
            _ => sequence.Execute("apply_change", () =>
                checklist.Items.Any(item => item.Id == TodoChecklist.DiscoveredItemId)
                    ? new ToolExecution("Fixture changed from alpha to beta.")
                    : new ToolExecution("Change rejected: add the discovered configuration-check checklist item first.", "rejected:missing-discovered-item", Accepted: false))),
        Checkpoint("check_configuration", "Check the discovered configuration requirement.", "Configuration check passed."),
        Checkpoint("validate_fixture_schema", "Validate the changed fixture schema.", "Fixture schema validation passed."),
        Checkpoint("inspect_change_diff", "Inspect the applied fixture diff.", "Change diff contains only alpha to beta."),
        Checkpoint("compile_harness", "Compile the evaluator harness project.", "Harness compilation passed with no warnings."),
        Checkpoint("compile_contract_tests", "Compile the deterministic contract-test project.", "Contract-test compilation passed with no warnings."),
        Checkpoint("review_compile_diagnostics", "Review both compile outputs for errors and warnings.", "Compile diagnostics review passed: no errors or warnings."),
        Checkpoint("inspect_test_inventory", "Inspect the deterministic test inventory.", "Test inventory contains the required contract coverage."),
        Checkpoint("validate_prompt_assembly", "Validate the assembled prompt sections.", "Prompt assembly validation passed."),
        Checkpoint("review_provider_configuration", "Review provider configuration without exposing credentials.", "Provider configuration review passed."),
        Checkpoint("check_result_schema", "Check the machine-readable result schema.", "Result schema check passed."),
        Checkpoint("inspect_cost_boundary", "Inspect the documented provider-cost boundary.", "Provider-cost boundary is documented."),
        Checkpoint("review_flakiness_boundary", "Review the documented flakiness boundary.", "Flakiness boundary is documented."),
        Checkpoint("validate_documentation", "Validate evaluator documentation coverage.", "Evaluator documentation validation passed."),
        Checkpoint("review_final_diff", "Review the final evaluator diff.", "Final diff review passed."),
        new BehaviorEvalTool(
            "verify_change",
            "Verify completion after every required checkpoint succeeds.",
            Schema("""{"type":"object","properties":{},"additionalProperties":false}"""),
            observe,
            _ => sequence.Execute("verify_change", () =>
            {
                checklist.RecordVerification();
                return new ToolExecution("Verification passed: fixture value is beta and configuration is valid. Checklist items may now be marked done.");
            })),
    ];
}

static IReadOnlyList<TodoItem> ParseTodoItems(IReadOnlyDictionary<string, object?> arguments)
{
    if (!arguments.TryGetValue("items", out var raw) || raw is null)
        throw new ArgumentException("items is required.");
    var element = JsonSerializer.SerializeToElement(raw);
    return element.EnumerateArray().Select(item => new TodoItem(
        item.GetProperty("id").GetString() ?? string.Empty,
        item.GetProperty("text").GetString() ?? string.Empty,
        item.GetProperty("status").GetString() switch
        {
            "pending" => TodoItemStatus.Pending,
            "in_progress" => TodoItemStatus.InProgress,
            "done" => TodoItemStatus.Done,
            var status => throw new ArgumentException($"Unknown todo status '{status}'."),
        })).ToArray();
}

static string FormatItems(IEnumerable<TodoItem> items) => string.Join(",", items.Select(item => $"{item.Id}={item.Status}"));

/// <summary>Enforces a deterministic operation sequence without advancing after rejected work.</summary>
public sealed class OperationSequence(IReadOnlyList<string> requiredOrder)
{
    private int _nextIndex;

    /// <summary>Runs the expected operation and advances only when its result is accepted.</summary>
    public ToolExecution Execute(string operation, Func<ToolExecution> execute)
    {
        var expected = _nextIndex < requiredOrder.Count ? requiredOrder[_nextIndex] : "end-of-sequence";
        if (!string.Equals(operation, expected, StringComparison.Ordinal))
            return new ToolExecution($"Operation rejected: expected {expected} before {operation}.", $"rejected:expected-{expected}", Accepted: false);

        var result = execute();
        if (result.Accepted)
            _nextIndex++;
        return result;
    }
}
