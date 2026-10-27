# Embedded workflow foundation

For developers integrating trusted, code-defined workflows. This project provides an **opt-in,
single-node** WorkflowCore 3.21.0 host and a bounded activity catalog. It is not enabled by the
normal gateway startup. There is no user-facing CLI, API, graph interpreter, or authored CLR/DSL loader.

## Ownership map

| Concern | Owner |
| --- | --- |
| Definition/version registration, branch/loop topology, scheduling, event subscriptions, workflow checkpoints | WorkflowCore |
| SQLite engine persistence | WorkflowCore.Persistence.Sqlite; fixed path `<verified-home>/workflows/workflow-core.sqlite` |
| Namespaced activity registration, contract version, JSON size/shape checks, runtime authorization | This project's catalog and engine-free `Gateway.Abstractions.Workflows` contracts |
| Binding an engine instance to an actor; resolving current/revoked actor permissions on every invocation/resume | Integrating host's `IWorkflowInvocationContextResolver` and `IActivityAuthorizer` |
| Managed-task ownership, operation attempts, side-effect uncertainty, reconciliation and approvals | Existing managed-task ledger/executor; unchanged by this foundation |

The engine database is not a second managed-task ledger. A workflow checkpoint does not prove that
an external effect did or did not happen. Activity implementations that perform effects must go
through the existing attempt/uncertainty boundary, or provide an independently reviewed idempotent
operation boundary. No managed-task executor or ledger is replaced here.

## Integrate in a dedicated owner host

1. Obtain `IVerifiedHome` from the existing verified-home bootstrap. Do not resolve a new home from
   the environment. Pass it to `services.AddEmbeddedWorkflowCore(home)`. The directory is created
   only when the hosted lifecycle starts, not during service registration.
2. Register `IWorkflowActivity` implementations. Each declares an immutable `ActivityDescriptor`:
   a lowercase namespaced name, positive version, input/output UTF-8 byte limits, and optional
   top-level JSON kinds. Domain field validation remains the activity's responsibility. A version
   identifies the wire contract; do not silently change that contract in place.
3. Replace both deny-by-default authorization services with explicit policy. The runtime resolver
   receives **only the engine-generated instance ID**. It must look up a trusted actor binding outside
   authored data, validate its world/ownership, and resolve current permissions. Persist that binding
   separately if workflows must resume across process restarts. Missing or revoked bindings deny.
   Do not persist `ClaimsPrincipal`, authentication tokens, or trusted scopes in workflow data. Never
   treat an authored actor name or role as authorization. The bridge also rejects a context bound
   to a different instance.
4. Register trusted `IWorkflow<TData>` definitions with `IWorkflowHost` before starting the host.
   Use `CatalogActivityStep` for application activity calls. Map `ActivityName`, `ActivityVersion`,
   and `InputJson`; consume `OutputJson` through normal engine output mappings. JSON strings avoid
   relying on `JsonElement` interoperability with the engine's persistence serializer.
5. Let the registered `IHostedService` own engine start/stop. Do not also start the engine manually.
   Keep one owner process per database: the default queue and lock providers are single-node.
   Do not point an experimental host at a live gateway home.

`AddWorkflowActivities()` alone registers only the catalog/policy seams, with no engine host,
background tasks, database, or filesystem mutation. Duplicate activity name/version pairs fail
catalog construction. Unknown versions, invalid sizes/shapes, cancellation, and denied authorization fail
before application code where applicable. Catalog input/output elements are cloned so caller and
application JSON document lifetimes cannot invalidate the returned result.

### Dependency injection lifetimes and extension contributions

`IActivityCatalog` is **scoped**, not singleton. WorkflowCore resolves `CatalogActivityStep` and
its catalog in an execution scope. Register activities and custom policy/resolver services as
scoped or transient when they depend on scoped state. The scope owns their disposal. The two
default deny services are stateless singletons. Singleton contributions must be thread-safe;
the catalog does not serialize calls or make mutable dependencies thread-safe.

