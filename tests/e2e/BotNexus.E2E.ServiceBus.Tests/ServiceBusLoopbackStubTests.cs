using System.Threading.Tasks;

namespace BotNexus.E2E.ServiceBus.Tests;

/// <summary>
/// STUB e2e coverage for the Service Bus channel (issue #1962, epic #1958).
///
/// <para>This project marks that Service Bus channel e2e work EXISTS and is tracked,
/// without pretending to cover it. The real test will stand up the Azure Service Bus
/// emulator and drive a full loopback:</para>
/// <list type="number">
///   <item><description>publish an inbound message to the ingress queue/topic,</description></item>
///   <item><description>let the gateway ingest it and run an agent turn,</description></item>
///   <item><description>assert the reply is published back onto the egress entity.</description></item>
/// </list>
///
/// <para>The single placeholder below is a <see cref="SkippableFact"/> that ALWAYS
/// skips with a clear reason - so the suite reports "skipped (TBD)" rather than a
/// green pass that falsely implies coverage.</para>
/// </summary>
public sealed class ServiceBusLoopbackStubTests
{
    [SkippableFact]
    public Task ServiceBusLoopback_IsNotYetImplemented()
    {
        // TODO(#1962 follow-up): replace this stub with a real loopback test once the
        // Azure Service Bus emulator harness is available in CI. Wire:
        //   emulator -> publish inbound -> gateway ingest -> agent turn -> assert reply.
        Skip.If(true, "Service Bus e2e loopback is TBD (needs the Service Bus emulator harness).");
        return Task.CompletedTask;
    }
}
