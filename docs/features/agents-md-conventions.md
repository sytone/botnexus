# AGENTS.md Conventions

BotNexus follows the emerging `AGENTS.md` convention used by Claude Code and similar
agentic tooling: repository maintainers place `AGENTS.md` files at the repository root and
in subdirectories to describe conventions an agent should respect when working in that tree
(build commands, coding standards, "don't touch these files", PR workflow, etc.).

## How agents load them

Discovery is **pull-based**. Rather than eagerly injecting every discoverable `AGENTS.md`
into the system prompt on every turn — which, across all the directories an agent is granted
access to, could embed hundreds of files and exhaust the context window — the agent loads them
on demand with the `get_agent_files` tool.

A lightweight, always-on nudge in the system prompt reminds the agent that these files may
exist and that it should call `get_agent_files` for the path it is working in before creating
or editing files there. The nudge costs a single line of prompt; the file contents are only
pulled when actually needed.

### The `get_agent_files` tool

| | |
|---|---|
| **Name** | `get_agent_files` |
| **Parameter** | `path` — a directory or file inside the tree you are working in |
| **Returns** | The `AGENTS.md` files that apply to `path`, most-general first |

When called, the tool:

1. Resolves `path` (a file path is reduced to its containing directory).
2. Walks upward from that directory through its parents, **stopping at the nearest git
   repository root** (the directory containing a `.git` entry). It never escapes the repo
   boundary.
3. Collects each `AGENTS.md` found along the chain and returns them **root-first**
   (most general → most specific).

## Trust and access control

The tool is gated by the agent's `FileAccessPolicy`: the requested `path` must be readable by
the agent, otherwise the call is refused. Because only directories the agent owner has
explicitly granted are readable, an arbitrary untrusted repository is never read as
prompt-injection surface — the grant **is** the trust boundary.

Each returned file is also defensively size-capped so a single pathological `AGENTS.md`
cannot itself blow the context budget; oversized files are truncated with a marker.

## Relationship to the workspace `AGENTS.md`

This is distinct from the auto-generated `{workspace}/AGENTS.md` file (a list of configured
agents loaded into every system prompt — see
[Workspace and Memory](../development/workspace-and-memory.md)). Repository-root `AGENTS.md`
convention files are external to the agent workspace and are loaded on demand via the tool
described here.
