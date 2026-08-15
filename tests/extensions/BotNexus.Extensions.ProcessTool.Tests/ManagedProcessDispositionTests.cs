using BotNexus.Extensions.ProcessTool;

namespace BotNexus.Extensions.ProcessTool.Tests;

/// <summary>
/// Issue #2726: the process surface has the same two-disposition problem as exec. A start failure
/// provably ran nothing; an unconfirmed kill may have left work in flight. The two must not read
/// the same, or an agent recovering from either will make the same (sometimes wrong) choice.
/// </summary>
public class ManagedProcessDispositionTests
{
    [Fact]
    public void NotDispatchedMessage_StatesNothingRanAndThatRetryIsSafe()
    {
        ManagedProcess.NotDispatchedMessage.ShouldContain("did not run");
        ManagedProcess.NotDispatchedMessage.ShouldContain("safe to retry");
        ManagedProcess.NotDispatchedMessage.ShouldNotContain("may still be running");
    }
}
