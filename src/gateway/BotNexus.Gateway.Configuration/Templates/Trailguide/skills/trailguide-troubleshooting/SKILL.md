---
name: trailguide-troubleshooting
description: Diagnose BotNexus failures from exact evidence, current troubleshooting docs, doctor output, logs, and privacy-bounded provider request capture.
---

# Trailguide troubleshooting

Read `DOCUMENTATION_ROOT.md` first and use that one shared resolver result. If unresolved, report that remediation cannot be grounded on this installation; gather evidence but do not invent a cause or fix.

Obtain the exact error and read `docs/user-guide/troubleshooting.md` before diagnosing. Route symptoms to its current sections:

| Symptom | Documentation section | Deterministic evidence |
| --- | --- | --- |
| Build failure | Build Failures | exact build/restore output |
| Gateway will not start | Gateway Startup Issues | `botnexus doctor`, then status and logs described by the docs |
| Agent not responding | Agent Not Responding | `botnexus doctor`, agent configuration, and logs |
| Provider authentication | Provider Authentication Errors | exact provider error and configured provider; use bounded request capture below only with consent |
| Web UI connectivity | WebUI Connection Issues | `botnexus doctor locations`, health/status output, browser error |
| Tool execution failure | Tool Execution Failures | exact tool result, agent tool grant/configuration, and logs |

Current documented focused diagnostics are `botnexus doctor config`, `botnexus doctor locations`, and `botnexus doctor agents`. Do not invent other doctor subcommands. Label a cause **verified** only when evidence observed this session proves it, and state that evidence. Otherwise label it **candidate**. Read remediation from current docs; if no remedy covers the evidence, say so.

## Privacy-bounded provider request capture

Use only when `docs/observability.md` still supports the procedure and the user confirms the state-changing diagnostic. Warn that request logs contain system prompts, tool schemas, user content, and message history. Credential-bearing headers and recognised secrets are redacted, but conversation text is not. Keep capture short-lived:

```text
botnexus config set gateway.enableProviderRequestLogging true
botnexus config set gateway.logLevel Debug
# reproduce once and inspect the gateway log
botnexus config set gateway.enableProviderRequestLogging false
botnexus config set gateway.logLevel Information
```

Both settings are required and current docs say they apply without restart. Always revert both. Streaming response bodies are not captured, so never promise the model's reply text; only request content plus response status, headers, and timing are available for streams.

Never perform destructive or state-changing remediation without explicit confirmation. Do not update, restart, rebuild, delete configuration, reset stores, or kill processes unprompted.
