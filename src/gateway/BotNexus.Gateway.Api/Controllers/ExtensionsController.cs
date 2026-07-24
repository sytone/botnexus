using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Diagnostics;
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
    private readonly ExtensionBootReport _bootReport;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExtensionsController"/> class.
    /// </summary>
    /// <param name="extensionLoader">The extension loader runtime registry.</param>
    /// <param name="bootReport">The startup extension-load report used by the health endpoint.</param>
    public ExtensionsController(IExtensionLoader extensionLoader, ExtensionBootReport bootReport)
    {
        _extensionLoader = extensionLoader;
        _bootReport = bootReport;
    }

    /// <summary>
    /// Reports the outcome of the startup extension-load pass. Returns 200 with
    /// <c>status: ok</c> when every attempted extension loaded, or 503 with the actual
    /// per-extension load error (naming the missing or diverged assembly) when any failed.
    /// This is the boot smoke gate's assertion surface (#2220): it turns a silent
    /// extension-assembly-load regression - which previously left <c>/health</c> green and
    /// surfaced only as a generic timeout - into an explicit, named failure.
    /// </summary>
    [HttpGet("health")]
    [ProducesResponseType(typeof(ExtensionHealthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ExtensionHealthResponse), StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<ExtensionHealthResponse> Health()
    {
        var results = _bootReport.Results;
        var failed = results
            .Where(result => !result.Success)
            .Select(result => new ExtensionFailure(result.ExtensionId, result.Error ?? "unknown load failure"))
            .ToArray();
        var loadedCount = results.Count(result => result.Success);

        var response = new ExtensionHealthResponse(
            Status: failed.Length == 0 ? "ok" : "failed",
            LoadedCount: loadedCount,
            FailedCount: failed.Length,
            Failed: failed);

        return failed.Length == 0
            ? Ok(response)
            : StatusCode(StatusCodes.Status503ServiceUnavailable, response);
    }

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


/// <summary>
/// Health payload for the startup extension-load pass (GET /api/extensions/health).
/// </summary>
/// <param name="Status">"ok" when every attempted extension loaded; otherwise "failed".</param>
/// <param name="LoadedCount">Number of extensions that loaded successfully at boot.</param>
/// <param name="FailedCount">Number of extensions that failed to load at boot.</param>
/// <param name="Failed">The per-extension load failures, each naming the offending extension and its error.</param>
public sealed record ExtensionHealthResponse(
    string Status,
    int LoadedCount,
    int FailedCount,
    IReadOnlyList<ExtensionFailure> Failed);

/// <summary>
/// A single extension-load failure captured during gateway boot.
/// </summary>
/// <param name="Id">The extension ID that failed to load.</param>
/// <param name="Error">The actual load error (typically naming the missing or diverged assembly/type).</param>
public sealed record ExtensionFailure(string Id, string Error);
