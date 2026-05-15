namespace BotNexus.CodingAgent.Session;

public sealed record SessionBranchInfo(
    string LeafEntryId,
    string Name,
    bool IsActive,
    int MessageCount,
    DateTimeOffset UpdatedAt);
