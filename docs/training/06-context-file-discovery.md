# Context File Discovery

Project context files — README, copilot-instructions, and docs — help the LLM understand the codebase before writing code. BotNexus discovers and injects these automatically into the system prompt. This document explains the discovery process, prioritization order, and token budget management.

## Overview

When you create a coding agent, the `SystemPromptBuilder` asks: *"What files in this project should the agent know about?"* The answer comes from `ContextFileDiscovery` — an automatic, non-interactive process that scans the working directory, extracts relevant files, and fits them into a token budget.

```
Working Directory
        │
        ├── .github/copilot-instructions.md  ◄─── Priority 1 (highest)
        ├── README.md                          ◄─── Priority 2
        └── docs/
             ├── *.md (up to 5 files)           ◄─── Priority 3+
             └── other files (ignored)
              ↓
        ContextFileDiscovery.DiscoverAsync()
              ↓
        [Scan for files, fit to budget, truncate if needed]
              ↓
        IReadOnlyList<PromptContextFile>
              ↓
        SystemPromptBuilder includes as context
              ↓
        Agent system prompt sent to LLM
```

## Discovery process

### Step 1: Validation

```csharp
public static async Task<IReadOnlyList<PromptContextFile>> DiscoverAsync(
    string workingDirectory,
    CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(workingDirectory))
        throw new ArgumentException("Working directory cannot be empty.", nameof(workingDirectory));

    var root = Path.GetFullPath(workingDirectory);
    if (!Directory.Exists(root))
        return [];
    
    // ... continue with discovery
}
```

**Validation checks:**
- Working directory must not be null or whitespace
- Working directory must exist on disk (returns empty list if not)
- Non-existent files are skipped silently during iteration

### Step 2: Build prioritized file list

The discovery process looks for files in this order:

1. `.github/copilot-instructions.md` — **Highest priority.** User-authored project-specific guidance.
2. `README.md` — **High priority.** Foundational project documentation.
3. `docs/*.md` — **Medium priority.** Up to 5 markdown files from the `docs/` directory, sorted alphabetically by filename.

```csharp
var prioritizedFiles = new List<string>
{
    Path.Combine(root, ".github", "copilot-instructions.md"),
    Path.Combine(root, "README.md")
};

var docsDirectory = Path.Combine(root, "docs");
if (Directory.Exists(docsDirectory))
{
    prioritizedFiles.AddRange(
        Directory.EnumerateFiles(docsDirectory, "*.md", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(MaxDocsFiles));  // MaxDocsFiles = 5
}
```

**Key points:**
- Only `.md` files from `docs/` are considered (no subdirectories)
- Files are sorted alphabetically, then limited to 5
- If a file doesn't exist, it's skipped (no error)
- If a file is empty or whitespace-only, it's skipped

### Step 3: Fit to token budget

Each file is processed in priority order. A global **context budget** (16 KB by default) controls the total size:

```csharp
private const int ContextBudgetBytes = 16 * 1024;  // 16,384 bytes

var remainingBudget = ContextBudgetBytes;

foreach (var filePath in prioritizedFiles)
{
    ct.ThrowIfCancellationRequested();
    
    if (remainingBudget <= 0 || !File.Exists(filePath))
        continue;

    var content = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
    if (string.IsNullOrWhiteSpace(content))
        continue;

    var includedContent = FitContentToBudget(content, remainingBudget);
    if (string.IsNullOrEmpty(includedContent))
        break;  // Budget exhausted

    discovered.Add(new PromptContextFile(
        PathUtils.GetRelativePath(filePath, root),
        includedContent));
    
    remainingBudget -= Encoding.UTF8.GetByteCount(includedContent);
}
```

**Budget calculation:**
- Each file's byte count is calculated using `Encoding.UTF8.GetByteCount()`
- When a file doesn't fit completely, it's truncated and a `[truncated]` marker is appended
- Discovery stops when the budget is exhausted (no more files are added)

### Step 4: Truncation logic

If a file exceeds the remaining budget, it's truncated at character boundaries:

```csharp
private static string FitContentToBudget(string content, int maxBytes)
{
    if (maxBytes <= 0)
        return string.Empty;

    var fullSize = Encoding.UTF8.GetByteCount(content);
    if (fullSize <= maxBytes)
        return content;

    var markerBytes = Encoding.UTF8.GetByteCount(TruncatedMarker);
    if (maxBytes <= markerBytes)
        return TruncatedMarker[..Math.Min(TruncatedMarker.Length, maxBytes)];

    // Character-by-character iteration to find the longest prefix that fits
    var allowedBytes = maxBytes - markerBytes;
    var builder = new StringBuilder(content.Length);
    var usedBytes = 0;
    foreach (var ch in content)
    {
        var charBytes = Encoding.UTF8.GetByteCount(ch.ToString());
        if (usedBytes + charBytes > allowedBytes)
            break;

        builder.Append(ch);
        usedBytes += charBytes;
    }

    builder.Append(TruncatedMarker);
    return builder.ToString();
}
```

