using System.Threading.Tasks;

namespace BotNexus.E2E.Telegram.Tests;

/// <summary>
/// STUB e2e coverage for the Telegram channel (issue #1962, epic #1958).
///
/// <para>This project marks that Telegram channel e2e work EXISTS and is tracked,
/// without pretending to cover it. The real test will stand up a fake Telegram Bot
/// API server and drive a full loopback:</para>
/// <list type="number">
///   <item><description>deliver an inbound update (long-poll getUpdates or webhook POST),</description></item>
///   <item><description>let the gateway ingest it and run an agent turn,</description></item>
///   <item><description>assert the outbound sendMessage call hits the fake API.</description></item>
/// </list>
///
/// <para>The single placeholder below is a <see cref="SkippableFact"/> that ALWAYS
/// skips with a clear reason - so the suite reports "skipped (TBD)" rather than a
/// green pass that falsely implies coverage.</para>
/// </summary>
public sealed class TelegramLoopbackStubTests
{
    [SkippableFact]
    public Task TelegramLoopback_IsNotYetImplemented()
    {
        // TODO(#1962 follow-up): replace this stub with a real loopback test once the
        // fake Telegram Bot API server harness is available. Wire:
        //   fake API -> inbound update -> gateway ingest -> agent turn -> assert sendMessage.
        Skip.If(true, "Telegram e2e loopback is TBD (needs the fake Telegram Bot API harness).");
        return Task.CompletedTask;
    }
}
