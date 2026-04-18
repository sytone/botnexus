# Thinking Levels

Extended thinking (also called "reasoning" or "deep reasoning") allows some LLM models to spend more computation to solve harder problems. BotNexus supports thinking levels end-to-end: from CLI argument through provider-specific implementations. This document explains the full pipeline.

## Overview

Thinking levels control how much internal reasoning an LLM model performs before generating output. They're useful for complex coding tasks where step-by-step reasoning improves solution quality.

```
CLI: --thinking high
    ↓
CodingAgent parses config
    ↓
Agent.State.ThinkingLevel = ThinkingLevel.High
    ↓
AgentLoopRunner.RunTurnAsync()
    ↓
ContextConverter builds Context with thinking level
    ↓
SimpleOptionsHelper calculates thinking budget
    ↓
Provider-specific implementation
    ↓
LLM returns thinking content blocks
    ↓
StreamAccumulator extracts thinking blocks
    ↓
Agent.State.StreamingMessage includes thinking
```

## ThinkingLevel enum

Five levels control reasoning intensity:

```csharp
public enum ThinkingLevel
{
    [JsonStringEnumMemberName("minimal")] Minimal,      // ~1 KB thinking budget
    [JsonStringEnumMemberName("low")] Low,              // ~2 KB thinking budget
    [JsonStringEnumMemberName("medium")] Medium,        // ~8 KB thinking budget
    [JsonStringEnumMemberName("high")] High,            // ~16 KB thinking budget
    [JsonStringEnumMemberName("xhigh")] ExtraHigh       // ~16 KB thinking budget (capped)
}
```

**JSON representation:** Levels serialize to lowercase strings (`"minimal"`, `"low"`, etc.) for API compatibility.

## Thinking budgets

Each level has a default thinking token budget (the maximum tokens reserved for internal reasoning):

```csharp
private static readonly IReadOnlyDictionary<ThinkingLevel, int> DefaultThinkingBudgets =
    new Dictionary<ThinkingLevel, int>
    {
        [ThinkingLevel.Minimal] = 1024,      // 1 KB
        [ThinkingLevel.Low] = 2048,          // 2 KB
        [ThinkingLevel.Medium] = 8192,       // 8 KB
        [ThinkingLevel.High] = 16384,        // 16 KB
        [ThinkingLevel.ExtraHigh] = 16384    // 16 KB (same as High)
    };
```

These are used by `SimpleOptionsHelper.GetDefaultThinkingBudget()` when no custom budgets are specified.

## Custom thinking budgets

Models can override default budgets via `ThinkingBudgets`:

```csharp
public record ThinkingBudgets
{
    public int? Minimal { get; init; }
    public int? Low { get; init; }
    public int? Medium { get; init; }
    public int? High { get; init; }
    public int? ExtraHigh { get; init; }
}
```

Each level specifies the thinking budget token count. Example:

```csharp
var customBudgets = new ThinkingBudgets
{
    Low = 2000,
    Medium = 8000
};
```

## SimpleStreamOptions and Reasoning

The `SimpleStreamOptions` record extends `StreamOptions` to include thinking support:

```csharp
public record class SimpleStreamOptions : StreamOptions
{
    public ThinkingLevel? Reasoning { get; init; }
    public ThinkingBudgets? ThinkingBudgets { get; init; }
}
```

This is sent to the provider layer when calling an LLM:

```csharp
var options = new SimpleStreamOptions
{
    Reasoning = ThinkingLevel.High,
    ThinkingBudgets = null,  // Use defaults
    MaxTokens = 8000,
    Temperature = 1.0f
};

var stream = llmClient.Stream(model, context, options);
```

## SimpleOptionsHelper: Budget calculation

`SimpleOptionsHelper` is responsible for converting thinking levels to provider-specific parameters:

