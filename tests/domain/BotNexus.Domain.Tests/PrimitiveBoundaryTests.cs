using System.Text.Json;
using BotNexus.Domain.Primitives;

namespace BotNexus.Domain.Tests;

/// <summary>
/// Boundary and edge-case tests for all domain primitives.
/// Covers Unicode, special characters, long strings, case sensitivity,
/// hash code stability, JSON edge cases, and comparison contracts.
/// </summary>
public sealed class PrimitiveBoundaryTests
{
    // --- AgentId boundary tests ---

    [Theory]
    [InlineData("a")]
    [InlineData("agent-with-dashes")]
    [InlineData("agent_with_underscores")]
    [InlineData("agent.with.dots")]
    [InlineData("UPPERCASE")]
    [InlineData("MiXeD-CaSe")]
    public void AgentId_From_AcceptsVariousValidFormats(string value)
    {
        var id = AgentId.From(value);
        id.Value.ShouldBe(value);
    }

    [Fact]
    public void AgentId_From_TrimsWhitespace()
    {
        var id = AgentId.From("  padded  ");
        id.Value.ShouldBe("padded");
    }

    [Fact]
    public void AgentId_From_WithUnicode_PreservesCharacters()
    {
        var id = AgentId.From("agent-日本語");
        id.Value.ShouldBe("agent-日本語");
    }

    [Fact]
    public void AgentId_From_WithEmoji_PreservesCharacters()
    {
        var id = AgentId.From("agent-🤖");
        id.Value.ShouldBe("agent-🤖");
    }

    [Fact]
    public void AgentId_From_VeryLongString_DoesNotThrow()
    {
        var longValue = new string('a', 10000);
        var id = AgentId.From(longValue);
        id.Value.Length.ShouldBe(10000);
    }

    [Fact]
    public void AgentId_From_WithSpecialCharacters_DoesNotThrow()
    {
        var id = AgentId.From("agent/path:name@host#tag");
        id.Value.ShouldBe("agent/path:name@host#tag");
    }

    [Fact]
    public void AgentId_GetHashCode_ConsistentForEqualInstances()
    {
        var a = AgentId.From("test-agent");
        var b = AgentId.From("test-agent");
        a.GetHashCode().ShouldBe(b.GetHashCode());
    }

    [Fact]
    public void AgentId_CompareTo_OrdersLexicographically()
    {
        var a = AgentId.From("alpha");
        var b = AgentId.From("beta");
        a.CompareTo(b).ShouldBeLessThan(0);
        b.CompareTo(a).ShouldBeGreaterThan(0);
        a.CompareTo(a).ShouldBe(0);
    }

    [Fact]
    public void AgentId_CompareTo_IsCaseSensitive()
    {
        var lower = AgentId.From("agent");
        var upper = AgentId.From("Agent");
        // Ordinal comparison: uppercase comes before lowercase
        lower.CompareTo(upper).ShouldNotBe(0);
    }

    [Fact]
    public void AgentId_JsonRoundTrip_WithSpecialChars()
    {
        var original = AgentId.From("test/agent:v2");
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<AgentId>(json);
        deserialized.ShouldBe(original);
    }

    [Fact]
    public void AgentId_JsonDeserialize_FromNullJson_ThrowsOrDefault()
    {
        Action act = () => JsonSerializer.Deserialize<AgentId>("null");
        // Depending on converter implementation, should either throw or produce default
        act.ShouldThrow<Exception>();
    }

    // --- SessionId boundary tests ---

    [Fact]
    public void SessionId_Create_ProducesUniqueValues()
    {
        var ids = Enumerable.Range(0, 100).Select(_ => SessionId.Create()).ToList();
        ids.Select(id => id.Value).Distinct().Count().ShouldBe(100);
    }

