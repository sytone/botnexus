using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Sessions;

// Status/result updates may never turn an existing admission into a grant over another session.
internal static class SubAgentAdmissionIdentity
{
    public static void EnsureUnchanged(SubAgentRunDetail retained, SubAgentInfo info)
    {
        if (retained.SubAgentId != info.SubAgentId || retained.ParentSessionId != info.ParentSessionId.Value
            || retained.ChildSessionId != info.ChildSessionId.Value || retained.SpawningToolCallId != info.SpawningToolCallId
            || retained.ParentAgentId != info.ParentAgentId || retained.ChildAgentId != info.ChildAgentId
            || retained.ParentConversationId != info.ParentConversationId?.Value
            || retained.ChildConversationId != info.ChildConversationId?.Value)
            throw new UnauthorizedAccessException("Sub-agent admission identity cannot be rebound.");
    }
}
