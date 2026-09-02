using BotNexus.Agent.Core.Tools;

namespace BotNexus.Extensions.ExecTool.Tests;

/// <summary>
/// Issue #3704 AC2: exec and process must not word the retention disclosure independently. This
/// pins the exec call site onto the shared <see cref="OutputRetentionPolicy"/> so a reworded banner
/// can never land on one tool alone again.
/// </summary>
public class ExecToolSharedRetentionPolicyTests
{
    [Fact]
    public void FormatTruncationBanner_IsProducedByTheSharedPolicyHelper()
    {
        var viaExec = ExecTool.FormatTruncationBanner(retainedBytes: 102_204, discardedBytes: 48_096);
        var viaShared = OutputRetentionPolicy.FormatTruncationBanner(
            102_204,
            48_096,
            RetainedOutputPortion.Head);

        viaExec.ShouldBe(viaShared);
    }

    [Fact]
    public void TruncationBannerPrefix_IsTheSharedPrefix()
    {
        ExecTool.TruncationBannerPrefix.ShouldBe(OutputRetentionPolicy.TruncationBannerPrefix);
    }

    [Fact]
    public void SharedPolicy_NamesTheRetainedEndPerTool()
    {
        OutputRetentionPolicy.FormatTruncationBanner(10, 5, RetainedOutputPortion.Head)
            .ShouldContain("retained 10 bytes (head)");
        OutputRetentionPolicy.FormatTruncationBanner(10, 5, RetainedOutputPortion.Tail)
            .ShouldContain("retained 10 bytes (tail)");
    }
}
