using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Security;

namespace BotNexus.Gateway.Tests.Security;

/// <summary>
/// Tests for the security-event emissions wired into <see cref="SecretRedactor"/>
/// (Step 4/5 of the security-event taxonomy, issue #1647 / #1526). A redaction-triggering
/// <see cref="SecretRedactor.Redact(string)"/> call (one that actually replaced at least one
/// secret) must emit exactly one <see cref="SecurityEvent"/> to the trusted
/// <see cref="ISecurityEventSink"/>. A call that changes nothing must emit nothing, and the
/// emitted event must NEVER carry plaintext secret material.
/// </summary>
public sealed class SecretRedactorSecurityEventTests
{
    // A fake AWS access key id (AKIA + 16 uppercase alphanumerics) - matches the AWS pattern but
    // is a well-known example value, never a real credential.
    private const string FakeAwsKey = "AKIAIOSFODNN7EXAMPLE";

    private readonly RecordingSink _sink = new();
    private readonly SecretRedactor _sut;

    public SecretRedactorSecurityEventTests()
        => _sut = new SecretRedactor(_sink);

    // -- Redaction emits ------------------------------------------------

    [Fact]
    public void Redact_WhenSecretReplaced_EmitsExactlyOneSecretRedactedEvent()
    {
        var result = _sut.Redact($"aws_access_key_id={FakeAwsKey}");

        // Sanity: the redaction itself still happened.
        result.ShouldContain("[REDACTED]");
        result.ShouldNotContain(FakeAwsKey);

        _sink.Events.Count.ShouldBe(1);
        var evt = _sink.Events[0];
        evt.Category.ShouldBe(SecurityEventCategory.Secret);
        evt.Action.ShouldBe("secret.redacted");
        evt.Outcome.ShouldBe(SecurityEventOutcome.Success);
        evt.Severity.ShouldBe(SecurityEventSeverity.Low);
        evt.Control.ShouldBe(SecurityControlFamily.Secret);
    }

    [Fact]
    public void Redact_WhenSecretReplaced_CarriesNonSensitiveSecretRefTarget()
    {
        _sut.Redact($"aws_access_key_id={FakeAwsKey}");

        var evt = _sink.Events[0];
        evt.Target.ShouldNotBeNull();
        evt.Target!.Kind.ShouldBe(SecurityTargetKind.SecretRef);
        // The reference must be a stable, non-sensitive label - never the secret material.
        evt.Target.Reference.ShouldBe("transcript");
    }

    // -- No-match emits nothing -----------------------------------------

    [Fact]
    public void Redact_WhenNothingMatched_EmitsNothing()
    {
        var safe = "The quick brown fox jumps over the lazy dog.";

        var result = _sut.Redact(safe);

        result.ShouldBe(safe);
        _sink.Events.Count.ShouldBe(0);
    }

    [Fact]
    public void Redact_WhenInputEmpty_EmitsNothing()
    {
        _sut.Redact(string.Empty);

        _sink.Events.Count.ShouldBe(0);
    }

    // -- Secret-leakage guard (the security-critical assertion) ---------

    [Fact]
    public void Redact_EmittedEvent_NeverContainsTheSecretValue()
    {
        _sut.Redact($"Authorization: Bearer {FakeAwsKey}");

        _sink.Events.Count.ShouldBe(1);
        var evt = _sink.Events[0];

        // The secret value must not appear anywhere a consumer could read it off the event.
        evt.Action.ShouldNotContain(FakeAwsKey);
        evt.Target!.Reference.ShouldNotContain(FakeAwsKey);
        (evt.Actor?.Id ?? string.Empty).ShouldNotContain(FakeAwsKey);
    }

    [Fact]
    public void Redact_MultipleSecretsInOneCall_StillEmitsExactlyOneEvent()
    {
        // Two distinct secrets in one transcript chunk - the boundary emits per Redact call,
        // not per matched secret, so exactly one event is recorded.
        var input = $"id={FakeAwsKey} and Authorization: Bearer {FakeAwsKey}";

        var result = _sut.Redact(input);

        result.ShouldNotContain(FakeAwsKey);
        _sink.Events.Count.ShouldBe(1);
        _sink.Events[0].Action.ShouldBe("secret.redacted");
    }

    // -- Best-effort / never breaks the redaction path ------------------

    [Fact]
    public void Redact_WhenSinkThrows_StillRedacts()
    {
        var sut = new SecretRedactor(new ThrowingSink());

        var result = sut.Redact($"aws_access_key_id={FakeAwsKey}");

        result.ShouldContain("[REDACTED]");
        result.ShouldNotContain(FakeAwsKey);
    }

    [Fact]
    public void NullSink_DoesNotEmitAndStillRedacts()
    {
        var sut = new SecretRedactor();

        var result = sut.Redact($"aws_access_key_id={FakeAwsKey}");

        result.ShouldContain("[REDACTED]");
        result.ShouldNotContain(FakeAwsKey);
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

    private sealed class ThrowingSink : ISecurityEventSink
    {
        public int Count => 0;
        public void Record(SecurityEvent securityEvent) => throw new InvalidOperationException("sink down");
        public IReadOnlyList<SecurityEvent> Snapshot() => [];
        public void Clear() { }
    }
}