```csharp
public static class SimpleOptionsHelper
{
    // Get budget for a specific level (custom or default)
    public static int? GetBudgetForLevel(
        ThinkingLevel level,
        ThinkingBudgets? customBudgets)
    {
        if (customBudgets is null)
            return null;  // Fall back to defaults

        return level switch
        {
            ThinkingLevel.Minimal => customBudgets.Minimal,
            ThinkingLevel.Low => customBudgets.Low,
            ThinkingLevel.Medium => customBudgets.Medium,
            ThinkingLevel.High => customBudgets.High,
            ThinkingLevel.ExtraHigh => customBudgets.ExtraHigh,
            _ => null
        };
    }

    // Get the default budget for a level
    public static int GetDefaultThinkingBudget(ThinkingLevel level)
    {
        if (DefaultThinkingBudgets.TryGetValue(level, out var budget))
            return budget;

        return DefaultThinkingBudgets[ThinkingLevel.Medium];  // Fallback
    }

    // Clamp ExtraHigh to High for models that don't support it
    public static ThinkingLevel? ClampReasoning(ThinkingLevel? level)
    {
        if (level == ThinkingLevel.ExtraHigh)
            return ThinkingLevel.High;
        return level;
    }

    // Adjust maxTokens when thinking is enabled
    // Ensures enough room for both thinking and output
    public static (int MaxTokens, int ThinkingBudget) AdjustMaxTokensForThinking(
        LlmModel model,
        int? requestedMaxTokens,
        int thinkingBudget)
    {
        var baseMaxTokens = requestedMaxTokens ?? model.MaxTokens;
        var maxTokens = Math.Min(baseMaxTokens + thinkingBudget, model.MaxTokens);

        if (maxTokens <= thinkingBudget)
            thinkingBudget = Math.Max(0, maxTokens - 1024);

        return (maxTokens, thinkingBudget);
    }
}
```

### Budget adjustment algorithm

When thinking is enabled, the provider must ensure that `maxTokens` is large enough to accommodate both internal reasoning and output:

```
maxTokens = Min(baseMaxTokens + thinkingBudget, model.MaxTokens)

if (maxTokens <= thinkingBudget)
    thinkingBudget = Max(0, maxTokens - 1024)  // Reduce thinking to make room for output
```

This prevents scenarios where the thinking budget alone would consume all available tokens, leaving no room for the final response.

## Provider implementation: Anthropic example

The Anthropic provider translates thinking levels to the Claude API's `thinking` budget parameter:

```csharp
// From AnthropicProvider
if (options is SimpleStreamOptions simpleOptions && simpleOptions.Reasoning.HasValue)
{
    var thinkingLevel = simpleOptions.Reasoning.Value;
    
    // Clamp ExtraHigh to High (Claude API doesn't support it)
    var clampedLevel = SimpleOptionsHelper.ClampReasoning(thinkingLevel);
    
    // Resolve budget: custom or default
    var budgetLevel = SimpleOptionsHelper.GetBudgetForLevel(
        clampedLevel.Value,
        simpleOptions.ThinkingBudgets);
    
    var thinkingBudget = budgetLevel?.ThinkingBudget
        ?? SimpleOptionsHelper.GetDefaultThinkingBudget(clampedLevel.Value);
    
    // Adjust maxTokens to ensure room for both thinking and output
    var (adjustedMaxTokens, adjustedBudget) = SimpleOptionsHelper.AdjustMaxTokensForThinking(
        model,
        options.MaxTokens,
        thinkingBudget);
    
    // Build Claude API request body
    payload["thinking"] = new
    {
        type = "enabled",
        budget_tokens = adjustedBudget
    };
    
    payload["max_tokens"] = adjustedMaxTokens;
}
```

Each provider may have different constraints:
- **Anthropic (Claude):** Uses `thinking` budget and supports all 5 levels (clamping ExtraHigh to High)
- **OpenAI (o1, o3):** Uses reasoning/thinking parameters with different naming
- **Others:** May not support thinking at all, or require different parameter names

## End-to-end example

**Scenario:** User runs the coding agent with thinking enabled:

```bash
botnexus-coding-agent --thinking medium "Implement a binary search"
```

**Flow:**

1. **CLI parsing** (CommandParser) → Extracts `--thinking medium`
2. **CodingAgentConfig** → Reads config from `.botnexus-agent/config.json`, applies thinking override
3. **CodingAgent.CreateAsync()** → Sets `agent.State.ThinkingLevel = ThinkingLevel.Medium`
4. **User prompt sent** → `agent.PromptAsync("Implement a binary search")`
5. **Turn loop** (AgentLoopRunner):
   - Snapshots `AgentState`, including `ThinkingLevel.Medium`
   - Converts agent messages to provider messages
   - Builds `SimpleStreamOptions` with `Reasoning = ThinkingLevel.Medium`
