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
    /// <returns>Loaded extension metadata for each extension type.</returns>
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
}

/// <summary>
/// Loaded extension response payload.
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
