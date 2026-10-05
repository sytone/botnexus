namespace BotNexus.Agent.Core.Diagnostics;

/// <summary>Delivers informational diagnostics without giving subscribers execution authority.</summary>
internal static class DiagnosticNotification
{
    /// <summary>
    /// Reports one message to each synchronous subscriber without allowing a delivery failure
    /// to replace the caller's outcome. A null observer performs no delivery.
    /// </summary>
    public static void Report(Action<string>? observer, string message)
    {
        if (observer is null)
            return;

        foreach (var subscriber in observer.GetInvocationList().Cast<Action<string>>())
        {
            try
            {
                subscriber(message);
            }
            catch (Exception)
            {
                // Includes subscriber-thrown cancellation: this sink has no run-cancellation
                // authority. Do not report its failure through the same sink recursively.
            }
        }
    }
}