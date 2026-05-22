using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Scenarios.Harness;

/// <summary>
/// Deterministic <see cref="IApiProvider"/> used by scenario tests so the LLM round-trip
/// becomes a scripted function of <c>(turnIndex, context)</c>. Removes flakiness from the
/// suite — citizens always receive the same reply for the same turn, every run.
/// </summary>
/// <remarks>
/// <para>
/// Construct with a <c>responseFactory</c> that returns the text to emit for a given turn,
/// or a constant <c>response</c>. The factory sees the zero-based turn index and the gateway-
/// assembled <see cref="Context"/> (system prompt, message history, tools) so tests can
/// assert on the LLM-facing projection if they choose.
/// </para>
/// <para>
/// Register via <see cref="Register(ApiProviderRegistry, ModelRegistry)"/> which both
/// registers the provider under <see cref="ApiName"/> and registers a matching scenario
/// model under <see cref="ModelId"/>. Scenarios point their agents at <see cref="ModelId"/>
/// + <see cref="ProviderName"/> and the gateway resolves through this fake.
/// </para>
/// </remarks>
public sealed class ScenarioFakeApiProvider : IApiProvider
{
    /// <summary>The API key under which the fake is registered with <see cref="ApiProviderRegistry"/>.</summary>
    public const string ApiName = "scenario-test-api";

    /// <summary>The provider key under which the fake's model is registered with <see cref="ModelRegistry"/>.</summary>
    public const string ProviderName = "scenario-test-provider";

    /// <summary>The model id scenarios assign to their agents to route LLM calls to this fake.</summary>
    public const string ModelId = "scenario-test-model";

    private readonly Func<int, Context, string> _responseFactory;
    private int _turnIndex;

    /// <summary>
    /// Creates a fake provider that emits <paramref name="response"/> for every turn.
    /// </summary>
    public ScenarioFakeApiProvider(string response = "ok")
        : this((_, _) => response)
    {
    }

    /// <summary>
    /// Creates a fake provider that emits the text returned by <paramref name="responseFactory"/>
    /// for each turn. The factory receives the zero-based turn index and the gateway-assembled
    /// <see cref="Context"/>.
    /// </summary>
    public ScenarioFakeApiProvider(Func<int, Context, string> responseFactory)
    {
        _responseFactory = responseFactory ?? throw new ArgumentNullException(nameof(responseFactory));
    }

    /// <inheritdoc />
    public string Api => ApiName;

    /// <summary>The total number of stream/streamSimple calls observed by this provider.</summary>
    public int TurnCount => Volatile.Read(ref _turnIndex);

    /// <inheritdoc />
    public LlmStream Stream(LlmModel model, Context context, StreamOptions? options = null)
        => BuildStream(NextTurn(), context, model);

    /// <inheritdoc />
    public LlmStream StreamSimple(LlmModel model, Context context, SimpleStreamOptions? options = null)
        => BuildStream(NextTurn(), context, model);

    /// <summary>
    /// Registers this provider with <paramref name="apiProviderRegistry"/> and registers a matching
    /// scenario model with <paramref name="modelRegistry"/> so the gateway can resolve LLM calls
    /// from agents that point at <see cref="ModelId"/> and <see cref="ProviderName"/>.
    /// </summary>
    public void Register(ApiProviderRegistry apiProviderRegistry, ModelRegistry modelRegistry)
    {
        ArgumentNullException.ThrowIfNull(apiProviderRegistry);
        ArgumentNullException.ThrowIfNull(modelRegistry);

        apiProviderRegistry.Register(this, sourceId: ApiName);
        modelRegistry.Register(ProviderName, CreateModel());
    }

    /// <summary>The scenario-test <see cref="LlmModel"/> registered alongside this provider.</summary>
    public static LlmModel CreateModel() => new(
        Id: ModelId,
        Name: "Scenario Test Model",
        Api: ApiName,
        Provider: ProviderName,
        BaseUrl: "https://scenario.test.invalid",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 128_000,
        MaxTokens: 32_000);

    private int NextTurn() => Interlocked.Increment(ref _turnIndex) - 1;

    private LlmStream BuildStream(int turn, Context context, LlmModel model)
    {
        var text = _responseFactory(turn, context) ?? string.Empty;
        var stream = new LlmStream();
        var message = new AssistantMessage(
            Content: [new TextContent(text)],
            Api: ApiName,
            Provider: model.Provider,
            ModelId: model.Id,
            Usage: new Usage
            {
                Input = 10,
                Output = Math.Max(1, text.Length / 4),
                TotalTokens = 10 + Math.Max(1, text.Length / 4)
            },
            StopReason: StopReason.Stop,
            ErrorMessage: null,
            ResponseId: $"scenario-{turn:D6}",
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        stream.Push(new StartEvent(message));
        stream.Push(new TextStartEvent(0, message));
        stream.Push(new TextDeltaEvent(0, text, message));
        stream.Push(new TextEndEvent(0, text, message));
        stream.Push(new DoneEvent(StopReason.Stop, message));
        stream.End(message);
        return stream;
    }
}
