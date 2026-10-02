using BotNexus.Agent.Providers.Copilot.Discovery;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Abstractions.Security;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>Read-only Copilot account quota surface, separate from observed provider rate limits.</summary>
[ApiController]
[Route("api/copilot/quota")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CopilotQuotaController(CopilotQuotaService service) : ControllerBase
{
    private readonly CopilotQuotaService _service = service;

    /// <summary>Gets the account's premium-interaction allowance, or no content if unavailable.</summary>
    [HttpGet]
    public async Task<IActionResult> GetQuota(CancellationToken cancellationToken)
    {
        // This is an account-wide resource. Agent-scoped and satellite keys must not read it.
        if (HttpContext?.Items.TryGetValue(GatewayAuthMiddleware.CallerIdentityItemKey, out var caller) != true ||
            caller is not GatewayCallerIdentity { IsAdmin: true })
            return Forbid();

        try
        {
            var quota = await _service.GetQuotaAsync(cancellationToken).ConfigureAwait(false);
            return quota is null ? NoContent() : Ok(quota);
        }
        catch (CopilotQuotaUnavailableException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Copilot account quota is temporarily unavailable." });
        }
    }
}

/// <summary>On-demand Copilot account quota retrieval with bounded caching and failure throttling.</summary>
public sealed class CopilotQuotaService(
    GatewayAuthManager authManager,
    CopilotDiscoveryClient discoveryClient,
    ILogger<CopilotQuotaService> logger,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan SuccessCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureCacheDuration = TimeSpan.FromMinutes(1);
    private readonly GatewayAuthManager _authManager = authManager;
    private readonly CopilotDiscoveryClient _discoveryClient = discoveryClient;
    private readonly ILogger<CopilotQuotaService> _logger = logger;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CopilotQuotaDto? _cached;
    private DateTimeOffset _cacheExpiresAt;
    private bool _lastFetchFailed;
    private string? _cachedCredential;

    /// <summary>Fetches the premium-interaction quota if configured, honoring short-lived cache entries.</summary>
    public async Task<CopilotQuotaDto?> GetQuotaAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var token = await _authManager.GetCopilotOAuthTokenAsync(cancellationToken).ConfigureAwait(false);
                if (!string.Equals(_cachedCredential, token, StringComparison.Ordinal))
                {
                    _cachedCredential = token;
                    _cached = null;
                    _lastFetchFailed = false;
                    _cacheExpiresAt = default;
                }
                if (_cacheExpiresAt > _timeProvider.GetUtcNow())
                {
                    if (_lastFetchFailed) throw new CopilotQuotaUnavailableException();
                    return _cached;
                }
                if (string.IsNullOrWhiteSpace(token))
                {
                    _cached = null;
                    _lastFetchFailed = false;
                    _cacheExpiresAt = _timeProvider.GetUtcNow().Add(SuccessCacheDuration);
                    return null;
                }

                var user = await _discoveryClient.GetUserAsync(token, cancellationToken).ConfigureAwait(false);
                var snapshot = user.QuotaSnapshots?.GetValueOrDefault("premium_interactions");
                if (snapshot is not null && !IsValidSnapshot(snapshot))
                    throw new InvalidOperationException("Copilot quota snapshot was malformed.");

                _cached = snapshot is null ? null : new CopilotQuotaDto
                {
                    QuotaId = "premium_interactions",
                    Entitlement = SafeCount(snapshot.Entitlement),
                    Remaining = SafeCount(snapshot.QuotaRemaining),
                    PercentRemaining = SafePercent(snapshot.PercentRemaining),
                    IsUnlimited = snapshot.Unlimited,
                    ResetDate = SafeDate(user.QuotaResetDate),
                    ObservedAtUtc = _timeProvider.GetUtcNow()
                };
                _lastFetchFailed = false;
                _cacheExpiresAt = _timeProvider.GetUtcNow().Add(SuccessCacheDuration);
                return _cached;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (CopilotQuotaUnavailableException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Copilot account quota lookup failed ({FailureType}).", ex.GetType().Name);
                _cached = null;
                _lastFetchFailed = true;
                _cacheExpiresAt = _timeProvider.GetUtcNow().Add(FailureCacheDuration);
                throw new CopilotQuotaUnavailableException();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsValidSnapshot(CopilotQuotaSnapshot snapshot) =>
        string.Equals(snapshot.QuotaId, "premium_interactions", StringComparison.Ordinal) &&
        double.IsFinite(snapshot.Entitlement) && snapshot.Entitlement >= 0 &&
        double.IsFinite(snapshot.QuotaRemaining) && snapshot.QuotaRemaining >= 0 &&
        double.IsFinite(snapshot.PercentRemaining) && snapshot.PercentRemaining is >= 0 and <= 100 &&
        (snapshot.Unlimited || snapshot.QuotaRemaining <= snapshot.Entitlement);

    private static int SafeCount(double value) => (int)Math.Clamp(Math.Floor(value), 0, int.MaxValue);
    private static double SafePercent(double value) => Math.Clamp(value, 0, 100);
    private static string? SafeDate(string? value) => value is { Length: <= 32 } && DateOnly.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out _) ? value : null;
}

/// <summary>Allow-listed public account-quota data; no raw upstream fields are serialized.</summary>
public sealed class CopilotQuotaDto
{
    /// <summary>The Copilot quota dimension.</summary>
    public string QuotaId { get; init; } = "premium_interactions";
    /// <summary>The account's total premium-interaction entitlement.</summary>
    public int Entitlement { get; init; }
    /// <summary>The premium-interaction allowance remaining at observation time.</summary>
    public int Remaining { get; init; }
    /// <summary>The provider-reported percentage remaining.</summary>
    public double PercentRemaining { get; init; }
    /// <summary>Whether Copilot reports this quota as unlimited.</summary>
    public bool IsUnlimited { get; init; }
    /// <summary>The provider-reported reset date, if valid.</summary>
    public string? ResetDate { get; init; }
    /// <summary>When the quota was observed.</summary>
    public DateTimeOffset ObservedAtUtc { get; init; }
}

/// <summary>Indicates that the upstream Copilot account quota could not be fetched.</summary>
public sealed class CopilotQuotaUnavailableException : Exception
{
    /// <summary>Creates a generic exception that carries no upstream details or credentials.</summary>
    public CopilotQuotaUnavailableException() : base("Copilot account quota is unavailable.") { }
}
