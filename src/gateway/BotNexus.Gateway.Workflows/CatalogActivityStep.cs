using System.Text;
using System.Text.Json;
using BotNexus.Gateway.Abstractions.Workflows;
using WorkflowCore.Interface;
using WorkflowCore.Models;

namespace BotNexus.Gateway.Workflows;

/// <summary>
/// Trusted code-defined WorkflowCore step. Inputs and outputs are JSON strings so persistence does not
/// depend on JsonElement/Newtonsoft interoperability. No principal or executable type is persisted.
/// </summary>
/// <param name="catalog">The execution-scoped catalog, resolved with its activity and authorization dependencies.</param>
/// <param name="contexts">The runtime identity resolver from the same execution scope.</param>
/// <remarks>Registered transient; mutable mapping properties must not be shared across concurrent executions.</remarks>
public sealed class CatalogActivityStep(IActivityCatalog catalog, IWorkflowInvocationContextResolver contexts) : StepBodyAsync
{
    /// <summary>Gets or sets the registered lowercase namespaced activity name; no CLR type is loaded from it.</summary>
    public string ActivityName { get; set; } = string.Empty;
    /// <summary>Gets or sets the positive immutable wire-contract version to invoke.</summary>
    public int ActivityVersion { get; set; }
    /// <summary>Gets or sets persisted JSON input, bounded by the bridge limit and the selected activity contract.</summary>
    public string InputJson { get; set; } = "null";
    /// <summary>Gets the successful JSON output as independently owned text for engine output mappings.</summary>
    public string OutputJson { get; private set; } = "null";

    /// <summary>Resolves current trusted identity, invokes the catalog, and advances only after a successful result.</summary>
    /// <param name="context">The engine-owned instance identity and cancellation token; not authored actor data.</param>
    /// <returns>The next-step result after copying the activity output into <see cref="OutputJson"/>.</returns>
    /// <exception cref="UnauthorizedAccessException">The runtime binding is absent, mismatched, or denied by policy.</exception>
    /// <exception cref="ArgumentException">Input exceeds the bridge limit or violates an activity contract.</exception>
    /// <exception cref="JsonException">Input is not valid JSON.</exception>
    /// <remarks>
    /// Catalog errors are suspended by CatalogActivitySafetyMiddleware. This does not guarantee exactly-once effects:
    /// a crash between an effect and a durable checkpoint still requires activity-level reconciliation.
    /// </remarks>
    public override async Task<ExecutionResult> RunAsync(IStepExecutionContext context)
    {
        var cancellationToken = context.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        // This ID comes from the engine, not an input mapping or authored actor field.
        var invocation = await contexts.ResolveAsync(context.Workflow.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("No trusted runtime identity is bound to this workflow instance.");
        if (!StringComparer.Ordinal.Equals(invocation.RunId, context.Workflow.Id))
            throw new UnauthorizedAccessException("Runtime identity binding does not match the workflow instance.");
        if (InputJson is null || Encoding.UTF8.GetByteCount(InputJson) > ActivityDescriptor.AbsoluteMaxBytes)
            throw new ArgumentException("Activity JSON exceeds the bridge input limit.", nameof(InputJson));
        using var input = JsonDocument.Parse(InputJson);
        var result = await catalog.InvokeAsync(ActivityName, ActivityVersion, invocation, input.RootElement, cancellationToken).ConfigureAwait(false);
        OutputJson = result.GetRawText();
        return ExecutionResult.Next();
    }
}
