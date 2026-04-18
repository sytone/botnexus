# Extension Development

This guide covers how to extend BotNexus with custom tools, MCP servers, and skills.

## Extension Types

BotNexus supports three primary extension types:

1. **Tools** — C# classes that implement `IAgentTool` interface
2. **MCP Servers** — External processes implementing Model Context Protocol
3. **Skills** — Markdown files containing domain knowledge and patterns

---

## Custom Tools

Tools are C# classes that agents can invoke. BotNexus discovers tools automatically from extension assemblies.

### Tool Structure

**1. Create a Class Library Project:**

```bash
dotnet new classlib -n BotNexus.Extensions.MyTool
cd BotNexus.Extensions.MyTool
dotnet add package BotNexus.Agent.Core
```

**2. Implement `IAgentTool`:**

```csharp
using BotNexus.Agent.Core.Contracts;
using BotNexus.Agent.Core.Contracts.Models;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace BotNexus.Extensions.MyTool;

public class CalculatorTool : IAgentTool
{
    public string Name => "calculator";

    public string Description => "Perform arithmetic calculations";

    public async Task<ToolResult> ExecuteAsync(
        ToolRequest request,
        CancellationToken cancellationToken)
    {
        var input = request.DeserializeInput<CalculatorInput>();
        
        if (input == null)
        {
            return ToolResult.Failure("Invalid input");
        }

        var result = input.Operation switch
        {
            "add" => input.A + input.B,
            "subtract" => input.A - input.B,
            "multiply" => input.A * input.B,
            "divide" => input.B != 0 ? input.A / input.B : 
                throw new InvalidOperationException("Division by zero"),
            _ => throw new InvalidOperationException($"Unknown operation: {input.Operation}")
        };

        return ToolResult.Success(new CalculatorOutput 
        { 
            Result = result 
        });
    }

    public ToolSchema GetSchema()
    {
        return new ToolSchema
        {
            Name = Name,
            Description = Description,
            InputSchema = new
            {
                type = "object",
                properties = new
                {
                    operation = new
                    {
                        type = "string",
                        description = "Arithmetic operation",
                        @enum = new[] { "add", "subtract", "multiply", "divide" }
                    },
                    a = new
                    {
                        type = "number",
                        description = "First operand"
                    },
                    b = new
                    {
                        type = "number",
                        description = "Second operand"
                    }
                },
                required = new[] { "operation", "a", "b" }
            }
        };
    }
}

public class CalculatorInput
{
    [JsonPropertyName("operation")]
    [Description("Arithmetic operation to perform")]
    public string Operation { get; set; } = string.Empty;

    [JsonPropertyName("a")]
    [Description("First operand")]
    public double A { get; set; }

    [JsonPropertyName("b")]
    [Description("Second operand")]
    public double B { get; set; }
}

public class CalculatorOutput
{
    [JsonPropertyName("result")]
    public double Result { get; set; }
}
```

### Key Components

**`Name`**: Unique tool identifier (used in agent `toolIds`)

**`Description`**: What the tool does (shown to the LLM)

**`ExecuteAsync`**: Tool execution logic
- Receives `ToolRequest` with input parameters
- Returns `ToolResult` with success/failure and output data

**`GetSchema`**: JSON Schema defining tool inputs
- Follows OpenAPI/JSON Schema conventions
- LLM uses this to understand how to call the tool

### Tool Result

Return results using `ToolResult`:

```csharp
// Success with data
return ToolResult.Success(new { message = "Operation completed" });

// Success with string
return ToolResult.Success("File saved successfully");

// Failure with error
return ToolResult.Failure("File not found");

// Failure with exception
return ToolResult.Failure(exception);
```

### Extension Manifest

Create `botnexus-extension.json` in your project root:

```json
{
  "extensionId": "my-tool",
  "name": "My Custom Tool",
  "version": "1.0.0",
  "description": "Custom tool extension for BotNexus",
  "author": "Your Name",
  "assembly": "BotNexus.Extensions.MyTool.dll",
  "dependencies": []
}
```

