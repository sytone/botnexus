---
name: botnexus-github-powershell
description: "Critical PowerShell/Windows-specific rules for using `gh` CLI on BotNexus agents. Load this in addition to the `github` skill whenever running on Windows with pwsh. Covers: backtick escaping failures, grep not found, exec array form, body-file pattern, auth switching between sytone/jobullen_microsoft. Triggers on: any gh CLI work on BotNexus, 'gh issue', 'gh pr', PowerShell gh errors, 'not recognized' errors."
metadata:
  domain: workflow
  platform: windows-pwsh
  repo: sytone/botnexus
---
# BotNexus GitHub CLI — Windows/PowerShell Rules

This skill supplements the `github` skill with Windows+PowerShell-specific rules learned from real agent failures on BotNexus.

---

## Auth — Always Check, Always Switch

Jon has **two GitHub accounts**. Other agents on the same machine may switch auth at any time.

```powershell
# ALWAYS do this before any gh work
gh auth status
gh auth switch --user sytone   # switch if not active
gh auth status                 # confirm
```

The active account **must be `sytone`** for BotNexus work. Never assume it's set correctly.

---

## Use `exec` Array Form, Not `shell`

The `shell` tool uses PowerShell. PowerShell eats backticks, dollar signs, and parentheses.

**Use `exec` with an array for all `gh` commands:**

```json
exec(["gh", "issue", "create",
      "--repo", "sytone/botnexus",
      "--title", "bug: something broke",
      "--body-file", "C:\\Users\\jobullen\\.botnexus\\agents\\farnsworth\\workspace\\body.md",
      "--label", "bug"])
```

**Never do this in `shell`:**
```powershell
# ❌ FAILS — backtick is PowerShell escape char
gh issue create --title "has `code` in it"

# ❌ FAILS — comma inside quotes breaks arg parsing in some shells  
gh issue list --search "tool, conversation"

# ❌ FAILS — $var gets interpolated
gh issue create --title "Issue for $agentId"
```

---

## Body Content — Always `--body-file`

**Never pass multi-line or markdown body text inline.** Write to workspace file first.

```python
# Step 1: Write to workspace
write("issue-body.md", "## Summary\n\nContent here...")

# Step 2: Pass absolute path to gh
exec(["gh", "issue", "create",
      "--repo", "sytone/botnexus",
      "--title", "My issue",
      "--body-file", "C:\\Users\\jobullen\\.botnexus\\agents\\farnsworth\\workspace\\issue-body.md"])
```

**Workspace absolute path:** `C:\Users\jobullen\.botnexus\agents\farnsworth\workspace\`

This applies to:
- `gh issue create --body-file`
- `gh issue edit --body-file`
- `gh issue comment --body-file`
- `gh pr create --body-file`

---

## Commands That Don't Exist in PowerShell

These Unix commands fail in pwsh `shell` tool — use alternatives:

| ❌ Don't use | ✅ Use instead |
|---|---|
| `grep pattern file` | `exec` + `grep` tool, or `Select-String` |
| `head -n 5 file` | `Get-Content file \| Select-Object -First 5` |
| `tail -n 5 file` | `Get-Content file \| Select-Object -Last 5` |
| `cat file` | `Get-Content file -Raw` |
| `ls -la` | `Get-ChildItem` |

For file searching, use the `grep` tool directly (not via shell).

---

## PowerShell Patterns That Break

```powershell
# ❌ Backtick in -match/-like breaks parsing
Where-Object { $_.Name -match `tool` }
# ✅ Use quotes
Where-Object { $_.Name -match 'tool' }

# ❌ Backtick in string concat
$f = `$env:TEMP\file.md`
# ✅ Use proper path join
$f = Join-Path $env:TEMP 'file.md'

# ❌ Here-strings in exec/shell
@'
content
'@ | gh issue comment 1 --body-file -
# ✅ write() then --body-file

