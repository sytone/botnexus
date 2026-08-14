using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Audit;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// A deliberately BYPASSING agent-execution call site, compiled into the test assembly so the
/// #2616 fence's detector can be run against a known-bad shape (AC4 non-vacuity).
/// </summary>
/// <remarks>
/// This type is never executed and is never scanned by the production fence - the fence reads the
/// shipped <c>BotNexus.Gateway*</c> assemblies only. It exists so
/// <c>Fence_Reddens_WhenACallSiteDoesNotReachTheSink</c> can prove the detector reports a call site
/// that does not reach the sink, rather than the fence merely being green because it found nothing.
/// It is the in-suite twin of the throwaway-mutation check run by hand at review time.
/// </remarks>
internal sealed class BypassingProbe
{
    /// <summary>Executes an agent and persists nothing. This is exactly the shape #2616 forbids.</summary>
    /// <param name="handle">The agent handle.</param>
    /// <returns>The response content.</returns>
    public static async Task<string> RunAsync(IAgentHandle handle)
    {
        var response = await handle.PromptAsync("probe", CancellationToken.None).ConfigureAwait(false);
        return response.Content;
    }
}

/// <summary>
/// The compliant twin of <see cref="BypassingProbe"/>: an execution call site that routes the run's
/// tool timeline through the sink. Proves the detector discriminates instead of flagging every call
/// site, which would make the fence green-proof rather than correct.
/// </summary>
internal sealed class CompliantProbe
{
    /// <summary>Executes an agent and projects its tool timeline through the audit sink.</summary>
    /// <param name="handle">The agent handle.</param>
    /// <param name="sink">The execution-layer tool-audit sink.</param>
    /// <returns>The number of audit rows the run produced.</returns>
    public static async Task<int> RunAsync(IAgentHandle handle, IToolAuditSink sink)
    {
        var response = await handle.PromptAsync("probe", CancellationToken.None).ConfigureAwait(false);
        return sink.ProjectBlockingRun(sink.CaptureBlockingRun(response)).Count;
    }
}
