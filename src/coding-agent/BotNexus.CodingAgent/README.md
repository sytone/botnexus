# BotNexus.CodingAgent

Minimal coding agent CLI with read, edit, write, shell, and glob tools. Built on BotNexus.AgentCore and BotNexus.Providers.Core.

## Quick Start

Build the project:
```powershell
dotnet build src/coding-agent/BotNexus.CodingAgent/BotNexus.CodingAgent.csproj
```

Run interactively:
```powershell
dotnet run --project src/coding-agent/BotNexus.CodingAgent/
```

Run with a single prompt (non-interactive):
```powershell
dotnet run --project src/coding-agent/BotNexus.CodingAgent/ -- --non-interactive "Your prompt here"
```

### Setting Up with GitHub Copilot (OAuth)

The coding agent supports GitHub Copilot via OAuth device code flow, giving you access to all Copilot-proxied models (GPT-4o, Claude Sonnet, etc.) through your existing Copilot subscription.

**Step 1: Configure for Copilot**

Edit `.botnexus-agent/config.json` (created on first run):
```json
{
  "model": "gpt-4.1",
  "provider": "github-copilot"
}
```

**Step 2: Authenticate via device code flow**

Set your GitHub token via environment variable — Copilot checks these in order:
```powershell
# Option A: Use the GitHub CLI (recommended — handles OAuth automatically)
$env:GITHUB_TOKEN = $(gh auth token)

# Option B: Use a Copilot-specific token
$env:COPILOT_GITHUB_TOKEN = "gho_your_token_here"

# Option C: Use a general GitHub token
$env:GH_TOKEN = "ghp_your_token_here"
```

Or authenticate programmatically using `CopilotOAuth` from `BotNexus.Providers.Copilot`:
```csharp
using BotNexus.Providers.Copilot;

// Device code flow — displays a URL and code for the user to authorize
var credentials = await CopilotOAuth.LoginAsync(
    onUserCode: (url, code) =>
    {
        Console.WriteLine($"Open {url} and enter code: {code}");
    });

// Use the token
Environment.SetEnvironmentVariable("COPILOT_GITHUB_TOKEN", credentials.AccessToken);

// Refresh when expired (credentials.ExpiresAt is a Unix timestamp)
if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= credentials.ExpiresAt)
{
    credentials = await CopilotOAuth.RefreshAsync(credentials);
}
```

**Step 3: Run**
```powershell
dotnet run --project src/coding-agent/BotNexus.CodingAgent/ -- --provider github-copilot --model gpt-4.1
```

**Available Copilot models** include `gpt-4.1`, `gpt-4o`, `claude-sonnet-4-20250514`, `o4-mini`, and others — whatever your Copilot subscription provides access to.

## CLI Usage

### Command Line Options

```
botnexus-coding-agent [options] [prompt]

Options:
  --model <model>          Override model id (e.g., gpt-4.1, claude-3-opus)
  --provider <provider>    Override provider id (e.g., openai, anthropic)
  --resume <session-id>    Resume an existing session
  --non-interactive        Run one prompt and exit
  --verbose                Enable verbose logs
  --help                   Show this help
```

### Usage Patterns

**Interactive mode** (starts a chat loop):
```powershell
dotnet run --project src/coding-agent/BotNexus.CodingAgent/
```

**Single prompt** (non-interactive):
```powershell
dotnet run --project src/coding-agent/BotNexus.CodingAgent/ -- --non-interactive "Summarize all C# files in src/"
```

**Override model**:
```powershell
dotnet run --project src/coding-agent/BotNexus.CodingAgent/ -- --model gpt-4.1 --provider openai
```

**Resume session**:
```powershell
dotnet run --project src/coding-agent/BotNexus.CodingAgent/ -- --resume abc123def456
```

**Verbose logging**:
```powershell
dotnet run --project src/coding-agent/BotNexus.CodingAgent/ -- --verbose
```

## Built-in Tools

The coding agent includes five core tools, available within any prompt:

### read

**Purpose**: Read file content with line numbers or list directory entries.

**Parameters**:
- `path` (string, required): File or directory path relative to working directory
- `range` (string, optional): Line range in format `START-END` (e.g., `1-50`)

**Output**:
- Files: Line-numbered records (`1 | line content`) up to 2000 lines
- Directories: Formatted listing with file names and sizes

**Example**:
```
> read
path: src/Tools/ReadTool.cs
range: 1-50
```

### write

**Purpose**: Write complete file content to disk, creating parent directories as needed.

**Parameters**:
- `path` (string, required): Target file path (will be created if missing)
- `content` (string, required): Full file content to write