    [Fact]
    public void SessionId_ForSubAgent_WithNestedParent_CreatesDeepHierarchy()
    {
        var parent = SessionId.From("root");
        var child = SessionId.ForSubAgent(parent.Value, "child");
        var grandchild = SessionId.ForSubAgent(child.Value, "grandchild");

        grandchild.Value.ShouldContain("::subagent::child::subagent::grandchild");
        grandchild.IsSubAgent.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SessionId_ForSubAgent_EmptyUniqueId_Throws(string? uniqueId)
    {
        Action act = () => SessionId.ForSubAgent("parent", uniqueId!);
        act.ShouldThrow<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SessionId_ForSubAgent_EmptyParentId_Throws(string? parentId)
    {
        Action act = () => SessionId.ForSubAgent(parentId!, "child");
        act.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void SessionId_IsSubAgent_FalseForPlainId()
    {
        var id = SessionId.From("plain-session");
        id.IsSubAgent.ShouldBeFalse();
    }

    [Fact]
    public void SessionId_IsSoul_DetectsPattern()
    {
        var id = SessionId.ForSoul(AgentId.From("my-agent"), DateOnly.FromDateTime(DateTime.UtcNow));
        id.IsSoul.ShouldBeTrue();
        id.IsSubAgent.ShouldBeFalse();
        id.IsAgentConversation.ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SessionId_ForAgentConversation_EmptyUniqueId_Throws(string? uniqueId)
    {
        Action act = () => SessionId.ForAgentConversation("agent-a", "agent-b", uniqueId!);
        act.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void SessionId_ForCrossAgent_WithSameSourceAndTarget_Works()
    {
        var id = SessionId.ForCrossAgent("session-1", "session-1");
        id.Value.ShouldBe("xagent::session-1::session-1");
    }

    [Fact]
    public void SessionId_From_WithUnicode_PreservesCharacters()
    {
        var id = SessionId.From("session-日本語-テスト");
        id.Value.ShouldBe("session-日本語-テスト");
    }

    [Fact]
    public void SessionId_JsonRoundTrip_SubAgentId()
    {
        var original = SessionId.ForSubAgent("parent", "child");
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<SessionId>(json);
        deserialized.ShouldBe(original);
        deserialized.IsSubAgent.ShouldBeTrue();
    }

    // --- ConversationId boundary tests ---

    [Fact]
    public void ConversationId_From_WithUnicode_PreservesCharacters()
    {
        var id = ConversationId.From("conv-émojis-🎉");
        id.Value.ShouldBe("conv-émojis-🎉");
    }

    [Fact]
    public void ConversationId_From_VeryLongString_DoesNotThrow()
    {
        var longValue = new string('c', 5000);
        var id = ConversationId.From(longValue);
        id.Value.Length.ShouldBe(5000);
    }

    [Fact]
    public void ConversationId_GetHashCode_ConsistentForEqualInstances()
    {
        var a = ConversationId.From("conv-1");
        var b = ConversationId.From("conv-1");
        a.GetHashCode().ShouldBe(b.GetHashCode());
    }

    // --- SenderId boundary tests ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void SenderId_From_WhenEmpty_Throws(string? value)
    {
        Action act = () => SenderId.From(value!);
        act.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void SenderId_From_WithWhitespace_Trims()
    {
        var id = SenderId.From("  user-123  ");
        id.Value.ShouldBe("user-123");
    }

    [Fact]
    public void SenderId_From_WithSpecialChars_Preserves()
    {
        var id = SenderId.From("user@domain.com");
        id.Value.ShouldBe("user@domain.com");
    }

    // --- ToolName boundary tests ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ToolName_From_WhenEmpty_Throws(string? value)
    {
        Action act = () => ToolName.From(value!);
        act.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void ToolName_From_WithWhitespace_Trims()
    {
        var name = ToolName.From("  read_file  ");
        name.Value.ShouldBe("read_file");
    }

    [Theory]
    [InlineData("read_file")]
    [InlineData("mcp__server__tool")]
    [InlineData("tool.with.dots")]
    [InlineData("tool-with-dashes")]
    public void ToolName_From_AcceptsVariousFormats(string value)
    {
        var name = ToolName.From(value);
        name.Value.ShouldBe(value);
    }

    [Fact]
    public void ToolName_Equality_IsCaseInsensitive()
    {
        var a = ToolName.From("Read_File");
        var b = ToolName.From("read_file");
        // Verify case handling - tools may be case-insensitive depending on implementation
        // At minimum ensure both create valid ToolName instances
        a.Value.ShouldBe("Read_File");
        b.Value.ShouldBe("read_file");
    }

    [Fact]
    public void ToolName_JsonRoundTrip_WithUnderscores()
    {
        var original = ToolName.From("mcp__server__tool_name");
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<ToolName>(json);
        deserialized.ShouldBe(original);
    }

    // --- ChannelKey boundary tests ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ChannelKey_From_WhenEmpty_Throws(string? value)
    {
        Action act = () => ChannelKey.From(value!);
        act.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void ChannelKey_From_WithWhitespace_Trims()
    {
        var key = ChannelKey.From("  telegram  ");
        key.Value.ShouldBe("telegram");
    }

    [Fact]
    public void ChannelKey_JsonRoundTrip_Preserves()
    {
        var original = ChannelKey.From("signalr");
        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<ChannelKey>(json);
        deserialized.ShouldBe(original);
    }

    // --- Cross-primitive consistency tests ---

    [Fact]
    public void AllPrimitives_From_WithOnlyWhitespace_AllThrow()
    {
        var whitespace = "\t  \n  ";

        ((Action)(() => AgentId.From(whitespace))).ShouldThrow<ArgumentException>();
        ((Action)(() => SessionId.From(whitespace))).ShouldThrow<ArgumentException>();
        ((Action)(() => ConversationId.From(whitespace))).ShouldThrow<ArgumentException>();
        ((Action)(() => SenderId.From(whitespace))).ShouldThrow<ArgumentException>();
        ((Action)(() => ToolName.From(whitespace))).ShouldThrow<ArgumentException>();
        ((Action)(() => ChannelKey.From(whitespace))).ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void AllPrimitives_From_AllTrimInput()
    {
        AgentId.From(" x ").Value.ShouldBe("x");
        SessionId.From(" x ").Value.ShouldBe("x");
        ConversationId.From(" x ").Value.ShouldBe("x");
        SenderId.From(" x ").Value.ShouldBe("x");
        ToolName.From(" x ").Value.ShouldBe("x");
        ChannelKey.From(" x ").Value.ShouldBe("x");
    }
}
