using Microsoft.JSInterop;
using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// What the browser will currently let the portal do about desktop notifications.
/// </summary>
public sealed class DesktopNotificationStatus
{
    /// <summary>False on a browser with no Notification API at all.</summary>
    [JsonPropertyName("supported")] public bool Supported { get; set; }

    /// <summary>The browser permission: default, granted, denied, or unsupported.</summary>
    [JsonPropertyName("permission")] public string Permission { get; set; } = "default";

    /// <summary>The user's own opt-in for THIS portal, kept in the browser beside the permission.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }

    /// <summary>
    /// Whether the page is a secure context. Plain http to anything but localhost is not, and a
    /// browser reports permission as denied there regardless of what the user does.
    /// </summary>
    [JsonPropertyName("secure")] public bool Secure { get; set; }

    /// <summary>The origin serving the portal, so a diagnosis can name the address at fault.</summary>
    [JsonPropertyName("origin")] public string? Origin { get; set; }

    /// <summary>Coarse browser family - chrome, edge, firefox, safari or other.</summary>
    [JsonPropertyName("browser")] public string? Browser { get; set; }

    /// <summary>Whether this browser has a service worker and a push manager at all.</summary>
    [JsonPropertyName("pushSupported")] public bool PushSupported { get; set; }

    /// <summary>
    /// Whether this browser currently holds a push subscription, which is what lets a notification
    /// arrive with the portal closed.
    /// </summary>
    [JsonPropertyName("pushSubscribed")] public bool PushSubscribed { get; set; }

    /// <summary>Toasts will actually be raised: supported, permitted, and opted in.</summary>
    [JsonIgnore]
    public bool IsActive => Supported && Enabled
        && string.Equals(Permission, "granted", StringComparison.Ordinal);

    /// <summary>The user has not answered the prompt yet, so it is still worth offering.</summary>
    [JsonIgnore]
    public bool CanAsk => Supported && string.Equals(Permission, "default", StringComparison.Ordinal);

    /// <summary>
    /// Permission was refused. This is a dead end from script - the browser will never show the
    /// prompt again - so the UI has to say so rather than offer a button that cannot work.
    /// </summary>
    [JsonIgnore]
    public bool IsBlocked => Supported && string.Equals(Permission, "denied", StringComparison.Ordinal);

    /// <summary>
    /// The permission is denied because the portal is not served over a secure connection, not
    /// because anyone refused it.
    /// </summary>
    /// <remarks>
    /// Worth telling apart from an ordinary denial, because the remedies are opposites: this one
    /// cannot be fixed in browser settings at all, and sending someone there to try is worse than
    /// saying nothing. It needs HTTPS, or reaching the gateway on localhost.
    /// </remarks>
    [JsonIgnore]
    public bool NeedsSecureContext => Supported && !Secure;
}

/// <summary>
/// Raises OS-level notifications for gateway notifications, via the browser's Notification API.
/// </summary>
/// <remarks>
/// Every call here is guarded. This is driven from the banner, which renders on every page, and it
/// runs against a browser API that is absent on some clients, permission-gated on all of them, and
/// unavailable entirely during prerender. A failure to raise a toast is not a reason for the
/// portal to stop working, so the failure mode throughout is "no toast", never an exception.
/// </remarks>
public sealed class DesktopNotifier
{
    private readonly IJSRuntime _js;

    public DesktopNotifier(IJSRuntime js) => _js = js;

    /// <summary>
    /// Reads the current permission, opt-in and push subscription without prompting for anything.
    /// </summary>
    public Task<DesktopNotificationStatus> GetStatusAsync() =>
        CallAsync("botnexusDesktopNotifications.statusWithPush");

    /// <summary>
    /// Subscribes this browser to push, so notifications arrive with the portal closed.
    /// </summary>
    /// <remarks>
    /// Best-effort on top of the permission. Push needs a service worker, which needs a secure
    /// context, and Safari adds conditions of its own - so a false here is ordinary rather than
    /// exceptional, and simply means alerts work only while the portal is open.
    /// </remarks>
    public async Task<bool> EnablePushAsync()
    {
        try
        {
            return await _js.InvokeAsync<bool>("botnexusDesktopNotifications.enablePush");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Drops this browser's push subscription, telling the gateway on the way out.</summary>
    public async Task DisablePushAsync()
    {
        try
        {
            await _js.InvokeAsync<bool>("botnexusDesktopNotifications.disablePush");
        }
        catch
        {
            // The gateway prunes an endpoint the push service reports as gone, so a failure here
            // is self-healing rather than a leak.
        }
    }

    /// <summary>
    /// Prompts for permission. MUST be called from a user gesture: a prompt raised on load is
    /// ignored by Chrome and refused by Safari, and a denial can never be re-asked from script.
    /// </summary>
    public Task<DesktopNotificationStatus> RequestAsync() =>
        CallAsync("botnexusDesktopNotifications.request");

    /// <summary>Turns toasts on or off for this browser, leaving the browser permission alone.</summary>
    public Task<DesktopNotificationStatus> SetEnabledAsync(bool enabled) =>
        CallAsync("botnexusDesktopNotifications.setEnabled", enabled);

    /// <summary>
    /// Hands the notification centre to the JS side so a clicked toast routes inside the running
    /// app instead of reloading it. Safe to skip: a click still works without it.
    /// </summary>
    public async Task RegisterAsync(object componentReference)
    {
        try
        {
            await _js.InvokeVoidAsync("botnexusDesktopNotifications.register", componentReference);
        }
        catch
        {
            // Prerendering, or the script did not load. The toast click falls back to a reload.
        }
    }

    /// <summary>Releases the reference when the notification centre goes away.</summary>
    public async Task UnregisterAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("botnexusDesktopNotifications.unregister");
        }
        catch
        {
            // Nothing to release, or the page is already going away.
        }
    }

    /// <summary>
    /// Raises a toast for one notification, and reports what the browser did with it.
    /// </summary>
    /// <returns>
    /// shown, suppressed-visible (the portal is in front of the user, so the badge already said
    /// it), inactive (unsupported, unpermitted or opted out), failed, or unavailable when JS
    /// could not be reached at all.
    /// </returns>
    public async Task<string> ShowAsync(string id, string title, string? body, string? link)
    {
        try
        {
            return await _js.InvokeAsync<string>(
                "botnexusDesktopNotifications.show", id, title, body, link)
                ?? "unavailable";
        }
        catch
        {
            return "unavailable";
        }
    }

    private async Task<DesktopNotificationStatus> CallAsync(string identifier, params object?[] args)
    {
        try
        {
            // A Loose JS interop mock returns null rather than a status; treat that the same as a
            // browser without the API, which is what a missing script actually means.
            return await _js.InvokeAsync<DesktopNotificationStatus?>(identifier, args)
                ?? new DesktopNotificationStatus { Supported = false, Permission = "unsupported" };
        }
        catch
        {
            return new DesktopNotificationStatus { Supported = false, Permission = "unsupported" };
        }
    }
}