**Output**:
- Success: Confirmation with file path
- Error: Path validation or I/O errors

**Example**:
```
> write
path: src/MyFeature/Processor.cs
content: 
using System;

namespace MyFeature;

public class Processor { }
```

### edit

**Purpose**: Replace a single exact string in an existing file.

**Parameters**:
- `path` (string, required): Target file path
- `old_str` (string, required): Exact string to replace (must match exactly once)
- `new_str` (string, required): Replacement string

**Output**:
- Success: Confirmation with character count
- Error: If `old_str` not found, found multiple times, or file doesn't exist

**Notes**:
- Line endings are normalized to `\n` for matching, then converted back to file's style
- Strictness prevents ambiguous edits; if multiple matches exist, provide more context

**Example**:
```
> edit
path: src/MyClass.cs
old_str: public int Value { get; set; }
new_str: public int Value { get; set; } = 0;
```

### shell

**Purpose**: Execute shell commands with timeout and captured output.

**Parameters**:
- `command` (string, required): Shell command text to execute
- `timeout` (integer, optional): Timeout in seconds (default: 120)

**Output**:
- stdout/stderr combined up to 10,000 characters
- Exit code included in result

**Platform behavior**:
- Windows: Executes via PowerShell
- Linux/macOS: Executes via bash

**Example**:
```
> shell
command: dotnet build -c Release
timeout: 180
```

### glob

**Purpose**: Find files by glob pattern with optional base path.

**Parameters**:
- `pattern` (string, required): Glob pattern (e.g., `**/*.cs`, `src/**/*.json`)
- `base` (string, optional): Base directory for glob expansion (default: working directory)

**Output**:
- List of matching file paths relative to working directory
- Respects .gitignore rules

**Examples**:
```
> glob
pattern: **/*.cs

> glob
pattern: **/*.test.cs
base: src/Tests/
```

## Sessions

Sessions persist agent state, allowing you to resume conversations across invocations.

### Session Structure

Sessions are stored in `.botnexus-agent/sessions/<session-id>.jsonl`:

```
<session-id>.jsonl
├── session_header      # Session metadata, version, and parent-session reference
├── message             # User/assistant/system timeline entries
├── tool_result         # Tool result entries
├── compaction_summary  # Compaction summary entries
└── metadata            # Leaf pointer + branch metadata
```

Legacy folder sessions (`session.json` + `messages.jsonl`) are still supported and auto-loaded.

### Creating Sessions

By default, running the agent creates a new session:
```powershell
dotnet run --project src/coding-agent/BotNexus.CodingAgent/
```

The session ID is generated automatically (UUID format).

### Resuming Sessions

Continue an existing session by ID:
```powershell
dotnet run --project src/coding-agent/BotNexus.CodingAgent/ -- --resume abc123def456
```

The agent loads all prior messages and continues with the same model and context.

### Auto-save Behavior

- **Interactive mode**: Session is saved after each turn (after user input is processed)
- **Non-interactive mode**: Session is saved after running the single prompt
- Session directory is created automatically if it doesn't exist

## Configuration

Configuration is loaded from defaults, global user settings, and project-local settings in that order.

### Directories

The agent creates the following structure in `.botnexus-agent/`:

```
.botnexus-agent/
├── config.json          # Project-local configuration
├── sessions/            # Session storage (see Sessions)
├── extensions/          # Extension assemblies (.dll files)
└── skills/              # Skills definitions and prompts
```

### Configuration Resolution Order

1. **Defaults**: Hard-coded defaults (all directories in `.botnexus-agent/`)
2. **Global**: `~/.botnexus/coding-agent.json` (user home directory)
3. **Local**: `.botnexus-agent/config.json` (project root)

Later configurations override earlier ones. Command-line flags take highest priority.

### config.json Format

```json
{
  "model": "gpt-4.1",
  "provider": "openai",
  "apiKey": "sk-...",
  "maxToolIterations": 40,
  "maxContextTokens": 100000,
  "allowedCommands": ["npm", "dotnet"],
  "blockedPaths": [".env", "secrets/"],
  "custom": {
    "verbose": false,
    "customKey": "customValue"
  }
}
```

### Configuration Properties

- **model** (string): LLM model ID (e.g., `gpt-4.1`, `claude-3-opus`)
- **provider** (string): Provider ID (e.g., `openai`, `anthropic`)
- **apiKey** (string): API key for the provider (or use environment variable)
- **maxToolIterations** (integer): Maximum tool calls per turn (default: 40)
- **maxContextTokens** (integer): Maximum tokens for context window (default: 100000)
- **allowedCommands** (array): Shell commands that are allowed (if empty, all are allowed)
- **blockedPaths** (array): Paths that cannot be written to or read from
- **custom** (object): User-defined configuration (accessible via code)

