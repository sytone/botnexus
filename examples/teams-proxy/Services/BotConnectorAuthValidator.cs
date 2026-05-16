using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using BotNexus.TeamsProxy.Configuration;
using BotNexus.TeamsProxy.Models;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace BotNexus.TeamsProxy.Services;

public sealed class BotConnectorAuthValidator
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    private readonly ConfigurationManager<OpenIdConnectConfiguration> _configurationManager;
    private readonly IHostEnvironment _environment;
    private readonly JwtSecurityTokenHandler _handler = new();
    private readonly ILogger<BotConnectorAuthValidator> _logger;
    private readonly TeamsProxyOptions _options;

    public BotConnectorAuthValidator(
        IOptions<TeamsProxyOptions> options,
        IHostEnvironment environment,
        ILogger<BotConnectorAuthValidator> logger)
    {
        _options = options.Value;
        _environment = environment;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_options.BotOpenIdMetadataUrl))
        {
            throw new InvalidOperationException("TeamsProxy:BotOpenIdMetadataUrl must be configured.");
        }

        _configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            _options.BotOpenIdMetadataUrl,
            new OpenIdConnectConfigurationRetriever());
    }

    public async Task ValidateAsync(
        string? authorizationHeader,
        BotActivity activity,
        CancellationToken cancellationToken)
    {
        if (_options.AllowUnauthenticatedRequests)
        {
            if (!_environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "TeamsProxy:AllowUnauthenticatedRequests can only be enabled in Development.");
            }

            _logger.LogWarning("Skipping Bot Connector authentication because Development unauthenticated mode is enabled.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.BotClientId))
        {
            throw new InvalidOperationException("TeamsProxy:BotClientId must be configured.");
        }

        if (!AuthenticationHeaderValue.TryParse(authorizationHeader, out var parsedHeader)
            || !string.Equals(parsedHeader.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(parsedHeader.Parameter))
        {
            throw new BotAuthenticationException("Missing Bot Connector bearer token.");
        }

        var configuration = await _configurationManager.GetConfigurationAsync(cancellationToken);
        var validationParameters = new TokenValidationParameters
        {
            ClockSkew = ClockSkew,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateAudience = true,
            ValidAudience = _options.BotClientId,
            ValidateIssuer = true,
            ValidIssuers = BuildValidIssuers(_options.BotTokenIssuer),
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = configuration.SigningKeys,
            ValidateLifetime = true
        };

        try
        {
            var principal = _handler.ValidateToken(
                parsedHeader.Parameter,
                validationParameters,
                out _);

            ValidateAppIdClaim(principal);
            ValidateServiceUrlClaim(principal, activity);
        }
        catch (SecurityTokenException exception)
        {
            throw new BotAuthenticationException("Bot Connector token validation failed.", exception);
        }
    }

    private static string[] BuildValidIssuers(string issuer)
    {
        var normalized = issuer.TrimEnd('/');
        return [normalized, $"{normalized}/"];
    }

    private void ValidateAppIdClaim(System.Security.Claims.ClaimsPrincipal principal)
    {
        var appId = principal.Claims.FirstOrDefault(claim =>
            string.Equals(claim.Type, "appid", StringComparison.OrdinalIgnoreCase)
            || string.Equals(claim.Type, "azp", StringComparison.OrdinalIgnoreCase))?.Value;

        if (!string.IsNullOrWhiteSpace(appId)
            && !string.Equals(appId, _options.BotClientId, StringComparison.OrdinalIgnoreCase))
        {
            throw new BotAuthenticationException("Bot Connector token app id does not match the configured bot client id.");
        }
    }

    private void ValidateServiceUrlClaim(
        System.Security.Claims.ClaimsPrincipal principal,
        BotActivity activity)
    {
        var serviceUrl = principal.Claims.FirstOrDefault(claim =>
            string.Equals(claim.Type, "serviceurl", StringComparison.OrdinalIgnoreCase))?.Value;

        if (string.IsNullOrWhiteSpace(serviceUrl))
        {
            if (_options.RequireServiceUrlClaim)
            {
                throw new BotAuthenticationException("Bot Connector token is missing the serviceurl claim.");
            }

            return;
        }

        if (!ServiceUrlsEqual(serviceUrl, activity.ServiceUrl))
        {
            throw new BotAuthenticationException("Bot Connector token serviceurl does not match the activity serviceUrl.");
        }
    }

    private static bool ServiceUrlsEqual(string? left, string? right)
    {
        return string.Equals(
            left?.TrimEnd('/'),
            right?.TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);
    }
}
