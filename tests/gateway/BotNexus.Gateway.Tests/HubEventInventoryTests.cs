using System.Reflection;
using BotNexus.Extensions.Channels.SignalR;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Pins the generated hub event inventory against <see cref="IGatewayHubClient"/> (#3318).
/// </summary>
/// <remarks>
/// The defect this guards is measured, not hypothetical: on <c>main</c> the two integration
/// harnesses each carried a hand-written 13-element array while the interface declared 24 events.
/// A harness cannot receive an event it never subscribed to, so a regression in any of the missing
/// eleven presented as an empty event list - indistinguishable from a quiet but passing run.
/// <para>
/// These assertions compare the inventory the harnesses consume against the interface's members by
/// reflection, so they fail NAMING the divergent member rather than reporting a count mismatch.
/// </para>
/// </remarks>
public class HubEventInventoryTests
{
    private static string[] DeclaredEvents() =>
        typeof(IGatewayHubClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .ToArray();

    [Fact]
    public void Inventory_CoversEveryDeclaredHubEvent()
    {
        // Non-vacuity: this is the assertion a hand-written inventory fails. Deleting any name from
        // HubEvents.All (or adding a method to IGatewayHubClient without regenerating) fails here
        // and names the missing member, which is exactly the mutation recorded in the PR body.
        var missing = DeclaredEvents().Except(HubEvents.All).OrderBy(name => name).ToArray();

        Assert.True(
            missing.Length == 0,
            $"IGatewayHubClient declares events absent from HubEvents.All: {string.Join(", ", missing)}. "
            + "The inventory is generated - a divergence here means the generator did not run.");
    }

    [Fact]
    public void Inventory_ContainsNoEventTheInterfaceDoesNotDeclare()
    {
        // The other direction matters too: a stale name makes every harness subscribe to an event
        // the server never sends, which is silent rather than failing.
        var extra = HubEvents.All.Except(DeclaredEvents()).OrderBy(name => name).ToArray();

        Assert.True(
            extra.Length == 0,
            $"HubEvents.All carries names IGatewayHubClient does not declare: {string.Join(", ", extra)}.");
    }

    [Fact]
    public void Inventory_IsNotEmptyAndMatchesTheDeclaredCount()
    {
        // Guards the degenerate pass: two empty sets satisfy both Except checks above.
        Assert.NotEmpty(HubEvents.All);
        Assert.Equal(DeclaredEvents().Length, HubEvents.All.Length);
    }

    [Fact]
    public void Inventory_IncludesTheElevenEventsTheHandWrittenHarnessArraysOmitted()
    {
        // Named explicitly so the regression this change fixes cannot silently return: these are the
        // exact eleven events measured as missing from both TestSignalRClient arrays on main.
        string[] previouslyMissing =
        [
            "RunStarted", "RunEnded", "TurnEnd", "TurnInterrupted", "UserInputRequired",
            "AgentsChanged", "ConversationChanged", "SteeringFeedback", "CanvasUpdated",
            "CanvasStateChanged", "TodoUpdated"
        ];

        foreach (var name in previouslyMissing)
        {
            Assert.Contains(name, HubEvents.All);
        }
    }
}
