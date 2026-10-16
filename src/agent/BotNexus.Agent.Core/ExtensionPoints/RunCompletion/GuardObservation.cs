namespace BotNexus.Agent.Core.ExtensionPoints.RunCompletion;
/// <summary>Bounded payload-free evidence of a run-local guard decision.</summary>
public sealed record GuardObservation(
    string GuardKind,
    int ConsecutiveCount,
    int TotalResults,
    int WarningThreshold,
    int StopThreshold,
    bool AbsoluteLimitReached,
    string Disposition,
    IReadOnlyList<string> EvidenceReferences);
