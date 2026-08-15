# Automation and Scripting

BotNexus agents are usually driven interactively — through the web portal, a chat channel, or the
scheduler. `botnexus agent exec` is the fourth route: a **headless, one-shot agent run** you can put
in a shell script, a Makefile, a git hook, or a CI job.

```bash
botnexus agent exec farnsworth "summarise the last 10 commits on main"
```

The agent's answer goes to **stdout**. Everything else — progress notes, warnings, errors — goes to
**stderr**, so the command composes:

```bash
botnexus agent exec farnsworth "list the failing tests" > report.txt
botnexus agent exec farnsworth "..." --json | jq -r '.text'
```

## When to use which entry point

| You want to… | Use |
| --- | --- |
| Run an ad-hoc task once, from a shell | `agent exec` |
| Run a *saved template* with parameters | `prompt run` |
| Run on a schedule | `cron` (a registered job) |
| Hold a conversation | the portal or a chat channel |

## Options

| Option | Default | Purpose |
| --- | --- | --- |
| `--json` | off | Emit a structured result on stdout instead of plain text. |
| `--timeout <seconds>` | `300` | Wall-clock budget. The run is abandoned when it elapses. |
| `--model <id>` | agent default | Per-run model override — `model-id` or `provider/model-id`. |
| `--thinking <level>` | agent default | `minimal`, `low`, `medium`, `high`, `xhigh`, or `max`. |
| `--conversation <id>` | fresh session | Run inside an existing session instead of a new one. |
| `--url <url>` | local gateway | Target a different gateway. |
| `--token <value>` | — | Credential for that gateway. **Required** for any non-loopback `--url`. |

A gateway must be running: `agent exec` submits the run to it rather than executing the agent inside
the CLI process. See [Approval posture](#approval-posture-and-tool-policy) for why.

## Exit codes

The command is designed to be branched on, so the failure modes are distinguishable rather than all
collapsing into `1`:

| Code | Meaning |
| --- | --- |
| `0` | The run completed and every tool call succeeded. |
| `1` | Usage error, unreachable gateway, refused credential, or an unclassified gateway error. |
| `2` | The named agent is not registered. |
| `3` | The run exceeded `--timeout`. |
| `4` | The run completed, but at least one tool call reported an error. |

```bash
if ! botnexus agent exec farnsworth "run the health check"; then
  case $? in
    2) echo "agent is not registered on this gateway" ;;
    3) echo "the run timed out"                        ;;
    4) echo "a tool failed — see stderr"               ;;
    *) echo "the run could not be started"             ;;
  esac
fi
```

Note that exit code `4` still prints the agent's answer on stdout. A failed tool call does not mean
the turn produced nothing useful — it means you should not treat the result as fully trustworthy.

## The `--json` document

```json
{
  "sessionId": "9f2c1b7e4a5d4f0e8c3b6a1d2e5f7089",
  "agentId": "farnsworth",
  "text": "The last 10 commits are …",
  "toolCalls": [
    { "toolCallId": "tc_01", "toolName": "exec", "isError": false }
  ],
  "usage": { "inputTokens": 8421, "outputTokens": 512, "cacheRead": 6000, "cacheWrite": 0 },
  "exitCode": 0
}
```

`sessionId` is the handle for follow-up work — pass it back as `--conversation` to continue in the
same context. `toolCalls` records what the turn actually did, which is how you distinguish a turn
that performed work from one that answered from context. Tool *arguments* and *results* are
deliberately omitted; read the transcript store if you need them.

## Approval posture and tool policy

`agent exec` submits the run to the gateway over the same REST endpoint every other non-streaming
caller uses. It therefore inherits the gateway's tool policy and approval behaviour **unchanged**:

- A tool that requires approval still requires approval when invoked from a headless run.
- There is deliberately **no** `--yes`, `--auto-approve`, or `--force` flag. The CLI has no authority
  to waive a policy decision that is made inside the gateway, and a headless entry point that could
  waive it would be an approval bypass by construction.
- The credential rule is the same one every gateway-facing command follows: the credential configured
  for your local gateway is never sent to a host you named on the command line. Targeting a remote
  `--url` without `--token` is **refused**, not sent unauthenticated.

If a run stalls waiting on an approval that no human is present to grant, it will hit `--timeout` and
exit `3`. That is the intended behaviour: the run fails visibly rather than proceeding unapproved.

## Recipes

**Gate a commit on an agent review**

```bash
#!/usr/bin/env bash
set -euo pipefail
review=$(botnexus agent exec farnsworth "review the staged diff; reply APPROVE or REJECT with reasons")
echo "$review"
grep -q '^APPROVE' <<<"$review"
```

**Continue a multi-step task across invocations**

```bash
session=$(botnexus agent exec farnsworth "start the migration audit" --json | jq -r '.sessionId')
botnexus agent exec farnsworth "now list the blockers you found" --conversation "$session"
```

**Give a hard budget to an expensive task**

```bash
botnexus agent exec farnsworth "profile the slow query" --timeout 900 --thinking high
```

## Troubleshooting

| Symptom | Cause |
| --- | --- |
| Exit `1`, "unable to reach the gateway" | The gateway is not running, or `--url` points elsewhere. Check `botnexus gateway status`. |
| Exit `1`, "Refusing to contact …" | A non-loopback `--url` with no `--token`. Supply the credential for *that* gateway. |
| Exit `2` | Typo in the agent id, or the agent is not in this gateway's `config.json`. `botnexus agent list` shows what is registered. |
| Exit `3` on every run | The default 300s budget is too small for the task. Raise `--timeout`. |
| Empty stdout, exit `0` | The agent genuinely produced no text — often a turn that only performed tool work. Re-run with `--json` to see `toolCalls`. |
