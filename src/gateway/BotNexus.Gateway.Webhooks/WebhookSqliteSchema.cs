using BotNexus.Persistence.Sqlite;

namespace BotNexus.Gateway.Webhooks;

/// <summary>
/// Owns the schema version for the single webhook database shared by registration and run stores.
/// </summary>
internal static class WebhookSqliteSchema
{
    internal const int CurrentVersion = 1;

    internal static readonly SqliteSchemaMigration[] Migrations = [];
}
