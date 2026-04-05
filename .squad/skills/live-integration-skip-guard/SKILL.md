# live-integration-skip-guard

## Purpose
Stabilize live integration tests that run through `GatewayHost` where downstream provider failures are captured as activity events instead of thrown exceptions.

## Pattern
1. Keep hard gating (`BOTNEXUS_RUN_COPILOT_INTEGRATION=1`) and auth-file availability checks.
2. Execute real dispatch through `GatewayHost` with a recording `IActivityBroadcaster`.
3. If `GatewayActivityType.Error` contains auth/connectivity signatures (401/403/unauthorized/connection/timeout/host/SSL), treat as graceful skip.
4. Otherwise, enforce strict assertions on channel output, session history, router/supervisor invocations, and stream assembly.

## References
- `tests/BotNexus.Gateway.Tests/Integration/CopilotIntegrationTests.cs`
- `src/gateway/BotNexus.Gateway/GatewayHost.cs`
