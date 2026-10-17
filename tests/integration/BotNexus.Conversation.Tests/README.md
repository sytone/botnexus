# Conversation HTTP and SignalR seam tests

These developer tests run the candidate `Program` through ASP.NET Core `TestServer`.
They do not connect to a developer gateway, open a TCP listener, or require model credentials.

## Run the gate

From an isolated repository worktree, use remote CORE validation:

```powershell
scripts/repo/Invoke-AzureBuildTest.ps1 -Mode core -WorktreePath <worktree>
```

Do not run a test host on a machine hosting the live gateway. A local `dotnet build`
of this project is safe, but it is not a behavior verdict.

## Boundary and isolation

`CandidateGatewayFixture` uses `WebApplicationFactory<Program>` and a SignalR client
with `factory.Server.CreateHandler()` and long polling. HTTP controllers, hub methods,
routing, production in-memory conversation/session stores, and SignalR adapter delivery
are real. Deterministic inbound execution replaces only agent/provider work outside
this seam. This does not test durable SQLite restart behavior or socket transport.

The fixture creates a unique disposable home and configuration, restores its process
environment, disables hosted background services and isolation strategies, closes host
and bootstrap logging, and removes its home. It never reads the operator's configuration.
Each mutating REST test registers a fresh agent. First-send tests use fresh agents and
sessions; separate seeded history supplies explicit user/tool/assistant rows.

Startup failure fails collection initialization. There is no availability flag or
precondition skip. All 28 former live-gateway cases remain represented; two vacuous
future-state probes now assert actual conversation identities. A further candidate-host
regression checks in-process transport and the candidate version endpoint.

| Test class | Boundary |
| --- | --- |
| `ConversationRestApiTests` | Creation, listing, updates, missing resources and history |
| `ConversationBindingTests` | Binding CRUD, composite addresses and projections |
| `ConversationSignalRTests` | Snapshot, first-send routing, identity stamping and stream event delivery |
| `ProbeRound2Tests` | Independent conversations and seeded session/history projections |
| `CandidateHostRegressionTests` | Candidate host and transport ownership |

## Read evidence honestly

Inspect each project's TRX results, not only the aggregate green status. For this
project every discovered test must execute and pass, with zero skipped results.
CORE still reports separate intentional credential/platform skips in other projects;
those are not passes. The runner no longer accepts `Dev gateway not running at` as a
deliberate skip reason. Browser-emulated E2E quarantine is separate (#3601).
