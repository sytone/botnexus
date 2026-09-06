# Sub-agent workspace lifecycle

An isolated child needs a real working directory before its first tool invocation. Reading an explicitly granted external path does not establish that the child's own working directory exists.

## Admission and ownership

`DefaultSubAgentManager` admits the request through the existing model, grant and budget checks, then registers the child descriptor. For the production `FileAgentWorkspaceManager`, it explicitly provisions an empty isolated directory before conversation/handle construction. Registering first makes the existing registry liveness probe authoritative as soon as the directory exists. A provisioning failure unregisters the child and fails admission before a handle is exposed.

Provisioning does not copy parent files or change file-access policy. `shareWorkspace` adds parent read/write grants; it does not replace the child's isolated cwd with the parent's workspace. Custom workspace-manager implementations retain their existing lifecycle.

`GetWorkspacePath` remains a resolver for isolated children. Neither that lookup nor `DefaultAgentToolFactory` recreates the directory after cleanup. This prevents a status lookup or retried tool call from resurrecting a retired workspace.

## Terminal cleanup and audit

An explicit kill atomically claims `Killed` before cancelling the run token. Cancellation callbacks therefore observe the winning disposition instead of racing to classify the run as timed out. A completed run or an earlier kill cannot be overwritten by a late kill. The existing terminal lifecycle still stops the handle, unregisters the child and attempts cleanup through its once-only record gate. The sweeper still consults the registry liveness probe; elapsed time is not evidence that a run is dead.

The file-backed cleanup implementation returns `true` only after deleting an existing owned child directory. An absent directory, including a directory removed by another cleanup before deletion, returns `false`. The lifecycle emits its successful reclamation audit only for `true`. Parent directories and persistent-agent workspaces are not cleanup targets.

The interface's legacy XML return description still includes an already-absent directory as success. That description is outside this change's reserved file scope; callers requiring evidence of actual deletion must use the file-backed behavior described here. Correcting that interface documentation remains follow-up work.

## Missing-workspace diagnostics

`ReclaimedWorkspacePreflight` retains its existing public entry points and narrow sub-agent-path marker check. Its message states the observed fact: the working directory does not exist. Without lifecycle evidence, it cannot distinguish never provisioned, reclaimed, or otherwise unavailable.

The message does not assert prior creation, successful use, or deletion, and does not claim that separately granted external paths are unavailable. It advises stopping operations that require the missing cwd and reporting the condition to the parent.

## Regression evidence

`SubAgentWorkspaceProvisioningTests` drives production spawn orchestration, the file-backed workspace manager and the default tool factory with a controlled supervisor/handle. It executes a real first cwd command without any preceding child write or memory operation. It covers live ownership, shared-parent preservation, terminal removal, and actual-versus-absent audit behavior.

`FileAgentWorkspaceManagerTests` covers absent and repeated cleanup without directory recreation. `ReclaimedWorkspacePreflightTests` covers both a never-created directory and a genuinely created/deleted directory while requiring the same factual, history-unknown diagnostic. Existing lifecycle, liveness and policy tests remain in place.

Validation runs remotely in `core` mode; local builds compile the affected projects without starting test hosts.
