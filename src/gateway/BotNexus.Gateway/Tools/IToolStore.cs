using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Tools;

/// <summary>
/// Server-side persistence for user-defined portal <see cref="ToolDefinition"/> records.
/// Implementations must survive a gateway restart so tools roam with the user.
/// </summary>
public interface IToolStore
{
    /// <summary>Ensures the backing schema exists. Safe to call repeatedly.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Returns all tools ordered by <see cref="ToolDefinition.Order"/> ascending.</summary>
    Task<IReadOnlyList<ToolDefinition>> ListAsync(CancellationToken ct = default);

    /// <summary>Returns the tool with the given id, or <c>null</c> when it does not exist.</summary>
    Task<ToolDefinition?> GetAsync(ToolId id, CancellationToken ct = default);

    /// <summary>Persists a new tool and returns the stored record.</summary>
    Task<ToolDefinition> CreateAsync(ToolDefinition tool, CancellationToken ct = default);

    /// <summary>Upserts an existing tool and returns the stored record.</summary>
    Task<ToolDefinition> UpdateAsync(ToolDefinition tool, CancellationToken ct = default);

    /// <summary>Removes the tool with the given id. Idempotent.</summary>
    Task DeleteAsync(ToolId id, CancellationToken ct = default);
}
