# CLI wizard framework

The CLI wizard framework provides a reusable step-based flow engine for interactive CLI commands. It uses [Spectre.Console](https://spectreconsole.net/) for rich terminal prompts and is designed so multiple commands can share the same infrastructure.

## Overview

A wizard is a sequence of **steps** that execute in order, threading a shared **context** (key-value bag) between them. Steps can prompt the user, run internal checks, perform actions, or branch the flow by jumping to a named step.

**Key types:**

| Type | Location | Purpose |
|------|----------|---------|
| `WizardContext` | `Wizard/WizardContext.cs` | Case-insensitive key-value bag shared across steps |
| `IWizardStep` | `Wizard/IWizardStep.cs` | Step interface — `Name` + `ExecuteAsync` |
| `StepResult` | `Wizard/StepResult.cs` | Step outcome — `Continue`, `GoTo(name)`, `Abort` |
| `WizardRunner` | `Wizard/WizardRunner.cs` | Orchestrator — runs steps, handles branching |
| `WizardBuilder` | `Wizard/WizardBuilder.cs` | Fluent API for constructing wizards |

All source lives under `src/gateway/BotNexus.Cli/Wizard/`.

---

## Quick start

Build a wizard with the fluent builder and run it:

```csharp
var wizard = new WizardBuilder()
    .AskSelection("pick-provider", "Which provider?", "provider",
        new[] { "copilot", "openai", "anthropic" })
    .AskText("api-key", "Enter your API key:", "apiKey", secret: true)
    .AskConfirm("confirm", "Save this configuration?", "confirmed")
    .Check("gate", (ctx, _) =>
    {
        return Task.FromResult(ctx.Get<bool>("confirmed")
            ? StepResult.Continue()
            : StepResult.Abort());
    })
    .Action("save", async (ctx, ct) =>
    {
        var provider = ctx.Get<string>("provider");
        var key = ctx.Get<string>("apiKey");
        // Save to config...
    })
    .Build();

var result = await wizard.RunAsync();
if (result.Outcome == WizardOutcome.Completed)
    Console.WriteLine("Done!");
```

---

## Built-in step types

### TextPromptStep

Prompts the user for free-text input. Supports defaults, secret masking, and validation.

```csharp
builder.AskText("name", "What is the agent name?", "agentName",
    defaultValue: "assistant",
    validator: v => v.Length > 0
        ? ValidationResult.Success()
        : ValidationResult.Error("Name cannot be empty."));
```

### SelectionStep

Single-choice selection list. Generic — works with any type.

```csharp
builder.AskSelection("model", "Pick a model:", "selectedModel",
    models,
    displayConverter: m => $"{m.Id} — {m.Name}");
```

### MultiSelectionStep

Multi-choice selection list. Set `required: true` to require at least one selection.

```csharp
builder.AskMultiSelection("features", "Enable features:", "enabledFeatures",
    new[] { "mcp", "web-tools", "exec" },
    required: true);
```

### ConfirmStep

Yes/no confirmation. Stores a `bool` in the context.

```csharp
builder.AskConfirm("proceed", "Continue with setup?", "shouldProceed",
    defaultValue: true);
```

### CheckStep

Runs logic without user interaction. Controls flow by returning `Continue`, `GoTo`, or `Abort`.

```csharp
builder.Check("validate", (ctx, ct) =>
{
    var key = ctx.Get<string>("apiKey");
    return Task.FromResult(IsValid(key)
        ? StepResult.Continue()
        : StepResult.GoTo("api-key")); // Loop back to re-enter
});
```

### ActionStep

Performs work (writes files, calls APIs) and always continues to the next step.

```csharp
builder.Action("write-config", async (ctx, ct) =>
{
    await File.WriteAllTextAsync(path, json, ct);
    AnsiConsole.MarkupLine("[green]✓[/] Config saved.");
});
```

---

## Flow control

Steps execute sequentially by default. You can alter the flow with `StepResult`:

| Result | Behavior |
|--------|----------|
| `StepResult.Continue()` | Proceed to the next step |
| `StepResult.GoTo("step-name")` | Jump to the named step |
| `StepResult.Abort()` | Stop the wizard immediately |

### Branching example

```csharp
var wizard = new WizardBuilder()
    .AskSelection("auth-mode", "How do you want to authenticate?", "authMode",
        new[] { "oauth", "apikey" })
    .Check("route", (ctx, _) =>
    {
        var mode = ctx.Get<string>("authMode");
        return Task.FromResult(mode == "oauth"
            ? StepResult.GoTo("oauth-flow")
            : StepResult.GoTo("enter-key"));
    })
    .AskText("enter-key", "Enter API key:", "apiKey", secret: true)
    .Check("skip-oauth", (_, _) => Task.FromResult(StepResult.GoTo("done")))
    .Action("oauth-flow", async (ctx, ct) =>
    {
        // Run OAuth device code flow...
    })
    .Action("done", async (ctx, ct) =>
    {
        // Finalize setup...
    })
    .Build();
```

### Looping example

```csharp
builder
    .Action("init", (ctx, _) => { ctx.Set("count", 0); return Task.CompletedTask; })
    .Action("work", (ctx, _) =>
    {
        ctx.Set("count", ctx.Get<int>("count") + 1);
        return Task.CompletedTask;
    })
    .Check("loop", (ctx, _) =>
    {
        return Task.FromResult(ctx.Get<int>("count") < 3
            ? StepResult.GoTo("work")
            : StepResult.Continue());
    });
```

---

## WizardContext

The context is a case-insensitive dictionary that carries data between steps.

```csharp
// Store a value
ctx.Set("provider", "openai");

// Retrieve a value (throws if missing)
var provider = ctx.Get<string>("provider");

// Safe retrieval
if (ctx.TryGet<string>("provider", out var p))
    Console.WriteLine(p);

// Check existence
if (ctx.Has("apiKey")) { ... }

// Remove a value
ctx.Remove("tempData");
```

You can pass a pre-populated context to `RunAsync` to seed values before the wizard starts:

```csharp
var ctx = new WizardContext();
ctx.Set("configPath", "/home/user/.botnexus/config.json");
var result = await wizard.RunAsync(ctx);
```

---

## Extending the wizard

### Custom step class

Implement `IWizardStep` for complex or reusable steps:

```csharp
public sealed class OAuthFlowStep : IWizardStep
{
    public string Name => "oauth";

    public async Task<StepResult> ExecuteAsync(
        WizardContext context, CancellationToken cancellationToken)
    {
        var credentials = await CopilotOAuth.LoginAsync(
            onAuth: (uri, code) =>
            {
                AnsiConsole.MarkupLine($"Open: [link]{uri}[/]");
                AnsiConsole.MarkupLine($"Code: [bold]{code}[/]");
                return Task.CompletedTask;
            },
            ct: cancellationToken);

        context.Set("credentials", credentials);
        return StepResult.Continue();
    }
}

// Use it:
builder.Step(new OAuthFlowStep());
```

### Injecting IAnsiConsole for testing

Both `WizardBuilder` and `WizardRunner` accept an optional `IAnsiConsole`. In production this defaults to the real console. For tests, inject a test console or mock:

```csharp
// Production
var wizard = new WizardBuilder().AskText(...).Build();

// Test — non-interactive steps only (Check, Action)
var wizard = new WizardBuilder(testConsole).Check(...).Action(...).Build();
var result = await wizard.RunAsync();
```

Steps that use Spectre.Console prompts (`TextPromptStep`, `SelectionStep`, etc.) require real terminal interaction and are best tested at the integration level. Test your wizard logic by isolating branching and action steps with `CheckStep` and `ActionStep`.

---

## Existing usage

The `botnexus provider setup` command uses the wizard framework with branching, custom steps, and a seeded context:

```
botnexus provider setup
```

The flow demonstrates several wizard patterns:

1. **Seeded context** — loads existing config and passes it in via `WizardContext`
2. **Built-in steps** — `AskSelection` for provider, `AskText` for API key
3. **Branching** — `Check` step routes to OAuth flow or API key prompt based on provider type
4. **Custom steps** — `OAuthFlowStep` runs the GitHub device code flow, `PickModelStep` queries the model registry and prompts for selection
5. **Action step** — final `save` step writes the config and auth files

See `Commands/ProviderCommand.cs` for the full implementation.

---

## File layout

```
src/gateway/BotNexus.Cli/
  Wizard/
    IWizardStep.cs          # Step interface
    StepResult.cs           # Step outcome (Continue/GoTo/Abort)
    WizardContext.cs         # Shared key-value context
    WizardRunner.cs          # Step orchestrator
    WizardBuilder.cs         # Fluent builder API
    Steps/
      TextPromptStep.cs      # Free-text input
      SelectionStep.cs       # Single-choice list
      MultiSelectionStep.cs  # Multi-choice list
      ConfirmStep.cs         # Yes/no prompt
      CheckStep.cs           # Non-interactive logic gate
      ActionStep.cs          # Async work step
tests/BotNexus.Cli.Tests/
  Wizard/
    WizardContextTests.cs
    WizardRunnerTests.cs
    WizardBuilderTests.cs
  Commands/
    ProviderCommandTests.cs
```
