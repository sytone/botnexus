# Channel end-to-end tests (`tests/e2e/`)

This category gives each channel a real, deterministic end-to-end home
(issue #1962, epic #1958). It has four sub-areas:

| Project | Kind | What it does |
| --- | --- | --- |
| `BotNexus.E2E.PortalDesktop.Tests` | **Live Playwright** | Drives a real browser against the Blazor Server **desktop** portal. |
| `BotNexus.E2E.PortalMobile.Tests` | **Live Playwright** | Drives a mobile-emulated browser against the **mobile** Blazor site. |
| `BotNexus.E2E.ServiceBus.Tests` | **Stub** | Placeholder marking Service Bus e2e work exists; real emulator loopback TBD. |
| `BotNexus.E2E.Telegram.Tests` | **Stub** | Placeholder marking Telegram e2e work exists; real long-poll/webhook loopback TBD. |

## Skip contract (never a silent pass)

Every test here uses `[SkippableFact]`. Without the required environment/credentials
they **SKIP with an explicit reason** rather than pass green:

| Variable | Used by | Effect when unset |
| --- | --- | --- |
| `E2E_PORTAL_DESKTOP_URL` | portal-desktop | desktop Playwright tests skip |
| `E2E_PORTAL_MOBILE_URL` | portal-mobile | mobile Playwright tests skip |
| `E2E_LLM=1` | portal-desktop LLM turn | lightweight real-LLM turn skips unless `=1` |
| `E2E_LLM_ENDPOINT` / `E2E_LLM_API_KEY` | portal-desktop LLM turn | **required** when `E2E_LLM=1` (no creds are invented) |

The stub projects always skip with a `TBD` reason.

## Lightweight real LLM

`LightweightRealLlmTurnTests` exercises a genuine cheap model turn against an
OpenAI-compatible endpoint (e.g. GitHub Models `https://models.inference.ai.azure.com`)
gated behind `E2E_LLM=1`. With the flag ON but config missing, it **fails** (you opted
in) — it never silently passes. See the TODO in that file for driving the turn through
the portal channel once the CI portal harness lands.

## CI

The existing Playwright suite under `tests/integration/BotNexus.Integration.E2E.Tests`
never ran in CI (the `full-tests` job excludes `BotNexus.Integration.` and installs no
browser). A new **non-blocking** job (`e2e-portal-playwright`, `continue-on-error: true`)
in `.github/workflows/ci-build-test.yml` installs chromium and runs the `tests/e2e/`
portal suites so this coverage stops being decorative — without gating the required
checks for other PRs.