# ❌ Chained && in exec array
exec(["pwsh", "-c", "cd Q:\\repos && gh ..."])
# ✅ Split into separate exec calls, or use -C flag for git
```

---

## Standard Issue/PR Workflow for BotNexus

```python
# 1. Auth check
exec(["gh", "auth", "switch", "--user", "sytone"])

# 2. Write body
write("issue.md", issue_content)

# 3. Create issue
exec(["gh", "issue", "create",
      "--repo", "sytone/botnexus",
      "--title", "[Area] Short description",
      "--body-file", "C:\\Users\\jobullen\\.botnexus\\agents\\farnsworth\\workspace\\issue.md",
      "--label", "bug"])  # or "enhancement"

# 4. Edit issue (add labels, title changes)
exec(["gh", "issue", "edit", "123",
      "--repo", "sytone/botnexus",
      "--add-label", "squad"])

# 5. Add comment
write("comment.md", comment_content)
exec(["gh", "issue", "comment", "123",
      "--repo", "sytone/botnexus",
      "--body-file", "C:\\Users\\jobullen\\.botnexus\\agents\\farnsworth\\workspace\\comment.md"])

# 6. Close issue
exec(["gh", "issue", "close", "123", "--repo", "sytone/botnexus"])
```

---

## List / Search Issues

```python
# List open issues (JSON)
exec(["gh", "issue", "list",
      "--repo", "sytone/botnexus",
      "--state", "open",
      "-L", "100",
      "--json", "number,title,labels,state,createdAt"])

# Search by keyword
exec(["gh", "issue", "list",
      "--repo", "sytone/botnexus",
      "--search", "conversation",
      "-L", "20",
      "--json", "number,title,state"])

# Filter by label
exec(["gh", "issue", "list",
      "--repo", "sytone/botnexus",
      "--label", "bug",
      "-L", "50"])
```

---

## Git Workflow (exec array form)

```python
# Sync main
exec(["git", "-C", "Q:\\repos\\botnexus", "checkout", "main"])
exec(["git", "-C", "Q:\\repos\\botnexus", "pull", "--ff-only"])

# Create worktree
exec(["git", "-C", "Q:\\repos\\botnexus", "worktree", "add",
      "Q:\\repos\\botnexus-wt\\feat-my-branch", "-b", "feat/my-branch"])

# Push from worktree
exec(["git", "-C", "Q:\\repos\\botnexus-wt\\feat-my-branch",
      "push", "-u", "origin", "feat/my-branch"])

# Create PR
write("pr-body.md", pr_content)
exec(["gh", "pr", "create",
      "--repo", "sytone/botnexus",
      "--head", "feat/my-branch",
      "--title", "feat: my change",
      "--body-file", "C:\\Users\\jobullen\\.botnexus\\agents\\farnsworth\\workspace\\pr-body.md"])

# Cleanup after merge
exec(["git", "-C", "Q:\\repos\\botnexus", "worktree", "remove",
      "Q:\\repos\\botnexus-wt\\feat-my-branch"])
exec(["git", "-C", "Q:\\repos\\botnexus", "branch", "-d", "feat/my-branch"])
```

---

## Observed Failure Patterns (Real)

These were seen in agent sessions — don't repeat them:

| Session | Failure | Root cause |
|---|---|---|
| 2026-05-13 | `--body "has backtick"` in shell | backtick = PS escape |
| 2026-05-13 | `grep` not found | not a pwsh command |
| 2026-05-13 | `head` not found | not a pwsh command |
| 2026-05-14 | `-match \`tool\`` parse error | backtick in -match |
| 2026-05-14 | `-like \`*onver` parse error | backtick in -like |
| 2026-05-14 | `$env:TEMP\file\n$body` error | newline in variable path |
| 2026-05-14 | `--add-body` flag unknown | flag doesn't exist; use `--body-file` |
| 2026-05-14 | `--body` with comma in text | arg parsing failure |
| 2026-05-14 | Shell `--limit 20` as quoted string | flags must be separate args |
