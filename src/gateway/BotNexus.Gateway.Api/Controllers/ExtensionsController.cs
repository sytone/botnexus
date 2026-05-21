using BotNexus.Gateway.Abstractions.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.IO;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// REST API for inspecting loaded runtime extensions.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class ExtensionsController : ControllerBase
{
    private readonly IExtensionLoader _extensionLoader;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExtensionsController"/> class.
    /// </summary>
    /// <param name="extensionLoader">The extension loader runtime registry.</param>
    public ExtensionsController(IExtensionLoader extensionLoader) => _extensionLoader = extensionLoader;

    /// <summary>
    /// Lists loaded extensions and their declared extension types.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ExtensionResponse>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<ExtensionResponse>> List()
    {
        var responses = _extensionLoader.GetLoaded()
            .SelectMany(extension => (extension.ExtensionTypes.Count == 0 ? ["unknown"] : extension.ExtensionTypes)
                .Select(type => new ExtensionResponse(
                    extension.Name,
                    extension.Version,
                    type,
                    Path.GetFileName(extension.EntryAssemblyPath))))
            .ToArray();

        return Ok(responses);
    }

    /// <summary>
    /// Lists loaded extensions with full manifest details including config schema.
    /// </summary>
    [HttpGet("details")]
    [ProducesResponseType(typeof(IReadOnlyList<ExtensionDetailResponse>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<ExtensionDetailResponse>> Details()
    {
        var responses = _extensionLoader.GetLoaded()
            .Select(ext => new ExtensionDetailResponse(
                ext.ExtensionId,
                ext.Name,
                ext.Version,
                ext.Enabled,
                ext.ExtensionTypes,
                ext.RegisteredServices,
                ext.ConfigSchema,
                Path.GetFileName(ext.EntryAssemblyPath)))
            .ToArray();

        return Ok(responses);
    }
}

/// <summary>
/// Loaded extension response payload (legacy flat format).
/// </summary>
/// <param name="Name">The extension display name.</param>
/// <param name="Version">The extension version.</param>
/// <param name="Type">The extension type from the manifest.</param>
/// <param name="AssemblyPath">The resolved entry assembly filename.</param>
public sealed record ExtensionResponse(
    string Name,
    string Version,
    string Type,
    string AssemblyPath);

/// <summary>
/// Loaded extension detail response payload — includes config schema and enabled status.
/// </summary>
/// <param name="Id">The extension ID.</param>
/// <param name="Name">The extension display name.</param>
/// <param name="Version">The extension version.</param>
/// <param name="Enabled">Whether the extension is enabled per its manifest.</param>
/// <param name="ExtensionTypes">Extension type identifiers declared in the manifest.</param>
/// <param name="RegisteredServices">Service contract names registered by this extension.</param>
/// <param name="ConfigSchema">Configuration field schema declared by this extension.</param>
/// <param name="AssemblyFileName">The entry assembly filename.</param>
public sealed record ExtensionDetailResponse(
    string Id,
    string Name,
    string Version,
    bool Enabled,
    IReadOnlyList<string> ExtensionTypes,
    IReadOnlyList<string> RegisteredServices,
    IReadOnlyList<ExtensionConfigFieldSchema> ConfigSchema,
    string AssemblyFileName);
