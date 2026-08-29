using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>Payload for creating or overwriting a secret.</summary>
/// <param name="Value">The complete new value. There is no partial or merge form.</param>
public sealed record SecretWriteRequest(string Value);

/// <summary>
/// REST API over the file-per-secret store (#3528): <b>list</b>, <b>add/overwrite</b>, <b>delete</b>.
/// </summary>
/// <remarks>
/// <para><b>Why there is no GET-by-key.</b> The write-only contract is the feature, not a gap. The
/// existing <c>ConfigSecretMerge</c> scheme has to keep a read/restore channel open so a redacted
/// <c>***</c> can round-trip back to the real value on save; that channel is precisely what this
/// store does not have. An overwrite requires the operator to paste the full value, and recovering
/// an existing one requires filesystem access on the host. Adding a read action here would silently
/// convert a documented security property into a convenience feature - so
/// <c>SecretsControllerTests</c> asserts by reflection that no action returns secret content.</para>
/// <para><b>Why list carries no content-derived field.</b> Not the value, not a prefix, not a masked
/// form (which leaks length), not a hash (an offline oracle for short secrets). Everything in
/// <see cref="SecretDescriptor"/> comes from the file name or its filesystem metadata.</para>
/// </remarks>
[ApiController]
[Route("api/secrets")]
public sealed class SecretsController(IFileSecretStore store, ILogger<SecretsController> logger) : ControllerBase
{
    private readonly IFileSecretStore _store = store;
    private readonly ILogger<SecretsController> _logger = logger;

    /// <summary>Lists stored secret keys with their timestamps and size. Never returns a value.</summary>
    /// <returns>Metadata for every stored secret, ordered by key.</returns>
    [HttpGet]
    public ActionResult<IReadOnlyList<SecretDescriptor>> List() => Ok(_store.List());

    /// <summary>
    /// Creates or overwrites a secret. An existing key is replaced wholesale with the supplied
    /// value; no part of the previous value is read, returned, or merged.
    /// </summary>
    /// <param name="key">The secret key, which becomes the file name verbatim.</param>
    /// <param name="request">The complete new value.</param>
    /// <returns>The metadata for the written secret.</returns>
    [HttpPut("{key}")]
    public ActionResult<SecretDescriptor> Set(string key, [FromBody] SecretWriteRequest request)
    {
        if (request is null)
            return BadRequest(new { error = "A value is required." });

        try
        {
            var existed = _store.Exists(key);
            var descriptor = _store.Set(key, request.Value);

            // The key is logged; the value never is. A secret that reaches the log has escaped the
            // owner-only file the whole design exists to keep it in.
            _logger.LogInformation(
                "Secret {SecretKey} {SecretAction} via API.", key, existed ? "overwritten" : "created");
            return Ok(descriptor);
        }
        catch (InvalidSecretKeyException ex)
        {
            _logger.LogWarning("Rejected secret write for invalid key {SecretKey}.", key);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Deletes a secret. The key stops appearing in the list immediately.</summary>
    /// <param name="key">The secret key to delete.</param>
    /// <returns>No content when deleted; 404 when the key does not exist.</returns>
    [HttpDelete("{key}")]
    public IActionResult Delete(string key)
    {
        try
        {
            if (!_store.Delete(key))
                return NotFound(new { error = $"No secret named '{key}'." });

            _logger.LogInformation("Secret {SecretKey} deleted via API.", key);
            return NoContent();
        }
        catch (InvalidSecretKeyException ex)
        {
            _logger.LogWarning("Rejected secret delete for invalid key {SecretKey}.", key);
            return BadRequest(new { error = ex.Message });
        }
    }
}
