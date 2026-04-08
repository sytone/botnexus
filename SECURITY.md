# Security Policy

## Scope

BotNexus is a **locally-hosted** AI agent execution platform. It is designed and intended to run on a private local network or developer workstation — **not** exposed to the public internet. The security model reflects this.

## Design Decisions

| Area | Decision | Rationale |
|---|---|---|
| API authentication | Optional (off by default) | Local-first tool; auth is for multi-user or networked deployments |
| Swagger/OpenAPI UI | Development environment only | Docs not needed in production; excluded from production builds |
| CORS | `AllowAnyOrigin` in Development, restricted in Production | Enables local browser dev; production requires explicit allowed origins |
| Rate limiting | Enabled (60 req/min by default) | Basic protection even without auth |
| Path traversal | PathUtils enforces root containment for all file tools | Prevents agent from accessing files outside the workspace |
| Shell execution | ShellTool executes commands under the configured working directory | Inherits OS user permissions; do not run as root |

## Threat Model

BotNexus assumes the **caller is trusted** when running locally. The risks this project guards against are:

1. **Accidental credential exposure** — `auth.json`, `.env`, and similar files are excluded from git via `.gitignore`. API keys should always be stored in `~/.botnexus/auth.json` or environment variables, never in committed config files.
2. **Path traversal** — All file system tools enforce root containment via `PathUtils.ResolvePath`.
3. **Runaway shell commands** — `ShellTool` has configurable timeouts; `SafetyHooks` block known dangerous patterns (e.g., `rm -rf /`).
4. **Supply chain** — Dependencies are tracked via NuGet package lock files; CI verifies builds on every PR.

## What Is NOT a Vulnerability in This Project

- Lack of authentication on endpoints when no API key is configured (intentional local-dev design)
- HTTP (not HTTPS) by default on localhost (TLS termination is the responsibility of a reverse proxy for networked deployments)

## Reporting a Vulnerability

This is an open-source project. If you find a genuine security vulnerability (not covered by the design decisions above), please open a GitHub Issue with the `security` label. For sensitive findings, use GitHub's private vulnerability reporting feature.

Do not post credentials, tokens, or exploit code in public issues.
