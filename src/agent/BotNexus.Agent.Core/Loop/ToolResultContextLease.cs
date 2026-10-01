using BotNexus.Agent.Core.Types;

namespace BotNexus.Agent.Core.Loop;

/// <summary>Outcome of the provider turn offered leased tool-result detail.</summary>
public enum ToolResultConsumptionOutcome
{
    Success,
    ProviderFailure,
    ContentFiltered,
    Cancelled,
}

/// <summary>
/// One provider-request projection and the typed results whose detail it offered. The batch is
/// committed atomically only after a successful assistant continuation.
/// </summary>
public sealed record ToolResultContextProjection(
    IReadOnlyList<AgentMessage> Messages,
    IReadOnlyList<ToolResultContextLeaseKey> OfferedResults);

/// <summary>Stable identity of one revision leased into model context.</summary>
public sealed record ToolResultContextLeaseKey(ToolResultId ResultId, long Revision);

/// <summary>
/// Replaces already-consumed typed tool-result detail with compact receipts while leaving the
/// authoritative agent timeline untouched. Untyped and failed tool results retain their existing
/// behavior until generic stored-result envelope adoption supplies a safe recall contract.
/// </summary>
public sealed class ToolResultContextLease
{
    private readonly HashSet<ToolResultContextLeaseKey> _consumed = [];

    /// <summary>Builds the model-visible projection for one provider attempt.</summary>
    public ToolResultContextProjection Project(IReadOnlyList<AgentMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        List<AgentMessage>? projected = null;
        var offered = new List<ToolResultContextLeaseKey>();
        for (var index = 0; index < messages.Count; index++)
        {
            if (messages[index] is not ToolResultAgentMessage
                {
                    IsError: false,
                    Result.Details: ToolResultReceipt receipt,
                } toolResult)
            {
                continue;
            }

            var key = new ToolResultContextLeaseKey(receipt.ResultId, receipt.Revision);
            if (_consumed.Contains(key))
            {
                projected ??= messages.ToList();
                projected[index] = ToReceiptMessage(toolResult, receipt);
            }
            else
            {
                offered.Add(key);
            }
        }

        return new ToolResultContextProjection(projected ?? messages, offered);
    }

    /// <summary>
    /// Commits every result offered in the request together, and only after a successful consuming
    /// assistant turn. Failure, cancellation, filtering, and retry paths are deliberate no-ops.
    /// </summary>
    public void Complete(ToolResultContextProjection projection, ToolResultConsumptionOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (outcome != ToolResultConsumptionOutcome.Success)
        {
            return;
        }

        foreach (var key in projection.OfferedResults)
        {
            _consumed.Add(key);
        }
    }

    private static ToolResultAgentMessage ToReceiptMessage(
        ToolResultAgentMessage message,
        ToolResultReceipt receipt)
    {
        var projection = receipt.ToModelProjection();
        var recall = "Retained tool-result detail has been consumed. Use a supported stored-result "
            + "operation with result_id and revision to recall bounded evidence; do not rerun the source tool.";
        return message with
        {
            Result = new AgentToolResult(
                [new AgentToolContent(AgentToolContentType.Text, $"{projection}\n{recall}")],
                receipt),
        };
    }
}