### Build and Deploy

**1. Build the project:**

```bash
dotnet build -c Release
```

**2. Copy to extensions directory:**

```bash
# Create extension folder
mkdir -p ~/.botnexus/extensions/my-tool

# Copy assembly and dependencies
cp bin/Release/net10.0/*.dll ~/.botnexus/extensions/my-tool/
cp botnexus-extension.json ~/.botnexus/extensions/my-tool/
```

**3. Enable in agent config:**

```json
{
  "agents": {
    "assistant": {
      "toolIds": ["calculator"]
    }
  }
}
```

BotNexus automatically discovers and loads the tool on next startup or config reload.

### Built-in Tool Examples

**File Operations:**
```csharp
public class ReadFileTool : IAgentTool
{
    public string Name => "read_file";
    
    public async Task<ToolResult> ExecuteAsync(
        ToolRequest request,
        CancellationToken cancellationToken)
    {
        var input = request.DeserializeInput<ReadFileInput>();
        var content = await File.ReadAllTextAsync(input.Path, cancellationToken);
        return ToolResult.Success(new { content });
    }
}
```

**Web Search:**
```csharp
public class WebSearchTool : IAgentTool
{
    private readonly ISearchProvider _searchProvider;

    public string Name => "web_search";

    public async Task<ToolResult> ExecuteAsync(
        ToolRequest request,
        CancellationToken cancellationToken)
    {
        var input = request.DeserializeInput<WebSearchInput>();
        var results = await _searchProvider.SearchAsync(input.Query, cancellationToken);
        return ToolResult.Success(results);
    }
}
```

For more examples, see:
- `extensions/web/BotNexus.Extensions.WebTools/`
- `extensions/tools/process/BotNexus.Extensions.ProcessTool/`
- `extensions/tools/exec/BotNexus.Extensions.ExecTool/`

---

## MCP Servers

Model Context Protocol (MCP) allows BotNexus to connect to external tools and services.

### Connecting an MCP Server

**1. Configure in agent's extensions:**

```json
{
  "agents": {
    "assistant": {
      "extensions": {
        "botnexus-mcp": {
          "toolPrefix": true,
          "servers": {
            "filesystem": {
              "command": "npx",
              "args": ["-y", "@modelcontextprotocol/server-filesystem", "/workspace"],
              "env": {
                "NODE_ENV": "production"
              },
              "inheritEnv": false,
              "workingDirectory": "/workspace",
              "initTimeoutMs": 30000,
              "callTimeoutMs": 60000
            }
          }
        }
      }
    }
  }
}
```

**2. BotNexus automatically:**
- Spawns the MCP server process
- Connects via stdio (standard input/output)
- Registers tools exposed by the server
- Prefixes tool names (e.g., `filesystem_read`)

### MCP Server Types

**1. Stdio Transport (Local Process):**

```json
{
  "filesystem": {
    "command": "npx",
    "args": ["-y", "@modelcontextprotocol/server-filesystem", "/workspace"],
    "inheritEnv": false
  }
}
```

**2. HTTP/SSE Transport (Remote Server):**

```json
{
  "github": {
    "url": "https://mcp.example.com/github",
    "headers": {
      "Authorization": "Bearer ${GITHUB_TOKEN}"
    }
  }
}
```

### Security Best Practices

**Always set `inheritEnv: false` for production:**

```json
{
  "servers": {
    "filesystem": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "/workspace"],
      "env": {
        "NODE_ENV": "production",
        "API_KEY": "${EXTERNAL_API_KEY}"
      },
      "inheritEnv": false  // ← Only explicitly configured env vars
    }
  }
}
```

**Why?**
- `inheritEnv: true` (default): MCP server inherits all parent environment variables, including secrets
- `inheritEnv: false`: Only env vars in `env` are passed to the subprocess

