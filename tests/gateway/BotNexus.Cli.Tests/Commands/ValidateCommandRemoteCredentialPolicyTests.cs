using BotNexus.Cli.Commands;

namespace BotNexus.Cli.Tests.Commands;

/// <summary>
/// Behavioural cover for clause 1 of #2747: <c>botnexus validate --remote</c> used to build a bare
/// <see cref="HttpClient"/> and GET <c>/api/config/validate</c> on whatever host the operator typed
/// after <c>--gateway-url</c>, with no credential and no refusal. It now routes through
/// <c>GatewayClientFactory</c>, so the same policy the cron/conversation/debug commands obey applies
/// here.
/// </summary>
public sealed class ValidateCommandRemoteCredentialPolicyTests
{
    [Fact]
    public async Task ExecuteRemoteAsync_RefusesNonLoopbackTarget_WithoutExplicitToken()
    {
        // 203.0.113.0/24 is TEST-NET-3 (RFC 5737) - reserved for documentation and guaranteed not
        // routable, so a regression that actually sends the request fails on connect rather than
        // touching a real host. The refusal must happen BEFORE any socket is opened.
        var exitCode = await new ValidateCommand().ExecuteRemoteAsync(
            "http://203.0.113.10:5005",
            verbose: false,
            CancellationToken.None,
            token: null);

        exitCode.ShouldBe(1,
            "A non-loopback --gateway-url with no --token must be refused. Returning anything else " +
            "means the command contacted an operator-supplied host unauthenticated - the defect " +
            "#2747 closed everywhere else in the CLI.");
    }

    [Fact]
    public async Task ExecuteRemoteAsync_RejectsUnparseableGatewayUrl()
    {
        var exitCode = await new ValidateCommand().ExecuteRemoteAsync(
            "not-a-url",
            verbose: false,
            CancellationToken.None,
            token: null);

        exitCode.ShouldBe(1, "An unclassifiable target must fail closed rather than be probed.");
    }
}
