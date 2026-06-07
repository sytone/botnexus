using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests for <see cref="ToolDescriptionFormatter"/>.
/// </summary>
public sealed class ToolDescriptionFormatterTests
{
    [Fact]
    public void Read_WithPath_ShowsFilename()
    {
        var result = ToolDescriptionFormatter.FormatDescription("read", """{"path": "src/domain/Models/Agent.cs"}""");
        Assert.Equal("📄 Read Agent.cs", result);
    }

    [Fact]
    public void Write_WithPath_ShowsFilename()
    {
        var result = ToolDescriptionFormatter.FormatDescription("write", """{"path": "src/gateway/Host.cs", "content": "hello"}""");
        Assert.Equal("✏️ Write Host.cs", result);
    }

    [Fact]
    public void Edit_WithPath_ShowsFilename()
    {
        var result = ToolDescriptionFormatter.FormatDescription("edit", """{"path": "tests/MyTest.cs", "edits": []}""");
        Assert.Equal("✏️ Edit MyTest.cs", result);
    }

    [Fact]
    public void Shell_WithCommand_ShowsPreview()
    {
        var result = ToolDescriptionFormatter.FormatDescription("shell", """{"command": "git status --short"}""");
        Assert.Equal("💻 git status --short", result);
    }

    [Fact]
    public void Shell_WithLongCommand_Truncates()
    {
        var longCmd = new string('x', 100);
        var result = ToolDescriptionFormatter.FormatDescription("shell", $$"""{"command": "{{longCmd}}"}""");
        Assert.Equal("💻 " + longCmd[..50] + "…", result);
    }

    [Fact]
    public void Exec_WithCommandArray_ShowsFirst()
    {
        var result = ToolDescriptionFormatter.FormatDescription("exec", """{"command": ["pwsh", "-c", "Get-Date"]}""");
        Assert.Equal("💻 pwsh -c Get-Date", result);
    }