### Available MCP Servers

Popular MCP servers compatible with BotNexus:

| Server | Purpose | Package |
|--------|---------|---------|
| **Filesystem** | File operations | `@modelcontextprotocol/server-filesystem` |
| **GitHub** | GitHub API access | `@modelcontextprotocol/server-github` |
| **Git** | Git operations | `@modelcontextprotocol/server-git` |
| **PostgreSQL** | Database queries | `@modelcontextprotocol/server-postgres` |
| **Brave Search** | Web search | `@modelcontextprotocol/server-brave-search` |
| **Puppeteer** | Browser automation | `@modelcontextprotocol/server-puppeteer` |

**Installation:**
```bash
npm install -g @modelcontextprotocol/server-filesystem
npm install -g @modelcontextprotocol/server-github
```

### Tool Naming

With `toolPrefix: true`, tools are prefixed with server ID:

```text
filesystem → filesystem_read, filesystem_write
github → github_create_issue, github_search_repos
```

With `toolPrefix: false`, tools use original names (risk of conflicts):

```text
filesystem → read, write
github → create_issue, search_repos
```

---

## Skills System

Skills are markdown files containing domain knowledge, patterns, and guidelines that agents can load dynamically.

### What is a Skill?

A skill is a `.md` file in `~/.botnexus/skills/` that teaches an agent:
- Domain-specific knowledge
- Coding patterns and conventions
- Workflow procedures
- Best practices

**Example: `~/.botnexus/skills/git-workflow.md`**
```markdown
# Skill: Git Workflow

Use this workflow for all Git operations:

## Branching Strategy

- `main` — production-ready code
- `dev` — integration branch for features
- `feature/*` — feature branches
- `fix/*` — bug fix branches

## Commit Messages

Format: `type(scope): description`

Types:
- `feat`: New feature
- `fix`: Bug fix
- `docs`: Documentation
- `refactor`: Code restructuring
- `test`: Test changes
- `chore`: Build/tooling

Examples:
- `feat(auth): add OAuth2 provider`
- `fix(api): handle null response in GET /users`
- `docs(readme): update installation steps`

## Workflow

1. Create feature branch from `dev`:
   ```bash
   git checkout dev
   git pull origin dev
   git checkout -b feature/my-feature
   ```

2. Make changes and commit:
   ```bash
   git add .
   git commit -m "feat(module): add new capability"
   ```

3. Push and create PR:
   ```bash
   git push origin feature/my-feature
   # Open PR: feature/my-feature → dev
   ```

4. After merge, clean up:
   ```bash
   git checkout dev
   git pull origin dev
   git branch -d feature/my-feature
   ```
```

### Skill Discovery

Skills are stored in:
- **Project skills**: `~/.botnexus/skills/` (global)
- **Agent skills**: `~/.botnexus/agents/<agentId>/skills/` (agent-specific)

BotNexus scans these directories and makes skills available via the `skill` tool.

### Using Skills

Agents with the `skill` tool can load skills on demand:

**1. Agent invokes skill:**
```text
Agent: I need guidance on the git workflow.
[Uses skill tool to load "git-workflow"]
Agent: Based on the git-workflow skill, here's how to proceed...
```

**2. Auto-load skills:**
```json
{
  "agents": {
    "developer": {
      "extensions": {
        "botnexus-skills": {
          "enabled": true,
          "autoLoad": ["git-workflow", "coding-standards"],
          "maxLoadedSkills": 20
        }
      }
    }
  }
}
```

### Skill Configuration

Per-agent skill settings:

```json
{
  "extensions": {
    "botnexus-skills": {
      "enabled": true,
      "autoLoad": ["git-workflow", "coding-standards"],
      "disabled": ["deprecated-skill"],
      "allowed": null,
      "maxLoadedSkills": 20,
      "maxSkillContentChars": 100000
    }
  }
}
```

**Settings:**
- **`enabled`**: Enable/disable skills for this agent
- **`autoLoad`**: Skills loaded at agent startup
- **`disabled`**: Skills blocked from loading (blocklist)
- **`allowed`**: Allowed skills (`null` = all allowed)
- **`maxLoadedSkills`**: Max skills loaded simultaneously
- **`maxSkillContentChars`**: Max total skill content size

### Skill Structure

**Minimal skill:**
````markdown
# Skill: Tool Usage

Always use the `grep` tool for code search, not `read_file` + manual parsing.

Example:
```bash
# Good
grep --pattern "function.*calculate" --glob "**/*.js"

