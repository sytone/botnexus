namespace BotNexus.Agent.Core.Diagnostics;

/// <summary>Delivers informational diagnostics without giving subscribers execution authority.</summary>
internal static class DiagnosticNotification
{
    /// <summary>
    /// Reports one message to each synchronous subscriber without allowing a delivery failure
    /// to replace the caller's outcome. A null observer performs no delivery.
    /// </summary>
    public static void Report(Action<string>? observer, string message)
        => ReportSubscribers(observer, subscriber => ((Action<string>)subscriber)(message));

    /// <summary>Delivers typed capacity facts with the same informational subscriber isolation.</summary>
    public static void Report(BotNexus.Agent.Core.ExtensionPoints.ToolResults.ToolCapacityObserver? observer,
        BotNexus.Agent.Core.ExtensionPoints.ToolResults.ToolCapacityObservation observation)
        => ReportSubscribers(observer, subscriber => ((BotNexus.Agent.Core.ExtensionPoints.ToolResults.ToolCapacityObserver)subscriber)(observation));

    private static void ReportSubscribers(Delegate? observer, Action<Delegate> deliver)
    {
        if (observer is null)
            return;

        foreach (var subscriber in observer.GetInvocationList())
        {
            try
            {
                deliver(subscriber);
            }
            catch (Exception)
            {
                // Includes subscriber-thrown cancellation: this sink has no run-cancellation
                // authority. Do not report its failure through the same sink recursively.
            }
        }
    }
}