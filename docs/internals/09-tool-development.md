# Tool Development

Tools are the actions your agent can perform — reading files, running shell commands, searching code. Any tool that implements `IAgentTool` can be registered and invoked by the LLM. This guide teaches you to design, implement, test, and integrate custom tools.

**Prerequisites:** Familiarity with C# records, async/await, and the [Agent Core](02-agent-core.md) doc.

---

## The IAgentTool interface

Every tool must implement this interface:

```csharp
public interface IAgentTool
{
    string Name { get; }  // Machine-readable identifier (e.g., "read_file")
    string Label { get; }  // Human-readable name (e.g., "Read File")
    Tool Definition { get; }  // JSON Schema for the LLM

    // Validate and transform arguments before execution
    Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default);

    // Execute the tool
    Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null);

    // Optional: contribute guidance to the system prompt
    string? GetPromptSnippet() => null;
    IReadOnlyList<string> GetPromptGuidelines() => [];
}
```

| Member | Purpose |
|--------|---------|
| `Name` | Unique identifier. Used when the LLM calls the tool. Must be lowercase alphanumeric + underscores. Lookup is **case-insensitive** (`StringComparison.OrdinalIgnoreCase`). |
| `Label` | Display name for UI and documentation. |
| `Definition` | `Tool` record with `Name`, `Description`, and JSON Schema `Parameters`. Sent to the LLM. |
| `PrepareArgumentsAsync` | Validate and normalize arguments. Throw `ArgumentException` on invalid input. |
| `ExecuteAsync` | Run the tool. Takes `toolCallId` for correlation. Return `AgentToolResult` with content and optional error. |
| `GetPromptSnippet` | (Optional) Include in system prompt as-is. Use for short examples. Default: `null`. |
| `GetPromptGuidelines` | (Optional) List of guideline strings contributed to system prompt. Default: empty list. |

---

## Example 1: Simple echo tool

Start with the simplest possible tool — echo back the user's message:

```csharp
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using System.Text.Json;

public sealed class EchoTool : IAgentTool
{
    public string Name => "echo";
    public string Label => "Echo";

    public Tool Definition => new(
        Name: "echo",
        Description: "Echo the input message back to the caller.",
        Parameters: JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "message": { "type": "string" }
          },
          "required": ["message"]
        }
        """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var message = arguments.TryGetValue("message", out var msg)
            ? msg?.ToString()
            : null;

        if (string.IsNullOrEmpty(message))
            throw new ArgumentException("message is required and cannot be empty.");

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(
            new Dictionary<string, object?> { ["message"] = message });
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var message = (string)arguments["message"]!;

        return new AgentToolResult(
            Content: new List<AgentToolContent>
            {
                new AgentToolContent(
                    Type: AgentToolContentType.Text,
                    Text: $"Echo: {message}")
            },
            Details: null,
            IsError: false);
    }

    public string? GetPromptSnippet() => null;
    public IReadOnlyList<string> GetPromptGuidelines() => ["Use this tool to echo messages for testing."];
}
```

---

## Example 2: Calculator tool with argument normalization

A more realistic tool that validates and normalizes numeric arguments:

```csharp
using System.Globalization;

public sealed class CalculatorTool : IAgentTool
{
    public string Name => "calculate";
    public string Label => "Calculator";

    public Tool Definition => new(
        Name: "calculate",
        Description: "Perform arithmetic operations: add, subtract, multiply, divide.",
        Parameters: JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "operation": {
              "type": "string",
              "enum": ["add", "subtract", "multiply", "divide"]
            },
            "a": { "type": "number" },
            "b": { "type": "number" }
          },
          "required": ["operation", "a", "b"]
        }
        """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        // Extract and validate operation
        if (!arguments.TryGetValue("operation", out var opObj) || opObj is not string operation)
            throw new ArgumentException("operation is required and must be a string.");

        var validOps = new[] { "add", "subtract", "multiply", "divide" };
        if (!validOps.Contains(operation, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"operation must be one of: {string.Join(", ", validOps)}");

        // Extract and parse numeric arguments
        if (!arguments.TryGetValue("a", out var aObj))
            throw new ArgumentException("a is required.");

        if (!arguments.TryGetValue("b", out var bObj))
            throw new ArgumentException("b is required.");

        var a = Convert.ToDouble(aObj, CultureInfo.InvariantCulture);
        var b = Convert.ToDouble(bObj, CultureInfo.InvariantCulture);

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(
            new Dictionary<string, object?>
            {
                ["operation"] = operation.ToLowerInvariant(),
                ["a"] = a,
                ["b"] = b
            });
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var operation = (string)arguments["operation"]!;
        var a = (double)arguments["a"]!;
        var b = (double)arguments["b"]!;

        try
        {
            var result = operation switch
            {
                "add" => a + b,
                "subtract" => a - b,
                "multiply" => a * b,
                "divide" => b == 0
                    ? throw new InvalidOperationException("Division by zero")
                    : a / b,
                _ => throw new InvalidOperationException($"Unknown operation: {operation}")
            };

            return new AgentToolResult(
                Content: new List<AgentToolContent>
                {
                    new AgentToolContent(
                        Type: AgentToolContentType.Text,
                        Text: $"{a} {operation} {b} = {result}")
                },
                Details: null,
                IsError: false);
        }
        catch (Exception ex)
        {
            return new AgentToolResult(
                Content: new List<AgentToolContent>
                {
                    new AgentToolContent(
                        Type: AgentToolContentType.Text,
                        Text: $"Error: {ex.Message}")
                },
                Details: ex.Message,
                IsError: true);
        }
    }

    public string? GetPromptSnippet() => null;
    public IReadOnlyList<string> GetPromptGuidelines() =>
        ["Use this tool for arithmetic. Division by zero returns an error."];
}
```