# Bad
read_file src/index.js → manually search for "calculate"
```
```

**Structured skill:**
````markdown
# Skill: API Design Patterns

## REST Conventions

### Resource Naming
- Use plural nouns: `/users`, `/orders`
- Avoid verbs: ❌ `/getUsers`, ✅ `/users`

### HTTP Methods
- `GET` — Retrieve resource(s)
- `POST` — Create resource
- `PUT` — Replace resource
- `PATCH` — Partial update
- `DELETE` — Remove resource

### Status Codes
- `200 OK` — Success
- `201 Created` — Resource created
- `400 Bad Request` — Invalid input
- `404 Not Found` — Resource not found
- `500 Internal Server Error` — Server error

## Examples

**Create User:**
```http
POST /users
Content-Type: application/json

{
  "name": "Alice",
  "email": "alice@example.com"
}

Response: 201 Created
{
  "id": 123,
  "name": "Alice",
  "email": "alice@example.com",
  "createdAt": "2025-06-12T10:00:00Z"
}
```

**Update User:**
```http
PATCH /users/123
Content-Type: application/json

{
  "name": "Alice Smith"
}

Response: 200 OK
{
  "id": 123,
  "name": "Alice Smith",
  "email": "alice@example.com",
  "updatedAt": "2025-06-12T10:05:00Z"
}
```
````

### Skill Best Practices

**1. Keep skills focused:**
- One skill per topic
- Avoid mixing unrelated content

**2. Use examples:**
- Show "good" vs "bad" patterns
- Include code snippets

**3. Be concise:**
- Agents have token limits
- Prefer tables and lists over prose

**4. Version skills:**
- Add dates or version numbers
- Update as patterns evolve

**5. Test with agents:**
- Verify agents understand and apply the skill
- Iterate based on agent behavior

---

## Extension Deployment

### Local Development

**1. Build extension:**
```bash
dotnet build -c Release
```

**2. Copy to extensions directory:**
```bash
mkdir -p ~/.botnexus/extensions/my-extension
cp bin/Release/net10.0/*.dll ~/.botnexus/extensions/my-extension/
cp botnexus-extension.json ~/.botnexus/extensions/my-extension/
```

**3. Restart Gateway or wait for hot reload:**
```bash
# Option 1: Restart
dotnet run --project src/gateway/BotNexus.Gateway.Api

# Option 2: Hot reload (if supported)
# BotNexus automatically detects new extensions
```

### Production Deployment

**1. Package as NuGet:**
```bash
dotnet pack -c Release
```

**2. Publish to private NuGet feed:**
```bash
dotnet nuget push bin/Release/BotNexus.Extensions.MyTool.1.0.0.nupkg \
  -s https://nuget.example.com/v3/index.json \
  -k your-api-key
```

**3. Install on target system:**
```bash
dotnet add package BotNexus.Extensions.MyTool --version 1.0.0
```

### Extension Discovery

BotNexus discovers extensions from:

1. **`~/.botnexus/extensions/`** (default)
2. **Custom path** via `gateway.extensions.path` in config

**Directory structure:**
```text
~/.botnexus/extensions/
├── my-tool/
│   ├── botnexus-extension.json
│   ├── BotNexus.Extensions.MyTool.dll
│   └── Dependencies.dll
├── another-tool/
│   ├── botnexus-extension.json
│   └── BotNexus.Extensions.AnotherTool.dll
└── skills/
    ├── git-workflow.md
    └── api-design.md
```

