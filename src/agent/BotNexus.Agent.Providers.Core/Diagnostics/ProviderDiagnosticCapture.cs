namespace BotNexus.Agent.Providers.Core.Diagnostics;

/// <summary>Controls what diagnostic evidence may be retained for provider calls.</summary>
public enum ProviderDiagnosticMode
{
    Metadata,
    Errors,
    Payload
}

/// <summary>Why a provider diagnostic policy could not be activated.</summary>
public enum ProviderDiagnosticPolicyError
{
    None,
    Disabled,
    SelectorRequired,
    InvalidSampling,
    InvalidBounds,
    Expired
}

/// <summary>Exact, case-sensitive allow-list selectors. Configured selector groups are ANDed.</summary>
public sealed record ProviderDiagnosticSelectors
{
    public IReadOnlyCollection<string> Providers { get; init; } = [];
    public IReadOnlyCollection<string> Models { get; init; } = [];
    public IReadOnlyCollection<string> Agents { get; init; } = [];
    public IReadOnlyCollection<string> Conversations { get; init; } = [];
    public IReadOnlyCollection<string> Sessions { get; init; } = [];
    public IReadOnlyCollection<string> Transports { get; init; } = [];
    public IReadOnlyCollection<string> FailureClasses { get; init; } = [];
}

/// <summary>Independent deterministic sampling rates for successful and failed calls.</summary>
public sealed record ProviderDiagnosticSampling(double SuccessRate = 0, double FailureRate = 1);

/// <summary>Hard storage and retention ceilings for one diagnostic policy.</summary>
public sealed record ProviderDiagnosticBounds(
    int MaxCaptures = 100,
    long MaxTotalBytes = 5 * 1024 * 1024,
    int MaxBytesPerCapture = 64 * 1024,
    int MaxPayloadChars = 4096,
    TimeSpan RetentionAge = default)
{
    internal TimeSpan EffectiveRetentionAge => RetentionAge == default ? TimeSpan.FromHours(1) : RetentionAge;
}

/// <summary>Hot-reloadable provider diagnostic policy input.</summary>
public sealed record ProviderDiagnosticPolicy
{
    public bool Enabled { get; init; }
    public ProviderDiagnosticMode Mode { get; init; } = ProviderDiagnosticMode.Metadata;
    public ProviderDiagnosticSelectors Selectors { get; init; } = new();
    public ProviderDiagnosticSampling Sampling { get; init; } = new();
    public ProviderDiagnosticBounds Bounds { get; init; } = new();
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>Authoritative identity attached at the provider-call boundary.</summary>
public sealed record ProviderDiagnosticContext(
    string? Provider = null,
    string? Model = null,
    string? Agent = null,
    string? Conversation = null,
    string? Session = null,
    string? Transport = null,
    string? Api = null,
    string? Run = null,
    string? Turn = null,
    int Attempt = 0,
    string? CorrelationId = null);

/// <summary>Terminal outcome used for policy selection without retaining provider payloads.</summary>
public sealed record ProviderDiagnosticOutcome(bool Failed, string? FailureClass)
{
    public static ProviderDiagnosticOutcome Success { get; } = new(false, null);
    public static ProviderDiagnosticOutcome Failure(string failureClass) => new(true, failureClass);
}

public readonly record struct ProviderDiagnosticMatch(bool IsMatch, long PolicyRevision)
{
    internal static ProviderDiagnosticMatch NoMatch(long revision) => new(false, revision);
}

public readonly record struct ProviderDiagnosticPolicyCompilation(
    bool IsValid,
    ProviderDiagnosticPolicyError Error,
    ProviderDiagnosticPolicySnapshot Snapshot);

/// <summary>
/// Immutable, validated policy captured when a provider call starts. Reloads therefore cannot mix
/// selectors or bounds within one call.
/// </summary>
public sealed class ProviderDiagnosticPolicySnapshot
{
    private static readonly StringComparer ExactComparer = StringComparer.Ordinal;

    private ProviderDiagnosticPolicySnapshot(
        bool enabled,
        ProviderDiagnosticMode mode,
        ProviderDiagnosticSelectors selectors,
        ProviderDiagnosticSampling sampling,
        ProviderDiagnosticBounds bounds,
        DateTimeOffset? expiresAt,
        long revision)
    {
        Enabled = enabled;
        Mode = mode;
        Selectors = selectors;
        Sampling = sampling;
        Bounds = bounds;
        ExpiresAt = expiresAt;
        Revision = revision;
    }

