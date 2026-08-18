using System.Text.Json.Nodes;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Rewrites pre-#2854 provider entries into the per-capability shape, in place, as a pure document
/// transform (Jon's review on PR #3277).
///
/// <para><b>Why a migration exists at all when the flat fields still work.</b> #2854 shipped a
/// compatibility path — <c>ProviderConfig.Effective*</c> reads nested-then-flat — so nothing is
/// *broken* without this. But a compatibility path that never converts anything is permanent: every
/// reader of the config, every doc example and every future capability has to keep knowing both
/// shapes forever, and the deprecation warning becomes furniture an operator learns to ignore. The
/// migration makes the new shape the shape that is actually on disk, so the legacy branch has a
/// path to eventually being deleted rather than being load-bearing indefinitely.</para>
///
/// <para><b>This also migrates the SQLite config store, without touching it.</b> Store keys are not
/// an independent vocabulary — <see cref="Shadow.ConfigDocumentFlattener"/> derives them by walking
/// the JSON document, so <c>providers.foo.defaultModel</c> becomes <c>providers.foo.chat.defaultModel</c>
/// in the store for exactly the same reason it does in the file: the document changed shape. Writing
/// a second, store-specific key-rename migration would be a second implementation of one rule, free
/// to disagree with the first. See <c>ProviderConfigMigrationTests.Migrate_AlsoMigratesFlattenedStoreKeys</c>,
/// which pins that coupling so a future store that stops deriving keys from the document fails loudly
/// instead of silently keeping stale keys.</para>
///
/// <para><b>Idempotent by construction.</b> A flat field is moved only when the nested slot is absent,
/// and the flat key is removed as it is moved. Running the transform twice is therefore identical to
/// running it once, which matters because it runs on every gateway start, not once at an upgrade
/// boundary.</para>
/// </summary>
public static class ProviderConfigMigration
{
    /// <summary>
    /// The flat provider-level fields that mean "chat", mapped to their name inside the <c>chat</c>
    /// object. Every entry is a rename into a nested object, never a value change.
    /// </summary>
    /// <remarks>
    /// Deliberately spelled out rather than derived by reflection over <c>ProviderChatConfig</c>: the
    /// set of fields that must MOVE is a historical fact about the pre-#2854 document, not a property
    /// of the current type. A reflection-driven list would silently start moving any future
    /// chat-config field that never existed at the provider level, inventing a migration for a legacy
    /// shape that never shipped.
    /// </remarks>
    private static readonly (string Flat, string Nested)[] ChatFieldMap =
    {
        ("defaultModel", "defaultModel"),
        ("models", "models"),
        ("api", "api"),
        ("input", "input"),
        ("reasoning", "reasoning"),
        ("supportsExtraHighThinking", "supportsExtraHighThinking"),
        ("supportsExtendedContextWindow", "supportsExtendedContextWindow"),
        ("contextWindow", "contextWindow"),
    };

    /// <summary>
    /// Migrates every provider entry in <paramref name="root"/> to the per-capability shape.
    /// </summary>
    /// <param name="root">The raw configuration document, mutated in place. Null is a no-op.</param>
    /// <returns>
    /// The names of the provider entries that were actually changed. Empty means the document was
    /// already in the new shape — which is the normal steady state, and is why the caller must not
    /// treat "nothing migrated" as a failure.
    /// </returns>
    public static IReadOnlyList<string> Migrate(JsonObject? root)
    {
        var migrated = new List<string>();

        if (root?["providers"] is not JsonObject providers)
            return migrated;

        foreach (var (providerName, providerNode) in providers)
        {
            if (providerNode is JsonObject provider && MigrateProvider(provider))
                migrated.Add(providerName);
        }

        return migrated;
    }

    /// <summary>Migrates one provider entry. Returns whether anything moved.</summary>
    private static bool MigrateProvider(JsonObject provider)
    {
        var moved = false;

        foreach (var (flat, nested) in ChatFieldMap)
        {
            if (!provider.TryGetPropertyValue(flat, out var value))
                continue;

            var chat = provider["chat"] as JsonObject;

            // An explicit nested value always wins and the flat one is dropped. The operator wrote both;
            // the nested one is what the runtime already honours (Effective*), so leaving the flat key
            // behind would preserve a value that has no effect and reads like it does.
            if (chat is not null && chat.ContainsKey(nested))
            {
                provider.Remove(flat);
                moved = true;
                continue;
            }

            if (chat is null)
            {
                chat = new JsonObject();
                provider["chat"] = chat;
            }

            // Detach before re-parenting: a JsonNode belongs to exactly one parent, and assigning a
            // still-attached node throws rather than moving it.
            provider.Remove(flat);
            chat[nested] = value;
            moved = true;
        }

        return moved;
    }
}