---

## Advanced Topics

### Stateful Tools

Tools can maintain state across invocations:

```csharp
public class DatabaseTool : IAgentTool
{
    private readonly Dictionary<string, string> _cache = new();

    public async Task<ToolResult> ExecuteAsync(
        ToolRequest request,
        CancellationToken cancellationToken)
    {
        var input = request.DeserializeInput<DbInput>();

        // Check cache
        if (_cache.TryGetValue(input.Key, out var value))
        {
            return ToolResult.Success(new { value, cached = true });
        }

        // Fetch from database
        value = await FetchFromDatabaseAsync(input.Key);
        _cache[input.Key] = value;

        return ToolResult.Success(new { value, cached = false });
    }
}
```

### Dependency Injection

Tools can use dependency injection:

```csharp
public class HttpTool : IAgentTool
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpTool> _logger;

    public HttpTool(
        IHttpClientFactory httpClientFactory,
        ILogger<HttpTool> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<ToolResult> ExecuteAsync(
        ToolRequest request,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        // ... use client
    }
}
```

Register services in your extension's startup:

```csharp
public class MyExtensionStartup : IExtensionStartup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddHttpClient();
        services.AddSingleton<IAgentTool, HttpTool>();
    }
}
```

### Tool Approval Workflow

For dangerous tools, implement approval:

```csharp
public async Task<ToolResult> ExecuteAsync(
    ToolRequest request,
    CancellationToken cancellationToken)
{
    var input = request.DeserializeInput<ShellInput>();

    // Check if approval is required
    if (RequiresApproval(input.Command))
    {
        // Request approval from user
        var approved = await RequestApprovalAsync(input.Command);
        if (!approved)
        {
            return ToolResult.Failure("User denied approval");
        }
    }

    // Execute command
    var result = await ExecuteShellCommandAsync(input.Command);
    return ToolResult.Success(result);
}
```

---

## Troubleshooting

### Extension Not Loading

**Check:**
1. `botnexus-extension.json` exists in extension directory
2. Assembly targets .NET 10.0
3. Assembly name matches `assembly` field in manifest
4. Extension directory is in `~/.botnexus/extensions/`

**Debug:**
```bash
# List extension directories
ls -la ~/.botnexus/extensions/

# Check Gateway logs
tail -f ~/.botnexus/logs/gateway.log | grep -i extension
```

### Tool Not Available to Agent

**Check:**
1. Tool ID in agent's `toolIds` array
2. Extension assembly loaded successfully
3. Tool implements `IAgentTool` interface

**Verify:**
```bash
curl http://localhost:5005/api/tools
```

### MCP Server Won't Start

**Check:**
1. `command` is executable and in PATH
2. `args` are correct
3. `workingDirectory` exists and is accessible
4. Environment variables are set (if needed)

**Debug:**
```bash
# Test MCP server manually
npx -y @modelcontextprotocol/server-filesystem /workspace
```

### Skill Not Loading

**Check:**
1. Skill file exists in `~/.botnexus/skills/` or agent's `skills/` directory
2. Skill name matches filename (without `.md`)
3. Skill not in agent's `disabled` list
4. `botnexus-skills` extension enabled for agent

**Verify:**
```bash
ls -la ~/.botnexus/skills/
ls -la ~/.botnexus/agents/<agentId>/skills/
```

---

## Next Steps

- **[Configuration Reference](configuration.md)** — Complete extension config options
- **[Agent Setup](agents.md)** — Assign tools to agents
- **[Architecture](../architecture/overview.md)** — Extension loading and lifecycle

For extension examples, explore:
- `extensions/mcp/` — MCP bridge implementation
- `extensions/skills/` — Skills system
- `extensions/web/` — Web search and fetch tools
- `extensions/tools/` — Process and exec tools