---

## Example 3: Database query tool with streaming results

For long-running operations, use `updateCallback` to stream partial results:

```csharp
using System.Text;

public sealed class DatabaseQueryTool : IAgentTool
{
    private readonly string _connectionString;

    public DatabaseQueryTool(string connectionString)
    {
        _connectionString = connectionString;
    }

    public string Name => "query_database";
    public string Label => "Database Query";

    public Tool Definition => new(
        Name: "query_database",
        Description: "Execute a SQL SELECT query and return results.",
        Parameters: JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "query": { "type": "string" },
            "limit": { "type": "integer", "default": 100 }
          },
          "required": ["query"]
        }
        """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetValue("query", out var queryObj) || queryObj is not string query)
            throw new ArgumentException("query is required and must be a string.");

        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("query cannot be empty.");

        var limit = 100;
        if (arguments.TryGetValue("limit", out var limitObj))
        {
            limit = Convert.ToInt32(limitObj);
            if (limit <= 0 || limit > 10000)
                throw new ArgumentOutOfRangeException(nameof(arguments), "limit must be between 1 and 10000.");
        }

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(
            new Dictionary<string, object?> { ["query"] = query, ["limit"] = limit });
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var query = (string)arguments["query"]!;
        var limit = (int)arguments["limit"]!;

        var results = new StringBuilder();
        var rowCount = 0;

        try
        {
            // Simulate querying a database
            for (int i = 1; i <= Math.Min(limit, 50); i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var row = $"Row {i}: id={i}, name=Item{i}\n";
                results.Append(row);
                rowCount++;

                // Update callback: stream partial results every 10 rows
                if (i % 10 == 0 && onUpdate is not null)
                {
                    await onUpdate(new AgentToolContent(
                        Type: AgentToolContentType.Text,
                        Text: $"Processed {i} rows..."));
                }

                // Simulate network/query delay
                await Task.Delay(10, cancellationToken);
            }

            return new AgentToolResult(
                Content: new List<AgentToolContent>
                {
                    new AgentToolContent(
                        Type: AgentToolContentType.Text,
                        Text: $"Query returned {rowCount} rows:\n{results}")
                },
                Details: $"Query: {query}, Limit: {limit}, Rows: {rowCount}",
                IsError: false);
        }
        catch (OperationCanceledException)
        {
            return new AgentToolResult(
                Content: new List<AgentToolContent>
                {
                    new AgentToolContent(
                        Type: AgentToolContentType.Text,
                        Text: "Query cancelled by user.")
                },
                Details: null,
                IsError: true);
        }
        catch (Exception ex)
        {
            return new AgentToolResult(
                Content: new List<AgentToolContent>
                {
                    new AgentToolContent(
                        Type: AgentToolContentType.Text,
                        Text: $"Query failed: {ex.Message}")
                },
                Details: ex.ToString(),
                IsError: true);
        }
    }

    public string? GetPromptSnippet() => null;
    public IReadOnlyList<string> GetPromptGuidelines() =>
        ["Use this tool for SELECT queries only. No INSERT, UPDATE, or DELETE."];
}
```

---

## Tool lifecycle: The full flow

When a tool is called, here's what happens:

```
1. LLM response includes: { "toolUseId": "123", "toolName": "read_file", "arguments": {...} }
   ↓
2. ToolExecutor looks up the tool by name
   ↓
3. PrepareArgumentsAsync() — Validate and normalize arguments
   ├─ Throw ArgumentException on error → tool fails, LLM gets error message
   └─ Return prepared arguments
   ↓
4. BeforeToolCall hook (if registered)
   ├─ Can block execution: return BeforeToolCallResult(Block: true)
   └─ Proceed to execution
   ↓
5. ExecuteAsync() — Run the actual operation
   ├─ Use updateCallback to stream long results
   └─ Return AgentToolResult
   ↓
6. AfterToolCall hook (if registered)
   ├─ Can transform the result
   └─ Result is appended to conversation as ToolResultMessage
   ↓
7. LLM sees tool result and either:
   ├─ Makes another tool call
   ├─ Continues generating text
   └─ Finishes the turn
```

