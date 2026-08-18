using BotNexus.Extensions.Plugins.Lifecycle;

namespace BotNexus.Extensions.Plugins.Cron;

/// <summary>
/// The narrow slice of plugin lifecycle the <c>plugin-update</c> cron action needs: enumerate
/// what is installed, and update one plugin by name.
/// </summary>
/// <remarks>
/// The action depends on this rather than on <see cref="PluginLifecycleManager"/> directly so the
/// scheduled path cannot reach install or remove. A cron job that could uninstall a plugin is a
/// different and much larger blast radius than one that updates it, and the type system is a
/// cheaper guarantee of that than a code review.
/// </remarks>
public interface IPluginUpdateService
{
    /// <summary>Every plugin currently recorded as installed.</summary>
    IReadOnlyList<InstalledPlugin> List();

    /// <summary>
    /// Re-resolves one plugin's source and replaces its content when the source has moved.
    /// </summary>
    /// <param name="name">Plugin identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PluginOperationResult> UpdateAsync(string name, CancellationToken cancellationToken = default);
}
