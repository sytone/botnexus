using System.Text.RegularExpressions;
using BotNexus.Gateway.Abstractions.Conversations;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Fences for issue #3660: startup ask_user reconciliation must never scan the whole conversation
/// population, and every <see cref="IConversationStore"/> implementation must carry the narrow
/// pending-checkpoint query.
/// </summary>
/// <remarks>
/// The behavioural tests in <c>AskUserCheckpointReconciliationServiceTests</c> prove the current
/// service uses the narrow query. These fences prove it stays that way, and that a store
/// implementation added later cannot quietly omit the API and force a caller back onto
/// <c>ListAsync</c>.
/// </remarks>
public sealed class AskUserReconciliationScanArchitectureTests : ArchitectureTest
{
    /// <summary>
    /// #3660 acceptance criterion 1: a test fails if <c>ListAsync</c> is reintroduced into the
    /// reconciliation service. The whole defect was that a startup hosted service materialised
    /// 3,964 conversations to find 3, so the call itself is the thing to ban, not its cost.
    /// </summary>
    [Fact]
    public void ReconciliationService_DoesNotCall_ConversationStoreListAsync()
    {
        var servicePath = Repository.Path(
            "src", "gateway", "BotNexus.Gateway", "Services", "AskUserCheckpointReconciliationService.cs");
        File.Exists(servicePath).ShouldBeTrue(
            $"non-vacuity: the fence must point at a real file, but {servicePath} is missing");

        var source = File.ReadAllText(servicePath);

        // Non-vacuity: the file must still contain the call the fence is about, otherwise the
        // service was renamed out from under the fence and the assertion proves nothing.
        source.Contains("GetPendingAskUserCheckpointsAsync", StringComparison.Ordinal).ShouldBeTrue(
            "the reconciliation service must reach the store through the narrow #3660 query");

        var listCalls = ListAsyncCallPattern.Matches(source);
        listCalls.Count.ShouldBe(0,
            "#3660: AskUserCheckpointReconciliationService must not call IConversationStore.ListAsync — " +
            "it materialises every conversation as a blocking startup operation and delays the Kestrel " +
            "port bind. Use GetPendingAskUserCheckpointsAsync. Offending text: " +
            string.Join(", ", listCalls.Select(m => m.Value)));
    }

    /// <summary>
    /// #3660 acceptance criterion 5: every <see cref="IConversationStore"/> implementation supplies
    /// the pending-checkpoint API. C# already requires this to compile, so the fence exists to
    /// catch the escape hatch — a default interface implementation that silently forwards to
    /// <c>ListAsync</c> and reinstates the full scan for any store that does not override it.
    /// </summary>
    [Fact]
    public void PendingAskUserCheckpointApi_IsAbstract_SoEveryImplementationMustSupplyIt()
    {
        var method = typeof(IConversationStore)
            .GetMethod(nameof(IConversationStore.GetPendingAskUserCheckpointsAsync));

        method.ShouldNotBeNull(
            "#3660: IConversationStore must declare GetPendingAskUserCheckpointsAsync");
        method!.IsAbstract.ShouldBeTrue(
            "#3660: the pending-checkpoint query must have no default interface implementation, so a " +
            "new IConversationStore implementation cannot inherit a fallback that scans every " +
            "conversation. Implementations must supply their own filtered query.");
    }

    /// <summary>
    /// #3660 acceptance criterion 5: the concrete stores shipped in the Conversations assembly all
    /// declare the method themselves, so parity is observed rather than assumed.
    /// </summary>
    [Fact]
    public void EveryConcreteConversationStore_DeclaresThePendingCheckpointQuery()
    {
        var implementations = typeof(BotNexus.Gateway.Conversations.InMemoryConversationStore).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IConversationStore).IsAssignableFrom(t))
            .ToList();

        implementations.Count.ShouldBeGreaterThanOrEqualTo(3,
            "non-vacuity: expected the File, InMemory and Sqlite stores to be discovered");

        var missing = implementations
            .Where(t => t.GetMethod(nameof(IConversationStore.GetPendingAskUserCheckpointsAsync)) is null)
            .Select(t => t.Name)
            .ToArray();

        missing.ShouldBeEmpty(
            "#3660: every IConversationStore implementation must declare " +
            "GetPendingAskUserCheckpointsAsync with equivalent semantics. Missing: " +
            string.Join(", ", missing));
    }

    /// <summary>
    /// Matches a call to <c>ListAsync</c> on the injected conversation store. Deliberately
    /// permissive about the receiver name so renaming the constructor parameter does not create a
    /// hole in the fence.
    /// </summary>
    private static readonly Regex ListAsyncCallPattern = new(
        @"\bconversationStore\s*\.\s*ListAsync\s*\(|\b_?[Cc]onversation[Ss]tore\s*\.\s*ListAsync\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
