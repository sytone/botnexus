using System.Reflection;
using BotNexus.Gateway.Webhooks;
using BotNexus.Persistence.Seam.Tests.Harness;

namespace BotNexus.Persistence.Seam.Tests.Webhooks;

/// <summary>
/// Executable write-classification inventory for webhook registrations and runs (issue #3327,
/// acceptance clause 5).
/// </summary>
public sealed class WebhookWriteInventoryTests
{
    public static readonly IReadOnlyList<AggregateWriteEntry> RegistrationInventory =
    [
        new("webhook registrations", nameof(IWebhookRegistrationStore.CreateAsync), WriteClassification.Create,
            "one new webhook_registrations row",
            "Primary-key uniqueness rejects duplicate registration ids."),
        new("webhook registrations", nameof(IWebhookRegistrationStore.UpdateAsync), WriteClassification.NarrowPatch,
            "label, default_response_mode and enabled",
            "The update excludes the secret, agent, conversation pin, created timestamp and last-used bookkeeping."),
        new("webhook registrations", nameof(IWebhookRegistrationStore.TouchLastUsedAsync), WriteClassification.NarrowPatch,
            "last_used_at only",
            "Inbound bookkeeping cannot rewrite registration definition or conversation state."),
        new("webhook registrations", nameof(IWebhookRegistrationStore.DeleteAsync), WriteClassification.NarrowPatch,
            "one selected registration row",
            "Deletion is scoped by registration id and does not touch webhook run history."),
        new("webhook registrations", nameof(IWebhookRegistrationStore.TryPinConversationAsync), WriteClassification.CompareAndSwap,
            "pinned_conversation_id only",
            "The conditional update writes only while the pin is null and returns the winning value."),
    ];

    public static readonly IReadOnlyList<AggregateWriteEntry> RunInventory =
    [
        new("webhook runs", nameof(IWebhookRunStore.CreateAsync), WriteClassification.Create,
            "one new webhook_runs row",
            "Primary-key uniqueness rejects duplicate run ids."),
        new("webhook runs", nameof(IWebhookRunStore.UpdateAsync), WriteClassification.NarrowPatch,
            "conversation, session, status, timing, response and error columns of one run",
            "The conflict update excludes webhook identity, acceptance time, callback URL and agent-action intent."),
        new("webhook runs", nameof(IWebhookRunStore.PurgeOlderThanAsync), WriteClassification.NarrowPatch,
            "eligible terminal run rows older than the cutoff",
            "Predicate-scoped deletion excludes in-flight runs and never touches registrations."),
    ];

    [Fact]
    public void EveryRegistrationMutation_IsClassified()
        => AssertExactMutationSet<IWebhookRegistrationStore>(RegistrationInventory, nameof(IWebhookRegistrationStore.InitializeAsync), "Get", "List");

    [Fact]
    public void EveryRunMutation_IsClassified()
        => AssertExactMutationSet<IWebhookRunStore>(RunInventory, nameof(IWebhookRunStore.InitializeAsync), "Get", "List");

    [Fact]
    public void RegistrationDefinitionBookkeepingAndPin_HaveIndependentWriteShapes()
    {
        RegistrationInventory.Single(entry => entry.EntryPoint == nameof(IWebhookRegistrationStore.UpdateAsync))
            .Classification.ShouldBe(WriteClassification.NarrowPatch);
        RegistrationInventory.Single(entry => entry.EntryPoint == nameof(IWebhookRegistrationStore.TouchLastUsedAsync))
            .Classification.ShouldBe(WriteClassification.NarrowPatch);
        RegistrationInventory.Single(entry => entry.EntryPoint == nameof(IWebhookRegistrationStore.TryPinConversationAsync))
            .Classification.ShouldBe(WriteClassification.CompareAndSwap);
    }

    [Fact]
    public void WebhookStores_HaveNoFullReplaceWrite()
    {
        RegistrationInventory.ShouldNotContain(entry => entry.Classification == WriteClassification.FullReplace);
        RunInventory.ShouldNotContain(entry => entry.Classification == WriteClassification.FullReplace);
    }

    private static void AssertExactMutationSet<TStore>(
        IReadOnlyList<AggregateWriteEntry> inventory,
        string initializeMethod,
        params string[] readOnlyPrefixes)
    {
        var mutating = typeof(TStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.Name)
            .Where(name => name != initializeMethod)
            .Where(name => !readOnlyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        mutating.ShouldNotBeEmpty();
        var classified = inventory.Select(entry => entry.EntryPoint).ToArray();
        classified.ShouldNotBeEmpty();
        classified.ShouldBeUnique();
        classified.Order(StringComparer.Ordinal).ShouldBe(mutating.Order(StringComparer.Ordinal));
    }
}
