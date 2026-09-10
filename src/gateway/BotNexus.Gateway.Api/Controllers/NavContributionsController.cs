using BotNexus.Domain.Text;
using BotNexus.Gateway.Abstractions.Extensions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Left-nav entries contributed by loaded extensions, so an extension that serves a UI can appear
/// in the portal sidebar without the portal being edited to know about it.
/// </summary>
/// <remarks>
/// Everything returned here originates in a third-party manifest and is rendered by the portal
/// into an anchor. It is therefore validated on the way out rather than trusted: a contributed
/// entry is dropped if it cannot be rendered safely, and a bad entry never takes the rest of the
/// list down with it.
/// </remarks>
[ApiController]
[Route("api/nav")]
public sealed class NavContributionsController : ControllerBase
{
    /// <summary>Longest label rendered; anything beyond this is a layout attack, not a name.</summary>
    private const int MaxLabelLength = 40;

    private readonly IExtensionLoader _extensionLoader;

    /// <summary>Initialises the controller over the extension runtime registry.</summary>
    /// <param name="extensionLoader">Loaded-extension registry.</param>
    public NavContributionsController(IExtensionLoader extensionLoader) =>
        _extensionLoader = extensionLoader;

    /// <summary>
    /// Lists every valid nav entry contributed by a loaded extension, ordered by declared order
    /// then id so the sidebar is stable across restarts.
    /// </summary>
    [HttpGet("contributions")]
    [ProducesResponseType(typeof(IReadOnlyList<NavContributionResponse>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<NavContributionResponse>> Contributions()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<NavContributionResponse>();

        foreach (var extension in _extensionLoader.GetLoaded())
        {
            foreach (var nav in extension.Nav)
            {
                var projected = Project(nav, extension.ExtensionId);
                if (projected is null)
                {
                    continue;
                }

                // First declaration of an id wins. Two extensions claiming one nav key is a
                // conflict the portal cannot resolve, and rendering both would produce a
                // duplicated sidebar entry that navigates to whichever won a race.
                if (seen.Add(projected.Id))
                {
                    entries.Add(projected);
                }
            }
        }

        return Ok(entries
            .OrderBy(e => e.Order)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToArray());
    }

    /// <summary>
    /// Validates one declared entry, returning <c>null</c> when it cannot be rendered safely.
    /// </summary>
    internal static NavContributionResponse? Project(ExtensionNavEntry nav, string extensionId)
    {
        if (nav is null || !IsSafeKey(nav.Id) || string.IsNullOrWhiteSpace(nav.Label))
        {
            return null;
        }

        if (!IsSafePath(nav.Path))
        {
            return null;
        }

        // SafeTruncate, not raw slicing (#2883): the label comes from a plugin's manifest, so a
        // cut at MaxLabelLength can land between the halves of a surrogate pair and emit a lone
        // surrogate into the nav payload.
        var label = TextTruncation.SafeTruncate(nav.Label.Trim(), MaxLabelLength) ?? string.Empty;

        return new NavContributionResponse(
            nav.Id.Trim().ToLowerInvariant(),
            label,
            nav.Path.Trim(),
            IsSafeKey(nav.Icon) ? nav.Icon!.Trim().ToLowerInvariant() : null,
            nav.Order,
            nav.External,
            nav.FullPage,
            extensionId);
    }

    /// <summary>
    /// A renderable path is site-relative and nothing else.
    /// </summary>
    /// <remarks>
    /// The portal puts this straight into an <c>href</c>. Requiring a leading <c>/</c> rejects
    /// <c>javascript:</c> and <c>data:</c> URIs outright, and rejecting a second leading slash
    /// closes the protocol-relative <c>//evil.example</c> case, which a naive "starts with /"
    /// check would happily send a user to.
    /// </remarks>
    private static bool IsSafePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var trimmed = path.Trim();
        return trimmed.StartsWith('/')
            && !trimmed.StartsWith("//", StringComparison.Ordinal)
            && !trimmed.Contains('\\', StringComparison.Ordinal);
    }

    /// <summary>
    /// Keys and icon names are lowercase kebab-case. Constraining the icon to a name - never
    /// markup - is what stops a marketplace extension injecting SVG into the portal DOM.
    /// </summary>
    private static bool IsSafeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
        {
            return false;
        }

        foreach (var c in value.Trim())
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>One contributed nav entry as the portal consumes it.</summary>
/// <param name="Id">Stable nav key.</param>
/// <param name="Label">Sidebar text.</param>
/// <param name="Path">Site-relative path to navigate to.</param>
/// <param name="Icon">Portal icon name, or <c>null</c> to let the portal choose a default.</param>
/// <param name="Order">Sort position among nav entries.</param>
/// <param name="External">Whether the path is served outside the Blazor router.</param>
/// <param name="FullPage">Whether the view replaces the window instead of being hosted in the portal.</param>
/// <param name="ExtensionId">Extension that contributed the entry, for attribution and debugging.</param>
public sealed record NavContributionResponse(
    string Id,
    string Label,
    string Path,
    string? Icon,
    int Order,
    bool External,
    bool FullPage,
    string ExtensionId);