    public bool Enabled { get; }
    public ProviderDiagnosticMode Mode { get; }
    public ProviderDiagnosticSelectors Selectors { get; }
    public ProviderDiagnosticSampling Sampling { get; }
    public ProviderDiagnosticBounds Bounds { get; }
    public DateTimeOffset? ExpiresAt { get; }
    public long Revision { get; }

    public static ProviderDiagnosticPolicyCompilation Compile(
        ProviderDiagnosticPolicy policy,
        long revision,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var selectors = CopySelectors(policy.Selectors);
        var snapshot = new ProviderDiagnosticPolicySnapshot(
            policy.Enabled,
            policy.Mode,
            selectors,
            policy.Sampling,
            policy.Bounds,
            policy.ExpiresAt,
            revision);

        if (!policy.Enabled)
            return Invalid(ProviderDiagnosticPolicyError.Disabled, snapshot);
        if (!HasSelector(selectors))
            return Invalid(ProviderDiagnosticPolicyError.SelectorRequired, snapshot);
        if (!ValidRate(policy.Sampling.SuccessRate) || !ValidRate(policy.Sampling.FailureRate))
            return Invalid(ProviderDiagnosticPolicyError.InvalidSampling, snapshot);
        if (!ValidBounds(policy.Bounds))
            return Invalid(ProviderDiagnosticPolicyError.InvalidBounds, snapshot);
        if (policy.ExpiresAt is not null && policy.ExpiresAt <= now)
            return Invalid(ProviderDiagnosticPolicyError.Expired, snapshot);

        return new ProviderDiagnosticPolicyCompilation(true, ProviderDiagnosticPolicyError.None, snapshot);
    }

    public ProviderDiagnosticMatch Match(
        ProviderDiagnosticContext context,
        ProviderDiagnosticOutcome outcome,
        double sample,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(outcome);

        if (!Enabled || sample < 0 || sample >= 1 || (ExpiresAt is not null && now >= ExpiresAt))
            return ProviderDiagnosticMatch.NoMatch(Revision);
        if (Mode == ProviderDiagnosticMode.Errors && !outcome.Failed)
            return ProviderDiagnosticMatch.NoMatch(Revision);
        if (!Matches(Selectors.Providers, context.Provider) ||
            !Matches(Selectors.Models, context.Model) ||
            !Matches(Selectors.Agents, context.Agent) ||
            !Matches(Selectors.Conversations, context.Conversation) ||
            !Matches(Selectors.Sessions, context.Session) ||
            !Matches(Selectors.Transports, context.Transport))
            return ProviderDiagnosticMatch.NoMatch(Revision);
        if (Selectors.FailureClasses.Count > 0 &&
            (!outcome.Failed || !Matches(Selectors.FailureClasses, outcome.FailureClass)))
            return ProviderDiagnosticMatch.NoMatch(Revision);

        var rate = outcome.Failed ? Sampling.FailureRate : Sampling.SuccessRate;
        return new ProviderDiagnosticMatch(sample < rate, Revision);
    }

    private static ProviderDiagnosticPolicyCompilation Invalid(
        ProviderDiagnosticPolicyError error,
        ProviderDiagnosticPolicySnapshot snapshot) => new(
            false,
            error,
            new ProviderDiagnosticPolicySnapshot(
                false,
                snapshot.Mode,
                snapshot.Selectors,
                snapshot.Sampling,
                snapshot.Bounds,
                snapshot.ExpiresAt,
                snapshot.Revision));

    private static bool ValidRate(double value) => double.IsFinite(value) && value is >= 0 and <= 1;

    private static bool ValidBounds(ProviderDiagnosticBounds bounds) =>
        bounds.MaxCaptures > 0 &&
        bounds.MaxTotalBytes > 0 &&
        bounds.MaxBytesPerCapture > 0 &&
        bounds.MaxBytesPerCapture <= bounds.MaxTotalBytes &&
        bounds.MaxPayloadChars >= 0 &&
        bounds.EffectiveRetentionAge > TimeSpan.Zero;

