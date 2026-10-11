namespace BotNexus.Gateway.Dispatching;

/// <summary>Controls process-wide inbound admission during a planned lifecycle transition.</summary>
public interface IInboundAdmissionControl
{
    /// <summary>Atomically closes admission. Existing queue items and running turns continue settling.</summary>
    bool TryBeginQuiesce();

    /// <summary>True after planned shutdown has closed admission for this process.</summary>
    bool IsQuiescing { get; }
}
