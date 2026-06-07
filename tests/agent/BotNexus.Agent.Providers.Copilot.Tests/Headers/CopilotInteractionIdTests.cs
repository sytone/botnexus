using System.Diagnostics;
using BotNexus.Agent.Providers.Copilot.Headers;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Copilot.Tests.Headers;

/// <summary>
/// Pins <see cref="CopilotInteractionId"/>'s precedence rules:
/// explicit override &gt; current trace id &gt; fresh GUID. The "never reuse"
/// invariant — captures show the real Copilot CLI never sends the same
/// X-Interaction-Id twice — is verified by issuing two resolves without a
/// trace context and asserting the values differ.
/// </summary>
public class CopilotInteractionIdTests
{
    [Fact]
    public void Resolve_NullOptions_AndNoActivity_ReturnsFreshGuidEachCall()
    {
        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            var a = CopilotInteractionId.Resolve(options: null);
            var b = CopilotInteractionId.Resolve(options: null);

            Guid.TryParseExact(a, "D", out _).ShouldBeTrue();
            Guid.TryParseExact(b, "D", out _).ShouldBeTrue();
            a.ShouldNotBe(b);
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    [Fact]
    public void Resolve_PrefersExplicitOverride()
    {
        var options = new CopilotHeaderOptions(InteractionId: "00000000-0000-0000-0000-00000000ffff");

        var resolved = CopilotInteractionId.Resolve(options);

        resolved.ShouldBe("00000000-0000-0000-0000-00000000ffff");
    }

    [Fact]
    public void Resolve_FallsBackToActivityIdWhenAvailable()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("BotNexus.Test.InteractionId");
        using var activity = source.StartActivity("test", ActivityKind.Client)!;
        activity.ShouldNotBeNull();

        var resolved = CopilotInteractionId.Resolve(options: null);

        resolved.ShouldBe(activity.Id);
    }

    [Fact]
    public void WithResolvedInteractionId_NullOptions_ReturnsNull()
    {
        CopilotInteractionId.WithResolvedInteractionId(null).ShouldBeNull();
    }

    [Fact]
    public void WithResolvedInteractionId_PopulatesInteractionIdOnExistingOptions()
    {
        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            var options = new CopilotHeaderOptions(IntegrationId: "copilot-developer-cli");

            var updated = CopilotInteractionId.WithResolvedInteractionId(options);

            updated.ShouldNotBeNull();
            updated!.IntegrationId.ShouldBe("copilot-developer-cli");
            updated.InteractionId.ShouldNotBeNullOrEmpty();
            Guid.TryParseExact(updated.InteractionId!, "D", out _).ShouldBeTrue();
        }
        finally
        {
            Activity.Current = previous;
        }
    }
}