### Environment Variables

API keys can also be provided via environment variables:
- `{PROVIDER}_API_KEY` (e.g., `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`)
- Falls back to `GITHUB_TOKEN` if no specific provider key is set

## Extensions

Extensions allow you to add custom tools to the coding agent. They are implemented as .NET assemblies.

### Writing an Extension

1. Create a class that implements `IExtension`:

```csharp
using BotNexus.AgentCore.Tools;
using BotNexus.CodingAgent.Extensions;

public class MyExtension : IExtension
{
    public string Name => "MyExtension";
    
    public IReadOnlyList<IAgentTool> GetTools()
    {
        return new IAgentTool[]
        {
            new MyCustomTool()
        };
    }
}
```

2. Implement `IAgentTool` for each custom tool:

```csharp
using BotNexus.AgentCore.Tools;
using System.Text.Json;

public class MyCustomTool : IAgentTool
{
    public string Name => "my-custom-tool";
    public string Label => "My Custom Tool";
    
    public Tool Definition => new(
        Name,
        "Description of what this tool does.",
        JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "param1": {
              "type": "string",
              "description": "First parameter"
            }
          },
          "required": ["param1"]
        }
        """)
    );
    
    public async Task<string> ExecuteAsync(JsonElement input, CancellationToken ct)
    {
        var param1 = input.GetProperty("param1").GetString();
        // Implementation...
        return "Result";
    }
}
```

### Loading Extensions

Extensions are loaded from `.botnexus-agent/extensions/` directory:

1. Build your extension project to a `.dll` file
2. Place the `.dll` in `.botnexus-agent/extensions/`
3. When the agent starts, it will:
   - Scan the directory for `.dll` files
   - Load types implementing `IExtension` with parameterless constructors
   - Instantiate each extension and collect its tools

Extensions are loaded once at startup. Tools from extensions are merged with built-in tools.

## Skills

Skills are reusable agent capabilities loaded from skill files. They are included in the system prompt to guide agent behavior.

### Loading Skills

Skills are loaded from `.botnexus-agent/skills/` and referenced in `AGENTS.md` (if present in the project root).

1. Create a skill file (e.g., `.botnexus-agent/skills/my-skill.md`):

```markdown
# My Skill

This skill provides instructions for handling a specific task.

## Usage

When the user asks about X, follow this pattern:
1. First, read the configuration file
2. Parse the JSON
3. Apply the transformation
```

2. Reference in `AGENTS.md`:

```markdown
# Agents Configuration

## Coding Agent Skills

- ./. botnexus-agent/skills/my-skill.md
- .botnexus-agent/skills/another-skill.md
```

Skills are included in the system prompt and become part of the agent's permanent knowledge for the session.

## Safety Hooks

The coding agent includes safety validation before executing write and shell tools.

### Path Validation

Before executing `write` or `edit`:
- Path must be within the configured working directory (no traversal attacks)
- If `blockedPaths` is configured, path must not match any blocked path patterns
- Validation occurs before any file I/O

### Command Blocking

Before executing `shell`:
- Command is checked against a default blocklist (e.g., `rm -rf /`, `format`, `del /s /q`)
- If `allowedCommands` is configured, command must start with an allowed command
- Validation occurs before the process is spawned

### Large Write Warning

When executing `write` or `edit` with content > 1 MB:
- A warning is emitted to stderr
- The operation proceeds (not blocked)

### Customizing Safety

To customize safety validation:

1. **Block a path**:
```json
{
  "blockedPaths": [".env", "secrets/", "credentials.json"]
}
```

2. **Allow specific commands**:
```json
{
  "allowedCommands": ["npm", "dotnet", "cargo"]
}
```

If `allowedCommands` is empty, all commands are allowed (subject to default blocklist).

3. **Override defaults in code**:
Modify `SafetyHooks.cs` to customize validation logic for your environment.

## Architecture

### Component Overview

The coding agent is built on three core layers:

```
BotNexus.CodingAgent (CLI)
    ↓
BotNexus.AgentCore (Agent runtime, tool pipeline, state)
    ↓
BotNexus.Providers.Core (LLM abstraction, model registry)
```

### How CodingAgent Uses AgentCore

1. **Agent Creation**: `CodingAgent.CreateAsync()` builds an `Agent` from `AgentCore`
2. **Tool Registration**: Built-in tools + extension tools are registered
3. **System Prompt**: `SystemPromptBuilder` creates context-aware instructions
4. **Hook Integration**: Safety and audit hooks intercept tool calls
5. **Message Loop**: `Agent` handles LLM communication, streaming, and state management

