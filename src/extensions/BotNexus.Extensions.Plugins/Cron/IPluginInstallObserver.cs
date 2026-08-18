namespace BotNexus.Extensions.Plugins.Cron;

/// <summary>
/// Notified after a plugin install has fully succeeded, so platform state that only becomes
/// meaningful once at least one plugin exists can be created at that moment.
/// </summary>
/// <remarks>
/// <para>
/// This exists so <see cref="PluginUpdateCronProvisioner"/> can hang off install without the
/// lifecycle manager knowing anything about cron. The dependency points the right way: plugins
/// know they have an install event, and cron knows what to do with one.
/// </para>
/// <para>
/// Only a SUCCESSFUL install notifies. A failed install has materialised nothing, so provisioning
/// a periodic update job for it would schedule work over an empty set forever.
/// </para>
/// </remarks>
public interface IPluginInstallObserver
{
    /// <summary>Called after a plugin has been installed and its record persisted.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task OnPluginInstalledAsync(CancellationToken cancellationToken = default);
}