For direct catalog calls, create a scope rather than resolving from the root provider. This
illustrative registration uses application-owned implementations and does not start an engine:

```csharp
services.AddScoped<IWorkflowActivity, ReadTaskActivity>();
services.AddScoped<IActivityAuthorizer, CurrentActorAuthorizer>();
services.AddScoped<IWorkflowInvocationContextResolver, TrustedActorResolver>();
services.AddWorkflowActivities(); // scoped catalog; preserves explicit policy registrations

using var provider = services.BuildServiceProvider(new ServiceProviderOptions
{
    ValidateScopes = true,
    ValidateOnBuild = true
});
using var scope = provider.CreateScope();
var catalog = scope.ServiceProvider.GetRequiredService<IActivityCatalog>();
// Invoke with a trusted ActivityInvocationContext; keep the input document alive until completion.
```

An extension references `BotNexus.Gateway.Abstractions` and contributes activities through
`BotNexus.Gateway.Abstractions.Extensions.IServiceContributor`. Activity registration is not
automatically discovered by the activity catalog. For example, in the extension assembly:

```csharp
public sealed class TaskActivityServices : IServiceContributor
{
    public void ConfigureServices(IServiceCollection services)
        => services.AddScoped<IWorkflowActivity, ReadTaskActivity>();
}
```

The contributor must have a public parameterless constructor; the existing extension loader calls
it before building the provider. The integrating dedicated owner host still opts into the catalog
or embedded engine and supplies trusted policies. Contributions alone do not activate an engine
or authorize activities in normal gateway startup. Duplicate name/version registrations fail when
the scoped catalog is first resolved, not while adding service descriptors.

## Failure and recovery limits

`CatalogActivitySafetyMiddleware` forces catalog-step errors to **suspend**, even if a definition
requests automatic retry. Investigate and reconcile the existing attempt/uncertainty state before
an operator explicitly resumes. Do not use blind retries for side effects.

This does **not** provide exactly-once execution. A process can die after an external effect but
before its engine checkpoint is durable. Recovery can revisit that step without a reported error.
The activity/managed-task operation boundary must detect that existing attempt and reconcile it;
engine retries and compensations are not substitutes for that boundary. This foundation is not
production activation approval for arbitrary effectful activities.

SQLite checkpoints and subscriptions can be reopened by a new service provider with the same
registered definition/version and verified root. Definition code and compatible data models must
remain available for persisted versions. There is no workflow migration policy or multi-node
coordination in this stage.

## Verification boundary

`tests/gateway/BotNexus.Gateway.Workflows.Tests` preserves the original catalog tests and adds:

- Wire-shape, undefined JSON, size, cancellation, and runtime identity-binding boundary tests.
- Validated DI scopes with scoped activities, authorizers, and identity resolvers, isolated across
  scopes, disposed before the root provider, and rejected on root resolution.
- A middleware test proving automatic retry is overridden with suspend for catalog steps.
- A real WorkflowCore/SQLite workflow that maps activity results, branches, loops, waits for an
  event, closes its provider, reopens the same database, and completes after publishing the event.

These tests start only the embedded engine, not a gateway. Repository policy still prohibits local
test execution on the live host; compile locally and execute behavioral validation remotely.

Upstream API references used for this integration:
[SQLite registration](https://github.com/danielgerlag/workflow-core/blob/master/src/providers/WorkflowCore.Persistence.Sqlite/ServiceCollectionExtensions.cs),
[host lifecycle](https://github.com/danielgerlag/workflow-core/blob/master/src/WorkflowCore/Interface/IWorkflowHost.cs),
and [step middleware](https://github.com/danielgerlag/workflow-core/blob/master/src/WorkflowCore/Interface/IWorkflowStepMiddleware.cs).
The pinned package compile checks the actual referenced API surface; remote execution must verify behavior.