### How CodingAgent Uses Providers.Core

1. **Model Resolution**: `ModelRegistry` matches model ID + provider to `LlmModel`
2. **API Key Resolution**: Environment variables and config checked in order
3. **LLM Client**: `LlmClient` handles streaming and completion requests
4. **Message Conversion**: Provider-specific message format conversion

### Tool Execution Flow

```
User Input
    ↓
Agent Loop (AgentCore)
    ↓
Tool Call Request
    ↓
BeforeToolCall Hook (SafetyHooks validates path/command)
    ↓
Tool Execution (read/write/edit/shell/glob)
    ↓
AfterToolCall Hook (AuditHooks logs call)
    ↓
Tool Result → LLM Context
    ↓
LLM Response → Next Turn
```

## Differences from pi-mono

BotNexus.CodingAgent is a C# port inspired by pi-mono's architecture. Key differences:

| Aspect | pi-mono | BotNexus.CodingAgent |
|--------|---------|---------------------|
| **Language** | TypeScript/Node.js | C# (.NET 10) |
| **Runtime** | Node.js 18+ | .NET 10 runtime |
| **Package Manager** | npm | NuGet + dotnet CLI |
| **Extension Interface** | JavaScript modules | IExtension interface + reflection |
| **Skills Loading** | Package imports | AGENTS.md + .botnexus-agent/skills/ |
| **CLI** | npm script aliases | dotnet run + CommandParser |
| **Session Storage** | JSON files in session dir | Same (.jsonl for messages) |
| **Tool Execution** | JavaScript-based | C# tool classes |
| **Configuration** | Environment + .pirc | Environment + config.json |
| **Minimal Design** | ~1000 tokens system prompt | Similar minimal approach |
| **Hooks/Validation** | Custom provider plugins | BeforeToolCall/AfterToolCall hooks |

Both systems prioritize:
- Minimal core functionality (~1000 token system prompt)
- Extensibility via custom tools
- Multi-provider LLM support
- Session persistence
- Filesystem safety
- Minimal dependencies

## Building and Testing

### Build

```powershell
dotnet build src/coding-agent/BotNexus.CodingAgent/BotNexus.CodingAgent.csproj
```

### Run

```powershell
dotnet run --project src/coding-agent/BotNexus.CodingAgent/
```

### Release Build

```powershell
dotnet build src/coding-agent/BotNexus.CodingAgent/BotNexus.CodingAgent.csproj -c Release
```

The output binary is at `bin/Release/net10.0/BotNexus.CodingAgent.dll`.

## Project Structure

```
src/coding-agent/BotNexus.CodingAgent/
├── Program.cs                  # Entry point
├── CodingAgent.cs              # Agent factory
├── CodingAgentConfig.cs        # Configuration loading
├── SystemPromptBuilder.cs      # System prompt assembly
├── Cli/
│   ├── CommandParser.cs        # CLI argument parsing
│   ├── CommandOptions.cs       # Parsed options record
│   ├── InteractiveLoop.cs      # REPL loop
│   └── OutputFormatter.cs      # Formatted output
├── Tools/
│   ├── ReadTool.cs             # File/directory reading
│   ├── WriteTool.cs            # File writing
│   ├── EditTool.cs             # In-place file editing
│   ├── ShellTool.cs            # Command execution
│   └── GlobTool.cs             # Glob pattern matching
├── Extensions/
│   ├── IExtension.cs           # Extension contract
│   ├── ExtensionLoader.cs      # Assembly loading
│   └── SkillsLoader.cs         # Skills file loading
├── Hooks/
│   ├── SafetyHooks.cs          # Path & command validation
│   └── AuditHooks.cs           # Execution logging
├── Session/
│   ├── SessionInfo.cs          # Session metadata record
│   └── SessionManager.cs       # Session persistence
├── Utils/
│   ├── PathUtils.cs            # Path resolution & security
│   ├── GitUtils.cs             # Git branch/status detection
│   └── PackageManagerDetector.cs
└── BotNexus.CodingAgent.csproj # Project configuration
```

## Contributing

When extending the coding agent:

1. **New Tools**: Implement `IAgentTool`, add to `CreateTools()` in `CodingAgent.cs`
2. **Extensions**: Implement `IExtension` in a separate assembly
3. **Safety Rules**: Customize `SafetyHooks.cs` for environment-specific needs
4. **Configuration**: Add properties to `CodingAgentConfig` class
5. **System Prompt**: Update `SystemPromptBuilder` for new capabilities

## License

This project is part of the BotNexus framework. See the root LICENSE file for details.
