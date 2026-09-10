using BotNexus.Gateway.Notifications.Push;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Lets a native iOS app register the device token APNs gave it.
/// </summary>
/// <remarks>
/// The native counterpart to the web push subscribe endpoints. An app calls register on every
/// launch - iOS re-issues tokens without warning, and re-registering an unchanged one is expected
/// and cheap.
/// </remarks>
[ApiController]
[Route("api/notifications/apns")]
public sealed class ApnsDevicesController(
    IApnsDeviceStore store,
    ApnsOptions options) : ControllerBase
{
    /// <summary>APNs device tokens are 32 bytes, hex-encoded. Newer tokens may be longer.</summary>
    private const int MinTokenLength = 64;
    private const int MaxTokenLength = 200;

    private readonly IApnsDeviceStore _store = store;
    private readonly ApnsOptions _options = options;

    /// <summary>
    /// Whether this gateway can push to iOS at all, so an app can say so rather than registering
    /// into a void and waiting for notifications that will never come.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(ApnsStatusResponse), StatusCodes.Status200OK)]
    public ActionResult<ApnsStatusResponse> Status() =>
        Ok(new ApnsStatusResponse
        {
            Configured = _options.IsConfigured,
            BundleId = _options.IsConfigured ? _options.BundleId : null,
        });

    /// <summary>Registers a device token, or refreshes one already held.</summary>
    /// <param name="request">The token and the environment that minted it.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("register")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Register(
        [FromBody] ApnsRegisterRequest request,
        CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.DeviceToken))
            return BadRequest(new { error = "deviceToken is required." });

        var token = request.DeviceToken.Trim();

        // Checked on the way in rather than at send time. A malformed token is accepted by APNs
        // with a 400 on every future notification, and the app that sent it would go on believing
        // it was registered.
        if (token.Length is < MinTokenLength or > MaxTokenLength || !IsHex(token))
            return BadRequest(new { error = "deviceToken must be a hex-encoded APNs device token." });

        // The environment decides which Apple host the token is valid against, and the two are not
        // interchangeable - a sandbox token sent to production is refused as BadDeviceToken, which
        // reads like a bad token rather than the wrong address. Guessing it would be worse than
        // refusing.
        var environment = ApnsEnvironment.Normalise(request.Environment);

        if (environment is null)
            return BadRequest(new { error = "environment must be 'sandbox' or 'production'." });

        await _store.SaveAsync(
            new ApnsDevice
            {
                DeviceToken = token,
                Environment = environment,
                DeviceName = string.IsNullOrWhiteSpace(request.DeviceName)
                    ? null
                    : request.DeviceName.Trim()[..Math.Min(request.DeviceName.Trim().Length, 128)],
            },
            ct);

        return NoContent();
    }

    /// <summary>Forgets a device. Idempotent.</summary>
    /// <param name="request">The token to forget.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("unregister")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Unregister(
        [FromBody] ApnsUnregisterRequest request,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(request?.DeviceToken))
            await _store.RemoveAsync(request.DeviceToken.Trim(), ct);

        return NoContent();
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }

        return true;
    }
}

/// <summary>Whether the gateway is set up to push to iOS.</summary>
public sealed class ApnsStatusResponse
{
    /// <summary>False when gateway:apns is incomplete; registering would achieve nothing.</summary>
    [JsonPropertyName("configured")] public required bool Configured { get; init; }

    /// <summary>The bundle id pushes are sent for, so an app can check it matches its own.</summary>
    [JsonPropertyName("bundleId")] public string? BundleId { get; init; }
}

/// <summary>A device token as iOS handed it to the app.</summary>
public sealed class ApnsRegisterRequest
{
    /// <summary>Hex-encoded APNs device token.</summary>
    [JsonPropertyName("deviceToken")] public string? DeviceToken { get; init; }

    /// <summary>Either <c>sandbox</c> or <c>production</c>, matching the build.</summary>
    [JsonPropertyName("environment")] public string? Environment { get; init; }

    /// <summary>Optional label for diagnosis, such as the device name.</summary>
    [JsonPropertyName("deviceName")] public string? DeviceName { get; init; }
}

/// <summary>The device token to forget.</summary>
public sealed class ApnsUnregisterRequest
{
    /// <summary>Hex-encoded APNs device token.</summary>
    [JsonPropertyName("deviceToken")] public string? DeviceToken { get; init; }
}
