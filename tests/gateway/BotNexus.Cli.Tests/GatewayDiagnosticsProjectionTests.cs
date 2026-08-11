using System.Net.Http;
using BotNexus.Cli.Diagnostics;
using BotNexus.Cli.Services;

namespace BotNexus.Cli.Tests;

/// <summary>
/// Issue #2845: the CLI must never print a raw gateway URL or a raw transport exception
/// message, because both routinely carry operator-embedded credentials (userinfo or a
/// <c>token=</c> query parameter) straight into stdout and CI logs.
/// </summary>
public sealed class GatewayDiagnosticsProjectionTests
{
    // ── AC1: userinfo and credential-shaped query values are projected away ──

    [Fact]
    public void ProjectUrl_RemovesUserInfoAndTokenQueryValue()
    {
        var projected = GatewayDiagnosticsProjection.ProjectUrl("https://user:pass@host:1234/?token=abc");

        Assert.DoesNotContain("pass", projected, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", projected, StringComparison.Ordinal);
        Assert.Contains("host:1234", projected, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("access_token")]
    [InlineData("api_key")]
    public void ProjectUrl_MasksEveryCredentialShapedQueryParameter(string parameterName)
    {
        var projected = GatewayDiagnosticsProjection.ProjectUrl(
            $"https://gateway.example.com/api?{parameterName}=s3cr3t-value&agent=farnsworth");

        Assert.DoesNotContain("s3cr3t-value", projected, StringComparison.Ordinal);
        Assert.Contains(parameterName, projected, StringComparison.Ordinal);
        Assert.Contains("agent=farnsworth", projected, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectUrl_PreservesBenignUrlUnchangedInSubstance()
    {
        var projected = GatewayDiagnosticsProjection.ProjectUrl("http://localhost:5005");

        Assert.Contains("localhost:5005", projected, StringComparison.Ordinal);
        Assert.StartsWith("http://", projected, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectUrl_FailsClosed_ForUnparseableInput()
    {
        // A value that is not an absolute http/https URL still must not leak a userinfo blob.
        var projected = GatewayDiagnosticsProjection.ProjectUrl("not a url user:hunter2@host");

        Assert.DoesNotContain("hunter2", projected, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectUrl_ReturnsPlaceholder_ForNullOrEmpty()
    {
        Assert.Equal("(none)", GatewayDiagnosticsProjection.ProjectUrl(null));
        Assert.Equal("(none)", GatewayDiagnosticsProjection.ProjectUrl("   "));
    }

    // ── AC4: exception messages that embed a credential-bearing URI are redacted ──

    [Fact]
    public void ProjectMessage_RedactsCredentialBearingUriEmbeddedInExceptionMessage()
    {
        var ex = new HttpRequestException(
            "No connection could be made because the target machine actively refused it " +
            "(https://admin:hunter2@gateway.example.com:8443/api/conversations?token=abc123).");

        var projected = GatewayDiagnosticsProjection.ProjectMessage(ex.Message);

        Assert.DoesNotContain("hunter2", projected, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", projected, StringComparison.Ordinal);
        Assert.Contains("gateway.example.com", projected, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectMessage_UsesSharedSecretRedactorVocabulary()
    {
        var projected = GatewayDiagnosticsProjection.ProjectMessage(
            "Request failed with Authorization: Bearer ghp_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789");

        Assert.DoesNotContain("ghp_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789", projected, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectMessage_LeavesBenignMessageIntact()
    {
        const string message = "Connection refused.";

        Assert.Equal(message, GatewayDiagnosticsProjection.ProjectMessage(message));
    }

    [Fact]
    public void ProjectMessage_ReturnsPlaceholder_ForNullOrEmpty()
    {
        Assert.Equal("(no details)", GatewayDiagnosticsProjection.ProjectMessage(null));
        Assert.Equal("(no details)", GatewayDiagnosticsProjection.ProjectMessage(""));
    }

    // ── AC5: the client/probe path still receives the UNREDACTED url ──

    [Fact]
    public void GatewayClientFactory_StillReceivesUnredactedUrl()
    {
        const string url = "http://user:pass@localhost:5005";

        var resolution = GatewayClientFactory.Resolve(
            url,
            TimeSpan.FromSeconds(5),
            explicitToken: "operator-token",
            credentialSource: new NoCredentialSource());

        Assert.NotNull(resolution.Client);
        using var client = resolution.Client!;
        Assert.NotNull(client.BaseAddress);
        Assert.Equal("user:pass", client.BaseAddress!.UserInfo);

        // ...and the projection of that same URL is what the operator would have seen.
        Assert.DoesNotContain("pass", GatewayDiagnosticsProjection.ProjectUrl(url), StringComparison.Ordinal);
    }

    private sealed class NoCredentialSource : IGatewayCredentialSource
    {
        public string? GetGatewayCredential() => null;
    }
}
