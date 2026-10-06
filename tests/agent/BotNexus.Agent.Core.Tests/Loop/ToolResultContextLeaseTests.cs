using System.Diagnostics.Metrics;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Types;

namespace BotNexus.Agent.Core.Tests.Loop;

public sealed class ToolResultContextLeaseTests
{
    [Fact]
    public void Project_LeavesTypedResultDetailedUntilSuccessfulConsumption()
    {
        var receipt = CreateReceipt("call-1");
        var result = ToolResult("call-1", "large detail", receipt);
        var lease = new ToolResultContextLease();

        var first = lease.Project([result]);

        Text(first.Messages).ShouldBe("large detail");
        lease.Complete(first, ToolResultConsumptionOutcome.ProviderFailure);
        Text(lease.Project([result]).Messages).ShouldBe("large detail");
    }

    [Fact]
    public void Project_ReplacesConsumedDetailWithCompactReceiptOnFollowingRequest()
    {
        var receipt = CreateReceipt("call-1");
        var result = ToolResult("call-1", new string('x', 32_000), receipt);
        var lease = new ToolResultContextLease();

        var consuming = lease.Project([result]);
        lease.Complete(consuming, ToolResultConsumptionOutcome.Success);
        var following = lease.Project([result]);

        var projected = Text(following.Messages);
        projected.ShouldNotContain(new string('x', 100));
        projected.ShouldContain(receipt.ResultId.Value);
        projected.ShouldContain("operations");
        projected.ShouldContain("do not rerun the source tool");
        projected.Length.ShouldBeLessThan(2_000);
    }

    [Fact]
    public void Complete_CommitsParallelBatchAtomicallyOnlyAfterSuccess()
    {
        var firstReceipt = CreateReceipt("call-1");
        var secondReceipt = CreateReceipt("call-2");
        var messages = new AgentMessage[]
        {
            ToolResult("call-1", "first detail", firstReceipt),
            ToolResult("call-2", "second detail", secondReceipt),
        };
        var lease = new ToolResultContextLease();

        var failedBatch = lease.Project(messages);
        lease.Complete(failedBatch, ToolResultConsumptionOutcome.ContentFiltered);
        var retry = lease.Project(messages);
        Text(retry.Messages, 0).ShouldBe("first detail");
        Text(retry.Messages, 1).ShouldBe("second detail");

        lease.Complete(retry, ToolResultConsumptionOutcome.Success);
        var following = lease.Project(messages);
        Text(following.Messages, 0).ShouldContain(firstReceipt.ResultId.Value);
        Text(following.Messages, 1).ShouldContain(secondReceipt.ResultId.Value);
    }

    [Fact]
    public void Project_ReportsLeaseReceiptAndSavedPromptBytesWithoutPayloadTags()
    {
        var measurements = new List<(string Name, long Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == ToolResultContextTelemetry.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, tags.ToArray())));
        listener.Start();

        var receipt = CreateReceipt("call-telemetry");
        var detail = new string('x', 32_000);
        var result = ToolResult("call-telemetry", detail, receipt);
        using var lease = new ToolResultContextLease();

        var consuming = lease.Project([result]);
        lease.Complete(consuming, ToolResultConsumptionOutcome.Success);
        var following = lease.Project([result]);
        var receiptBytes = System.Text.Encoding.UTF8.GetByteCount(Text(following.Messages));

        measurements.ShouldContain(measurement =>
            measurement.Name == ToolResultContextTelemetry.ActiveLeasedBytesInstrumentName
            && measurement.Value == detail.Length);
        measurements.ShouldContain(measurement =>
            measurement.Name == ToolResultContextTelemetry.ActiveLeasedBytesInstrumentName
            && measurement.Value == -detail.Length);
        measurements.ShouldContain(measurement =>
            measurement.Name == ToolResultContextTelemetry.ReceiptBytesInstrumentName
            && measurement.Value == receiptBytes);
        measurements.ShouldContain(measurement =>
            measurement.Name == ToolResultContextTelemetry.SavedPromptBytesInstrumentName
            && measurement.Value == detail.Length - receiptBytes);
        measurements.SelectMany(measurement => measurement.Tags)
            .ShouldAllBe(tag => tag.Key == "tool.result.kind");
        measurements.SelectMany(measurement => measurement.Tags)
            .ShouldNotContain(tag => Equals(tag.Value, receipt.ResultId.Value) || Equals(tag.Value, detail));
    }

    [Fact]
    public void Project_DoesNotLeaseErrorsOrUntypedResults()
    {
        var receipt = CreateReceipt("call-error");
        var messages = new AgentMessage[]
        {
            ToolResult("call-error", "failure detail", receipt) with { IsError = true },
            ToolResult("call-plain", "ordinary detail", details: null),
        };
        var lease = new ToolResultContextLease();

        var first = lease.Project(messages);
        lease.Complete(first, ToolResultConsumptionOutcome.Success);
        var following = lease.Project(messages);

        Text(following.Messages, 0).ShouldBe("failure detail");
        Text(following.Messages, 1).ShouldBe("ordinary detail");
    }

    private static ToolResultReceipt CreateReceipt(string sourceCallId)
    {
        var store = new ToolResultStore();
        return store.Store(
            System.Text.Encoding.UTF8.GetBytes("retained detail"),
            new ToolResultDescriptor(
                ToolResultKind.Object,
                "application/json",
                "example/v1",
                ToolResultProvenance.ForeignUntrusted,
                "example_tool",
                sourceCallId,
                12,
                ToolResultCompleteness.Complete,
                ToolResultRetention.Volatile),
            new ToolResultScope("world", "agent", "conversation", "session", "policy"));
    }

    private static ToolResultAgentMessage ToolResult(string callId, string text, object? details) =>
        new(
            callId,
            "example_tool",
            new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, text)], details));

    private static string Text(IReadOnlyList<AgentMessage> messages, int index = 0) =>
        ((ToolResultAgentMessage)messages[index]).Result.Content.ShouldHaveSingleItem().Value;
}