6. **LlmClient.Stream()** → Routes to Anthropic provider
7. **Anthropic provider**:
   - Calls `SimpleOptionsHelper.GetDefaultThinkingBudget(ThinkingLevel.Medium)` → 8192
   - Calls `AdjustMaxTokensForThinking()` to ensure room for output
   - Builds Claude API payload with `thinking: { budget_tokens: 8192 }`
8. **LLM response** → Includes thinking content block:
   ```
   {
     "type": "thinking",
     "text": "Let me think through the binary search algorithm..."
   }
   ```
9. **StreamAccumulator** → Extracts thinking block as `ThinkingContent`
10. **Agent state** → `StreamingMessage.Content` now includes both thinking and text blocks
11. **User sees** → Full response with visible thinking tags (e.g., `<think>...</think>`)

## CLI integration

### `--thinking` command-line argument

The coding agent CLI exposes a `--thinking` flag for setting the thinking level at startup:

```
--thinking <level>       Set reasoning level: off|minimal|low|medium|high|xhigh
```

Example:

```bash
botnexus-coding-agent --thinking medium "Implement a binary search"
```

The `CommandParser` parses this argument and resolves it to a `ThinkingLevel?` value. The value `"off"` maps to `null` (thinking disabled).

### `/thinking` slash command

During an interactive session, you can view or change the thinking level with the `/thinking` slash command:

```
/thinking              Show current thinking level
/thinking <level>      Set thinking level (off|minimal|low|medium|high|xhigh)
```

When you change the thinking level, the session records a `thinking_level_change` metadata entry (e.g., `"off → medium"`) so the change is visible in session history.

### Configuration via config.json

You can also set the thinking level in `.botnexus-agent/config.json` using the `custom` dictionary:

```json
{
  "model": "claude-sonnet-4",
  "custom": {
    "thinking": "medium"
  }
}
```

The resolution order is: CLI `--thinking` flag → config.json `custom.thinking` → default (`null` / off).

### Programmatic access

Set thinking level programmatically via `Agent.State.ThinkingLevel`:

```csharp
var agent = await CodingAgent.CreateAsync(...);
agent.State.ThinkingLevel = ThinkingLevel.Medium;
await agent.PromptAsync("Complex coding task");
```

## Content blocks: ThinkingContent

LLM responses with thinking include `ThinkingContent` blocks:

```csharp
public record ThinkingContent : ContentBlock
{
    public string Text { get; init; }  // Internal reasoning
}
```

The `StreamAccumulator` recognizes these during streaming and includes them in the final `AssistantAgentMessage`. Some clients may render thinking blocks specially (e.g., collapsed sections or styled differently from main output).

## Token budgets and costs

Thinking tokens typically have the same cost as regular output tokens. When budgeting:

- **Total tokens = thinking budget + output tokens**
- **Cost** is calculated on both components

Example (Claude 3.5 Sonnet pricing):
- Input: $3 per million tokens
- Output: $15 per million tokens
- Thinking budget: 8,000 tokens (counts as output)
- Actual output: 2,000 tokens
- **Total cost** = 10,000 output tokens @ $15/M = $0.00015

Always account for the full token count when estimating costs.

## Troubleshooting

**Q: My thinking level is set but the LLM isn't generating thinking content**

A: Check that your model supports thinking. Not all models have reasoning capabilities. Verify:
- Model has `Reasoning: true` in `LlmModel` definition
- Provider correctly handles `SimpleStreamOptions.Reasoning`
- LLM API accepts your thinking budget format

**Q: The thinking budget exceeds my maxTokens**

A: `SimpleOptionsHelper.AdjustMaxTokensForThinking()` automatically adjusts. If you're seeing truncated output, increase `maxTokens` or decrease the thinking level.

**Q: ExtraHigh thinking level is being clamped to High**

A: This is expected behavior. Some models (e.g., Claude) don't support ExtraHigh. Use `ClampReasoning()` to detect this automatically, or adjust your code to use High-level thinking instead.

## Related documentation

- **[Provider System](01-providers.md)** — How providers handle streaming options
- **[Agent Core](02-agent-core.md)** — How thinking levels flow through the agent loop
- **[Coding Agent](03-coding-agent.md)** — How thinking integrates with built-in tools
- **[SimpleStreamOptions.cs](../src/agent/BotNexus.Agent.Providers.Core/StreamOptions.cs)** — Source implementation
- **[SimpleOptionsHelper.cs](../src/agent/BotNexus.Agent.Providers.Core/Utilities/SimpleOptionsHelper.cs)** — Budget calculation source