**Algorithm:**
- Iterates character-by-character, accumulating UTF-8 byte cost
- Reserves space for the `[truncated]` marker (11 bytes)
- Stops as soon as adding the next character would exceed the budget
- Always truncates at character boundaries (never mid-UTF8-sequence)
- Edge case: if the budget is smaller than the marker itself, the marker is truncated to fit

## Return value

Discovery returns a list of `PromptContextFile` records:

```csharp
public record PromptContextFile(
    string RelativePath,  // e.g., ".github/copilot-instructions.md"
    string Content        // Truncated to budget if needed
);
```

Example output:
```
[
    new PromptContextFile(".github/copilot-instructions.md", "..."),
    new PromptContextFile("README.md", "..."),
    new PromptContextFile("docs/architecture.md", "..."),
    new PromptContextFile("docs/deployment.md", "[truncated]")
]
```

## Integration with SystemPromptBuilder

The `SystemPromptBuilder` includes discovered context files in the system prompt:

```csharp
var contextFiles = await ContextFileDiscovery.DiscoverAsync(workingDirectory, ct);

var sb = new StringBuilder(basePrompt);
foreach (var file in contextFiles)
{
    sb.AppendLine($"\n# {file.RelativePath}\n```\n{file.Content}\n```");
}

return sb.ToString();
```

Files appear in the system prompt in discovery order, with their relative paths as section headers.

## Configuration and budget tuning

The budget and file limits are hardcoded constants:

```csharp
private const int ContextBudgetBytes = 16 * 1024;  // Total budget
private const int MaxDocsFiles = 5;                 // Max from docs/
private const string TruncatedMarker = "[truncated]";
```

To customize these limits, you would need to fork `ContextFileDiscovery` or refactor it to accept configuration parameters. This is a potential future improvement.

## Example walkthrough

**Directory structure:**
```
project/
├── .github/
│   └── copilot-instructions.md  (2 KB)
├── README.md                      (3 KB)
├── docs/
│   ├── architecture.md            (8 KB)
│   ├── api.md                     (5 KB)
│   ├── deployment.md              (4 KB)
│   └── other.md                   (6 KB)
```

**Discovery process (16 KB budget):**

1. **copilot-instructions.md** (2 KB) → Include fully. Budget: 14 KB remaining.
2. **README.md** (3 KB) → Include fully. Budget: 11 KB remaining.
3. **api.md** (5 KB) → Include fully. Budget: 6 KB remaining.
4. **architecture.md** (8 KB) → Truncate to 6 KB + `[truncated]`. Budget: 0 KB remaining.
5. **deployment.md** — Skipped (budget exhausted).
6. **other.md** — Skipped (only 5 files max anyway).

**Result:**
```
[
    PromptContextFile(".github/copilot-instructions.md", "...full..."),
    PromptContextFile("README.md", "...full..."),
    PromptContextFile("docs/api.md", "...full..."),
    PromptContextFile("docs/architecture.md", "...truncated...[truncated]")
]
```

## Best practices for project documentation

To maximize the value of context file discovery:

1. **Create `.github/copilot-instructions.md`** — Include project-specific guidance, coding standards, and patterns the agent should follow.
2. **Keep README.md up-to-date** — This is always included. Use it for installation, usage, and high-level architecture.
3. **Organize docs/ alphabetically** — Files are sorted alphabetically, so name them to appear in useful order (e.g., `01-architecture.md`, `02-setup.md`).
4. **Prioritize early docs** — The first 5 docs files are scanned. Put the most important documentation first.
5. **Use clear, concise markdown** — Truncation can happen mid-sentence. Write self-contained sections that don't depend on continuation.

## Related documentation

- **[Coding Agent — System Prompt](03-coding-agent.md#system-prompt-construction)** — How discovered files are integrated into the full system prompt
- **[SystemPromptBuilder.cs](../src/coding-agent/BotNexus.CodingAgent/SystemPromptBuilder.cs)** — Source implementation
- **[ContextFileDiscovery.cs](../src/coding-agent/BotNexus.CodingAgent/Utils/ContextFileDiscovery.cs)** — Source implementation
