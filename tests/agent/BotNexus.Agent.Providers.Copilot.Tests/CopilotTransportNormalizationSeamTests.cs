using System.Reflection;
using BotNexus.Agent.Providers.Copilot.Completions;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Providers.Copilot.Tests;

/// <summary>
/// Inverted fence (#3442): NO Copilot transport mutates streamed text deltas.
/// </summary>
/// <remarks>
/// This file previously proved the opposite property - that all three transports routed through
/// <c>CopilotTextDeltaNormalizer</c> (#2443). That normalizer existed to strip a per-delta CRLF
/// transport prefix, and mitm captures of the identical endpoints contain 0 raw CR bytes across
/// 3,025 provider deltas. The corruption it was built for was injected by our own
/// <c>string.Join(Environment.NewLine, ...)</c> in <c>MessageConverter.ToAgentMessage</c> (#3425,
/// fixed in #3428), which is why five transport-side fixes (#2049, #2119, #2170, #2443, #3336)
/// each appeared to work and each recurred.
/// <para>
/// The fence is retained rather than deleted, with its assertion inverted rather than weakened: the
/// property worth pinning is now "no Copilot transport reintroduces a lossy delta transform on a
/// falsified premise". A future author re-adding one must delete this fence deliberately.
/// </para>
/// </remarks>
public class CopilotTransportNormalizationSeamTests
{
    private static CompletionsTransportProfile BuildCompletionsProfile()
    {
        var method = typeof(CopilotCompletionsProvider).GetMethod(
            "BuildProfile", BindingFlags.NonPublic | BindingFlags.Static);
        method.ShouldNotBeNull("CopilotCompletionsProvider must still build its transport profile.");

        return (CompletionsTransportProfile)method!.Invoke(null, [null])!;
    }

    // The transport profile must carry no delta-mutation hook at all: the record member is gone,
    // so this asserts the shape of the profile the provider actually builds.
    [Fact]
    public void CompletionsTransportProfile_HasNoTextDeltaMutationHook()
    {
        BuildCompletionsProfile();

        typeof(CompletionsTransportProfile)
            .GetProperties()
            .Select(p => p.Name)
            .ShouldNotContain(
                "NormalizeTextDelta",
                "The CRLF that this hook stripped is not on the wire (#3442). Copilot text deltas " +
                "accumulate byte-identically, as on every non-Copilot transport.");
    }

    // The Copilot provider assembly must contain NO text-delta normalizer type. A re-added one
    // would be a sixth fix built on a premise the captures falsified.
    [Fact]
    public void CopilotAssembly_HasNoTextDeltaNormalizer()
    {
        var normalizers = typeof(CopilotCompletionsProvider).Assembly
            .GetTypes()
            .Where(t => t.Name.Contains("TextDeltaNormalizer", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .ToList();

        normalizers.ShouldBeEmpty(
            "CopilotTextDeltaNormalizer was deleted by #3442 because Copilot sends no CR on the " +
            "wire. If a newline defect recurs, the root cause is assembly (#3425), not framing.");
    }
}