    [Fact]
    public void Exec_WithLongCommandArray_Truncates()
    {
        var result = ToolDescriptionFormatter.FormatDescription("exec", """{"command": ["pwsh", "-NoProfile", "-c", "Get-ChildItem -Recurse -Include '*.cs' | Where-Object Name -Match 'Agent'"]}""");
        Assert.StartsWith("💻 pwsh -NoProfile -c Get-ChildItem", result);
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void WebSearch_WithQuery_ShowsQuery()
    {
        var result = ToolDescriptionFormatter.FormatDescription("web_search", """{"query": "BotNexus documentation"}""");
        Assert.Equal("🔍 BotNexus documentation", result);
    }

    [Fact]
    public void WebFetch_WithUrl_ShowsUrl()
    {
        var result = ToolDescriptionFormatter.FormatDescription("web_fetch", """{"url": "https://example.com/page"}""");
        Assert.Equal("🌐 example.com/page", result);
    }

    [Fact]
    public void WebFetch_WithLongUrl_Truncates()
    {
        var longPath = new string('x', 100);
        var result = ToolDescriptionFormatter.FormatDescription("web_fetch", $$"""{"url": "https://example.com/{{longPath}}"}""");
        Assert.StartsWith("🌐 example.com/", result);
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void Grep_WithPattern_ShowsPattern()
    {
        var result = ToolDescriptionFormatter.FormatDescription("grep", """{"pattern": "SystemPrompt", "glob": "*.cs"}""");
        Assert.Equal("🔎 Grep SystemPrompt", result);
    }

    [Fact]
    public void Glob_WithPattern_ShowsPattern()
    {
        var result = ToolDescriptionFormatter.FormatDescription("glob", """{"pattern": "**/*.razor"}""");
        Assert.Equal("🔎 Glob **/*.razor", result);
    }

    [Fact]
    public void Ls_WithPath_ShowsPath()
    {
        var result = ToolDescriptionFormatter.FormatDescription("ls", """{"path": "src/gateway"}""");
        Assert.Equal("📂 List src/gateway", result);
    }

    [Fact]
    public void MemorySave_ShowsDescription()
    {
        var result = ToolDescriptionFormatter.FormatDescription("memory_save", """{"content": "some note"}""");
        Assert.Equal("💾 Save memory", result);
    }

    [Fact]
    public void MemorySearch_WithQuery_ShowsQuery()
    {
        var result = ToolDescriptionFormatter.FormatDescription("memory_search", """{"query": "cron schedule"}""");
        Assert.Equal("🧠 Search memory: cron schedule", result);
    }

    [Fact]
    public void Cron_ShowsAction()
    {
        var result = ToolDescriptionFormatter.FormatDescription("cron", """{"action": "list"}""");
        Assert.Equal("⏰ Cron list", result);
    }

    [Fact]
    public void Canvas_ShowsAction()
    {
        var result = ToolDescriptionFormatter.FormatDescription("canvas", """{"action": "render"}""");
        Assert.Equal("🎨 Canvas render", result);
    }

    [Fact]
    public void UnknownTool_ShowsToolName()
    {
        var result = ToolDescriptionFormatter.FormatDescription("custom_tool", """{"foo": "bar"}""");
        Assert.Equal("🔧 custom_tool", result);
    }

    [Fact]
    public void NullArgs_ShowsToolName()
    {
        var result = ToolDescriptionFormatter.FormatDescription("read", null);
        Assert.Equal("📄 read", result);
    }

    [Fact]
    public void EmptyArgs_ShowsToolName()
    {
        var result = ToolDescriptionFormatter.FormatDescription("read", "");
        Assert.Equal("📄 read", result);
    }

    [Fact]
    public void InvalidJson_ShowsToolName()
    {
        var result = ToolDescriptionFormatter.FormatDescription("read", "not json");
        Assert.Equal("📄 read", result);
    }

    [Fact]
    public void AskUser_ShowsDescription()
    {
        var result = ToolDescriptionFormatter.FormatDescription("ask_user", """{"prompt": "Which option?"}""");
        Assert.Equal("❓ Ask user", result);
    }

    [Fact]
    public void SpawnSubagent_ShowsName()
    {
        var result = ToolDescriptionFormatter.FormatDescription("spawn_subagent", """{"task": "Review code", "name": "reviewer"}""");
        Assert.Equal("🤖 Spawn reviewer", result);
    }

    [Fact]
    public void SpawnSubagent_WithoutName_ShowsTask()
    {
        var result = ToolDescriptionFormatter.FormatDescription("spawn_subagent", """{"task": "Review code changes"}""");
        Assert.Equal("🤖 Spawn sub-agent", result);
    }

    [Fact]
    public void Conversation_ShowsAction()
    {
        var result = ToolDescriptionFormatter.FormatDescription("conversation", """{"action": "new"}""");
        Assert.Equal("💬 Conversation new", result);
    }

    [Fact]
    public void Sessions_ShowsAction()
    {
        var result = ToolDescriptionFormatter.FormatDescription("sessions", """{"action": "search", "query": "maintenance"}""");
        Assert.Equal("📋 Sessions search", result);
    }

    [Fact]
    public void SkillManage_ShowsAction()
    {
        var result = ToolDescriptionFormatter.FormatDescription("skill_manage", """{"action": "create", "name": "my-skill"}""");
        Assert.Equal("🛠️ Skill create my-skill", result);
    }

    [Fact]
    public void Skills_ShowsAction()
    {
        var result = ToolDescriptionFormatter.FormatDescription("skills", """{"action": "load", "skillName": "web-scraping"}""");
        Assert.Equal("📚 Load skill web-scraping", result);
    }

    [Fact]
    public void Delay_ShowsDuration()
    {
        var result = ToolDescriptionFormatter.FormatDescription("delay", """{"seconds": 30, "reason": "waiting for build"}""");
        Assert.Equal("⏳ Delay 30s", result);
    }

    [Fact]
    public void GetDatetime_ShowsDescription()
    {
        var result = ToolDescriptionFormatter.FormatDescription("get_datetime", """{}""");
        Assert.Equal("🕐 Get datetime", result);
    }

    [Fact]
    public void AgentConverse_ShowsTarget()
    {
        var result = ToolDescriptionFormatter.FormatDescription("agent_converse", """{"agentId": "nova", "message": "hello"}""");
        Assert.Equal("🗣️ Talk to nova", result);
    }
}