---

## Registering tools with the agent

```csharp
using BotNexus.Agent.Core;

var agent = new Agent(agentOptions);

var tools = new List<IAgentTool>
{
    new EchoTool(),
    new CalculatorTool(),
    new DatabaseQueryTool("connection_string")
};

agent.State.Tools = tools;
```

Or pass tools when creating a `CodingAgent`:

```csharp
using BotNexus.CodingAgent;

var agent = await CodingAgent.CreateAsync(
    config,
    workingDirectory,
    authManager,
    llmClient,
    modelRegistry,
    extensionRunner,
    extensionTools: new List<IAgentTool> { new EchoTool() },
    skills: null);
```

---

## Testing tools

Write unit tests for your tool:

```csharp
using Xunit;

[Fact]
public async Task CalculatorTool_AdditionWorks()
{
    var tool = new CalculatorTool();

    var args = new Dictionary<string, object?> { ["operation"] = "add", ["a"] = 2, ["b"] = 3 };
    var prepared = await tool.PrepareArgumentsAsync(args);
    var result = await tool.ExecuteAsync(prepared);

    Assert.False(result.IsError);
    Assert.Contains("2 add 3 = 5", result.Content[0].Text);
}

[Fact]
public async Task CalculatorTool_DivisionByZeroErrors()
{
    var tool = new CalculatorTool();

    var args = new Dictionary<string, object?> { ["operation"] = "divide", ["a"] = 1, ["b"] = 0 };
    var prepared = await tool.PrepareArgumentsAsync(args);
    var result = await tool.ExecuteAsync(prepared);

    Assert.True(result.IsError);
}

[Fact]
public async Task CalculatorTool_InvalidOperationThrows()
{
    var tool = new CalculatorTool();

    var args = new Dictionary<string, object?> { ["operation"] = "invalid", ["a"] = 1, ["b"] = 2 };

    var ex = await Assert.ThrowsAsync<ArgumentException>(
        () => tool.PrepareArgumentsAsync(args));
    Assert.Contains("must be one of", ex.Message);
}
```

---

## System prompt contribution

Use `GetPromptSnippet()` and `GetPromptGuidelines()` to help the LLM use your tool correctly:

```csharp
public string? GetPromptSnippet() => """
Example usage:
{
  "toolUseId": "abc123",
  "toolName": "query_database",
  "arguments": {
    "query": "SELECT * FROM users WHERE active = true",
    "limit": 100
  }
}
""";

public string? GetPromptGuidelines() => """
- Use SELECT queries only
- Avoid SELECT * on large tables (use LIMIT)
- For joins, provide explicit join conditions
- Handle NULL values in results
""";
```

The `SystemPromptBuilder` collects these and includes them in the system prompt.

---

## Error handling best practices

1. **Validate early** — Throw in `PrepareArgumentsAsync`
2. **Encode errors in results** — Never throw from `ExecuteAsync`; return `AgentToolResult` with `IsError: true`
3. **Provide context** — Include the `Details` field with stack traces or query details for debugging
4. **Handle cancellation** — Respect `CancellationToken` and catch `OperationCanceledException`

```csharp
public async Task<AgentToolResult> ExecuteAsync(
    string toolCallId,
    IReadOnlyDictionary<string, object?> arguments,
    CancellationToken cancellationToken = default,
    AgentToolUpdateCallback? onUpdate = null)
{
    try
    {
        cancellationToken.ThrowIfCancellationRequested();
        // ... your code
    }
    catch (OperationCanceledException)
    {
        return new AgentToolResult(
            Content: new List<AgentToolContent>
            {
                new AgentToolContent(AgentToolContentType.Text, "Operation cancelled")
            },
            IsError: true);
    }
    catch (Exception ex)
    {
        return new AgentToolResult(
            Content: new List<AgentToolContent>
            {
                new AgentToolContent(AgentToolContentType.Text, $"Error: {ex.Message}")
            },
            Details: ex.ToString(),
            IsError: true);
    }
}
```

---

## Related documentation

- **[Agent Core — Tool Execution](02-agent-core.md#tool-execution)** — How tools fit into the agent loop
- **[Coding Agent — Built-in Tools](03-coding-agent.md#built-in-tools)** — Examples: ReadTool, ListDirectoryTool, WriteTool, EditTool, ShellTool, GrepTool, GlobTool
- **[Building Your Own Agent](04-building-your-own.md)** — Register and use tools in your agent
- **[ReadTool.cs source](../src/coding-agent/BotNexus.CodingAgent/Tools/ReadTool.cs)** — Reference implementation
- **[IAgentTool interface source](../src/agent/BotNexus.Agent.Core/Tools/IAgentTool.cs)** — Complete interface definition
