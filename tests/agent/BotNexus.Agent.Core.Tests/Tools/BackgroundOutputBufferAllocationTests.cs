using BotNexus.Agent.Core.Tools;
using Xunit.Abstractions;

namespace BotNexus.AgentCore.Tests.Tools;

public sealed class BackgroundOutputBufferAllocationTests(ITestOutputHelper output)
{
    [Fact]
    public void AppendChunk_AllocationAfterWarmup_IsIndependentOfRetainedTailSize()
    {
        const int smallCap = 4 * 1024;
        const int largeCap = 100 * 1024;
        const int appendCount = 512;
        const string payload = "abcdefgh";

        _ = MeasureAllocatedBytes(smallCap, payload, 8);
        _ = MeasureAllocatedBytes(largeCap, payload, 8);

        var smallCapBytes = MeasureAllocatedBytes(smallCap, payload, appendCount);
        var largeCapBytes = MeasureAllocatedBytes(largeCap, payload, appendCount);
        output.WriteLine($"smallCapBytes={smallCapBytes}; largeCapBytes={largeCapBytes}; appendedBytes={appendCount * payload.Length}");

        largeCapBytes.ShouldBeLessThan(
            smallCapBytes * 4,
            $"Appending the same {appendCount * payload.Length} characters allocated {smallCapBytes:N0} bytes with a {smallCap:N0}-byte retained tail and {largeCapBytes:N0} bytes with a {largeCap:N0}-byte retained tail.");
    }

    [Fact]
    public void AppendChunk_PreservesUtf8AccountingAcrossSplitScalarAndTrimming()
    {
        var buffer = new BackgroundOutputBuffer(6);

        buffer.AppendChunk("a\ud83d");
        buffer.RetainedBytes.ShouldBe(4);
        buffer.AppendChunk("\ude00bc");

        buffer.RawSnapshot().ShouldBe("😀bc");
        buffer.RetainedBytes.ShouldBe(6);
        buffer.DiscardedBytes.ShouldBe(1);
    }

    private static long MeasureAllocatedBytes(int cap, string payload, int appendCount)
    {
        var buffer = new BackgroundOutputBuffer(cap);
        buffer.AppendChunk(new string('x', cap));

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < appendCount; index++)
            buffer.AppendChunk(payload);

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
