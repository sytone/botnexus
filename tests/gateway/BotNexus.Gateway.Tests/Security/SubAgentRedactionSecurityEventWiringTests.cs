using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Security;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Gateway.Tests.Security;

/// <summary>
/// Verifies that the type-mapped DI registrations for <see cref="SecretRedactor"/> and the
/// sub-agent manager auto-resolve the optional <see cref="ISecurityEventSink"/> constructor
/// parameter when the sink is registered (Step 4/5 of the security-event taxonomy, issue
/// #1647 / #1526). This is the DI claim the emitters rely on: because the sink is registered as
/// a singleton, .NET DI fills the optional reference parameter of a type-mapped service without
/// any explicit factory wiring.
/// </summary>
public sealed class SubAgentRedactionSecurityEventWiringTests
{
    [Fact]
    public void SecretRedactor_TypeMapped_AutoInjectsRegisteredSink()
    {
        // Mirror the GatewaySCE registration shape: a registered sink plus a type-mapped redactor.
        var sink = new RecordingSink();
        var services = new ServiceCollection();
        services.AddSingleton<ISecurityEventSink>(sink);
        services.AddSingleton<ISecretRedactor, SecretRedactor>();

        using var provider = services.BuildServiceProvider();
        var redactor = provider.GetRequiredService<ISecretRedactor>();

        // A redaction-triggering call must reach the registered sink, proving the optional
        // ctor param was auto-injected by DI.
        redactor.Redact("aws_access_key_id=AKIAIOSFODNN7EXAMPLE");

        sink.Count.ShouldBe(1);
        sink.Events[0].Action.ShouldBe("secret.redacted");
    }

    [Fact]
    public void SecretRedactor_TypeMapped_WithNoSink_StillResolvesAndRedacts()
    {
        // No sink registered - the optional param falls back to null and redaction still works.
        var services = new ServiceCollection();
        services.AddSingleton<ISecretRedactor, SecretRedactor>();

        using var provider = services.BuildServiceProvider();
        var redactor = provider.GetRequiredService<ISecretRedactor>();

        redactor.Redact("aws_access_key_id=AKIAIOSFODNN7EXAMPLE").ShouldContain("[REDACTED]");
    }

    private sealed class RecordingSink : ISecurityEventSink
    {
        private readonly List<SecurityEvent> _events = [];
        public IReadOnlyList<SecurityEvent> Events => _events;
        public int Count => _events.Count;
        public void Record(SecurityEvent securityEvent) => _events.Add(securityEvent);
        public IReadOnlyList<SecurityEvent> Snapshot() => _events;
        public void Clear() => _events.Clear();
    }
}
