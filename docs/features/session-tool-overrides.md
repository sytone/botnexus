# Per-Session Tool Overrides

A conversation can narrow the set of tools its agent may use, without editing the agent's
configuration. The restriction is persisted on the conversation row, so it survives a reconnect, a
new session in the same conversation, and a gateway restart.

Issue: [#2523](https://github.com/Sytone/botnexus/issues/2523).

## Why

Tool availability in BotNexus is otherwise fixed at the **agent** level. Before this feature the
only ways to run a turn without `exec` or `shell` were to reconfigure the agent (a global, durable
change affecting every conversation) or to spawn a sub-agent with a restricted `toolIds` list. Both
are heavyweight for what is usually a temporary concern:

- **Blast-radius reduction for a risky turn.** Drop the write-capable tools for the duration of one
  investigation and put them back afterwards.
- **Untrusted-input sessions.** A conversation about to ingest untrusted GitHub or channel content
  can drop write-capable tools first, which mitigates the prompt-injection paths directly rather
  than relying on the model's judgement.
- **Reproducing a tool-specific bug** without editing `config.json` and restarting the gateway.

This is an **availability** axis. It is distinct from the per-agent `toolPolicy` block, which is an
**approval** axis (risk level, `neverApprove`, `askFallback`). A tool removed by this overlay is
never offered to the model at all, so there is nothing left to approve.

## Narrowing-only — the security property

**The overlay can only remove tools from the agent's configured set. It can never add one.**

This is the whole point of the design. If a conversation-scoped setting could grant a tool, then
anyone able to write a conversation override could give themselves `exec` on an agent that was
deliberately configured without it, and the feature would be a privilege-escalation seam rather
than a blast-radius control.

The guarantee is structural, not advisory: the resolver filters the agent's assembled tool list and
never appends to it, so a request to enable a tool the agent does not have is **refused** and
logged. This holds even for an overlay written directly into the database by hand — enforcement
happens at resolution time, not at the point the value is stored.

To genuinely grant a tool, edit the agent's own `toolIds`. That path carries the authority the
change requires.

Two secondary rules follow from the same principle:

- **Deny beats allow.** A tool named in both lists is dropped. A control that exists to reduce
  blast radius must never resolve a contradiction by granting.
- **An empty result stays empty.** Narrowing to a set of tools the agent does not have yields *no*
  tools, not the full set. A "fall back to everything" behaviour would turn a refused widening into
  a total grant.

**Runtime-pinned tools survive the overlay.** Tools required for basic agent function (`ask_user`,
`conversation`, `session`, the memory tools, `cron`, `canvas`) are exempt from removal, matching the
existing deny-list behaviour, so an overlay cannot leave an agent unable to respond at all. Pinning
exempts a tool from being *dropped*; it never grants one the agent lacks.

## Using it

The overlay is driven from the existing slash-command surface:

| Command | Effect |
| --- | --- |
| `/tools` | Show the current overlay for this conversation. |
| `/tools disable exec shell` | Drop those tools for this conversation. |
| `/tools only read grep` | Narrow this conversation to that subset. |
| `/tools clear` | Remove the overlay; restore the agent's full configured set. |

`drop` is accepted as a synonym for `disable`, and `enable` for `only`. `off`, `default` and `agent`
work as synonyms for `clear`.

Changes take effect on the **next turn**, because the tool list is assembled when the agent is
constructed for a turn.

Requesting a tool the agent does not have is not an error at the command level — the name is simply
refused when the turn resolves, and the refusal is logged at warning level naming the tools that
were dropped and why.

## Persistence

The overlay is stored on the conversation row in the `tool_override_json` column as a small JSON
document:

```json
{
  "enabledTools": ["read", "grep"],
  "disabledTools": ["exec"]
}
```

Both fields are optional; `null` or absent means "no opinion". A conversation with no overlay
behaves exactly as it did before this feature existed, which is also how every row persisted before
the column was added hydrates.

Writes go through the narrow `PatchOverrideAsync` path shared with the model and thinking
overrides, so setting a tool overlay cannot clobber a concurrently committed model override (and
vice versa).

A corrupt or unparseable overlay degrades to "no overlay" rather than throwing on the
agent-construction path. This fails **open** by design, and safely so: the overlay narrows *from*
the agent's configured tool set, which remains the actual security boundary. Failing open here can
only ever restore the agent's normal, already-authorised tools — it can never widen beyond them.

## Where it is applied

The overlay is applied at the very end of tool assembly, after workspace tools, registry tools,
memory tools, tool providers and all extension contributors have added theirs. Applying it earlier
would miss anything appended afterwards, which is exactly the hole that would leave `exec` reachable
in a conversation that asked to disable it.

## Not yet covered

The portal does not currently expose the overlay; it is driven from the slash-command surface only.
That surface is tracked separately as
[#3271](https://github.com/Sytone/botnexus/issues/3271).
