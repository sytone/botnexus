using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Extensions;
using BotNexus.Gateway.Security;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Gateway.Tests.Security;

/// <summary>
/// Verifies the trusted security-event sink and the exec approval manager are DI-registered
/// (Step 2/5 of the security-event taxonomy, issue #1645 / #1526). Wiring the sink is what lets
/// the approval boundary emit security events to a trusted store rather than the public stream.
/// </summary>
public sealed class SecurityEventSinkRegistrationTests
{
    [Fact]
    public void AddBotNexusGateway_RegistersSecurityEventSink()
    {
        var services = new ServiceCollection();

        services.AddBotNexusGateway();

        services.ShouldContain(d => d.ServiceType == typeof(ISecurityEventSink));
    }

    [Fact]
    public void AddBotNexusGateway_RegistersExecApprovalManager()
    {
        var services = new ServiceCollection();

        services.AddBotNexusGateway();

        services.ShouldContain(d => d.ServiceType == typeof(IExecApprovalManager));
    }
}