    private static bool HasSelector(ProviderDiagnosticSelectors selectors) =>
        selectors.Providers.Count > 0 || selectors.Models.Count > 0 || selectors.Agents.Count > 0 ||
        selectors.Conversations.Count > 0 || selectors.Sessions.Count > 0 || selectors.Transports.Count > 0 ||
        selectors.FailureClasses.Count > 0;

    private static bool Matches(IReadOnlyCollection<string> allowed, string? actual) =>
        allowed.Count == 0 || (actual is not null && allowed.Contains(actual, ExactComparer));

    private static ProviderDiagnosticSelectors CopySelectors(ProviderDiagnosticSelectors selectors) => new()
    {
        Providers = Copy(selectors.Providers),
        Models = Copy(selectors.Models),
        Agents = Copy(selectors.Agents),
        Conversations = Copy(selectors.Conversations),
        Sessions = Copy(selectors.Sessions),
        Transports = Copy(selectors.Transports),
        FailureClasses = Copy(selectors.FailureClasses)
    };

    private static IReadOnlyCollection<string> Copy(IReadOnlyCollection<string>? values) =>
        values is null
            ? []
            : values.Where(value => !string.IsNullOrWhiteSpace(value)).ToHashSet(ExactComparer);
}

/// <summary>A bounded provider diagnostic record. Payload is absent outside explicit payload mode.</summary>
public sealed record ProviderDiagnosticCapture(
    string CaptureId,
    int SchemaVersion,
    long PolicyRevision,
    DateTimeOffset GeneratedAt,
    DateTimeOffset ExpiresAt,
    ProviderDiagnosticContext Context,
    ProviderDiagnosticOutcome Outcome,
    int StoredBytes,
    string? Payload);

/// <summary>Thread-safe in-memory sink with count, byte, per-record, and age limits.</summary>
public sealed class BoundedProviderDiagnosticCaptureSink
{
    private readonly object _gate = new();
    private readonly LinkedList<ProviderDiagnosticCapture> _captures = [];
    private readonly ProviderDiagnosticBounds _bounds;
    private long _storedBytes;
    private long _droppedCount;

    public BoundedProviderDiagnosticCaptureSink(ProviderDiagnosticBounds bounds)
    {
        if (!Valid(bounds))
            throw new ArgumentOutOfRangeException(nameof(bounds));
        _bounds = bounds;
    }

    public long StoredBytes
    {
        get { lock (_gate) return _storedBytes; }
    }

    public long DroppedCount
    {
        get { lock (_gate) return _droppedCount; }
    }

    public bool TryWrite(ProviderDiagnosticCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);

        lock (_gate)
        {
            PurgeExpired(capture.GeneratedAt);
            if (capture.StoredBytes < 0 || capture.StoredBytes > _bounds.MaxBytesPerCapture ||
                capture.Payload?.Length > _bounds.MaxPayloadChars ||
                _captures.Count >= _bounds.MaxCaptures ||
                _storedBytes + capture.StoredBytes > _bounds.MaxTotalBytes)
            {
                _droppedCount++;
                return false;
            }

            _captures.AddLast(capture);
            _storedBytes += capture.StoredBytes;
            return true;
        }
    }

    public IReadOnlyList<ProviderDiagnosticCapture> List(DateTimeOffset now)
    {
        lock (_gate)
        {
            PurgeExpired(now);
            return _captures.ToArray();
        }
    }

    private void PurgeExpired(DateTimeOffset now)
    {
        var node = _captures.First;
        while (node is not null)
        {
            var next = node.Next;
            var ageExpiry = node.Value.GeneratedAt + _bounds.EffectiveRetentionAge;
            if (node.Value.ExpiresAt <= now || ageExpiry <= now)
            {
                _storedBytes -= node.Value.StoredBytes;
                _captures.Remove(node);
            }
            node = next;
        }
    }

    private static bool Valid(ProviderDiagnosticBounds bounds) =>
        bounds.MaxCaptures > 0 && bounds.MaxTotalBytes > 0 && bounds.MaxBytesPerCapture > 0 &&
        bounds.MaxBytesPerCapture <= bounds.MaxTotalBytes && bounds.MaxPayloadChars >= 0 &&
        bounds.EffectiveRetentionAge > TimeSpan.Zero;
}
