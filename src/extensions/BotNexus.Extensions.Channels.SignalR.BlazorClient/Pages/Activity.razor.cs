using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;

/// <summary>
/// A single entry in the Activity sub-navigation shell (#2897). <paramref name="Key"/> is the
/// stable, URL-bearing identity; <paramref name="Label"/> is display-only. Nothing may derive a
/// link target from an entry's position in the list.
/// </summary>
/// <param name="Key">Stable route key, e.g. <c>overview</c> for <c>/activity/overview</c>.</param>
/// <param name="Label">Human-readable label rendered in the sub-navigation control.</param>
public sealed record ActivitySection(string Key, string Label);

/// <summary>
/// Activity page routing and sub-navigation shell (#2897).
/// </summary>
/// <remarks>
/// This is deliberately a SHELL: it adds <c>@page "/activity/{Section}"</c> alongside the existing
/// parameterless route (mirroring the <c>Configuration.razor</c> precedent) and renders a
/// sub-navigation control, but introduces no subsection content, no new endpoint and no new query.
/// It exists so the first real Activity subsection (#2643) is a content change rather than a
/// routing change. The parameterless <c>/activity</c> route still selects no subsection and renders
/// the same dashboard it rendered before.
/// </remarks>
public partial class Activity : IDisposable
{
    /// <summary>
    /// Sub-navigation entries known to the shell. <c>costs</c> is the first real subsection
    /// (#2898); further subsections are appended by the issues that introduce them.
    /// </summary>
    public static readonly IReadOnlyList<ActivitySection> DefaultSections =
    [
        new("overview", "Overview"),
        new("costs", "Cost")
    ];

    /// <summary>
    /// Section key from the route (e.g. <c>/activity/overview</c>). Null on the parameterless route,
    /// which selects no subsection. An unknown or removed key falls back to the default view with a
    /// non-fatal notice rather than an error page.
    /// </summary>
    [Parameter] public string? Section { get; set; }

    /// <summary>
    /// Section registry override. Present so tests can pin that hrefs follow the section KEY across
    /// a reordering; production rendering uses <see cref="DefaultSections"/>.
    /// </summary>
    [Parameter] public IReadOnlyList<ActivitySection>? Sections { get; set; }

    private IReadOnlyList<ActivitySection> KnownSections => Sections ?? DefaultSections;

    /// <summary>
    /// The selected section key, or null when the route carries none or carries an unknown one.
    /// </summary>
    public string? ActiveSection =>
        string.IsNullOrWhiteSpace(Section)
            ? null
            : KnownSections.FirstOrDefault(
                s => string.Equals(s.Key, Section, StringComparison.OrdinalIgnoreCase))?.Key;

    /// <summary>
    /// Non-fatal message shown when the route named a section the shell does not know about.
    /// </summary>
    public string? UnknownSectionMessage =>
        !string.IsNullOrWhiteSpace(Section) && ActiveSection is null
            ? $"Unknown activity section '{Section}' - showing the default view."
            : null;

    /// <summary>
    /// Builds a section link from its KEY, never from its display index, so reordering the registry
    /// cannot rewrite an existing bookmark (#2897 AC4).
    /// </summary>
    public static string SectionHref(string key) => $"/activity/{key}";

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        PortalLoad.OnReadyChanged += HandleReadyChanged;
    }

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        if (!PortalLoad.IsReady && !PortalLoad.IsLoading)
        {
            try
            {
                var baseUri = new Uri(Nav.BaseUri);
                var hubUrl = new Uri(baseUri, "/hub/gateway").ToString();
                PortalLoad.ClientKind = "desktop";
                await PortalLoad.InitializeAsync(hubUrl);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Portal initialization failed: {ex.Message}");
            }
        }
    }

    private void HandleReadyChanged() => _ = InvokeAsync(StateHasChanged);

    /// <inheritdoc />
    public void Dispose()
    {
        PortalLoad.OnReadyChanged -= HandleReadyChanged;
    }
}
