using BotNexus.Tools;

namespace BotNexus.Gateway.Tests.Tools;

/// <summary>
/// Verifies the skill-wrapper not-found diagnostics (issue #2758) are wired into <see cref="ShellTool"/>.
/// A <c>pwsh -File</c> invocation naming a wrapper that does not exist must be rejected BEFORE a
/// process is spawned, with a message that names the skill and lists the closest existing wrapper
/// names - not <c>pwsh</c>'s bare usage banner, which names neither.
/// </summary>
public sealed class ShellToolSkillScriptPreflightTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "bn-2758-" + Guid.NewGuid().ToString("N"));

    private string CreateTeamsSkill()
    {
        var scripts = Path.Combine(_root, "skills", "teams", "scripts");
        Directory.CreateDirectory(scripts);
        foreach (var name in new[] { "ListChatMessages.ps1", "ListChannelMessages.ps1", "GetChatMessage.ps1", "SendMessageToChat.ps1" })
        {
            File.WriteAllText(Path.Combine(scripts, name), "# wrapper");
        }

        return scripts;
    }

    [Fact]
    public async Task ExecuteAsync_MissingSkillWrapper_NamesSkillAndClosestMatches()
    {
        var scripts = CreateTeamsSkill();
        var tool = new ShellTool(shellPreference: ShellPreference.Pwsh);
        var missing = Path.Combine(scripts, "ListMessages.ps1");

        var ex = await Should.ThrowAsync<ArgumentException>(() => tool.ExecuteAsync(
            "skill-preflight",
            new Dictionary<string, object?> { ["command"] = $"pwsh -NoProfile -File '{missing}'" }));

        ex.Message.ShouldContain("teams");
        ex.Message.ShouldContain("ListMessages.ps1");
        ex.Message.ShouldContain("ListChatMessages.ps1");
        ex.Message.ShouldContain("ListChannelMessages.ps1");
        // A near match is reported, never silently executed in place of the request.
        ex.Message.Contains("NOT executed", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_MissingNonSkillScript_ReportsPlainNotFound()
    {
        Directory.CreateDirectory(_root);
        var tool = new ShellTool(shellPreference: ShellPreference.Pwsh);
        var missing = Path.Combine(_root, "fq.ps1");

        var ex = await Should.ThrowAsync<ArgumentException>(() => tool.ExecuteAsync(
            "generic-preflight",
            new Dictionary<string, object?> { ["command"] = $"pwsh -NoProfile -File '{missing}'" }));

        ex.Message.ShouldContain("fq.ps1");
        // AC5: no bogus candidate list outside a skill directory.
        ex.Message.Contains("Closest matches", StringComparison.Ordinal).ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_ExistingSkillWrapper_ExecutesUnchanged()
    {
        var scripts = CreateTeamsSkill();
        var real = Path.Combine(scripts, "Echo.ps1");
        File.WriteAllText(real, "Write-Output 'wrapper-ok'");
        var tool = new ShellTool(shellPreference: ShellPreference.Pwsh);

        var result = await tool.ExecuteAsync(
            "wrapper-ok",
            new Dictionary<string, object?> { ["command"] = $"pwsh -NoProfile -File '{real}'" });

        result.Content[0].Value.ShouldContain("wrapper-ok");
    }

    [Fact]
    public async Task ExecuteAsync_InlineCommand_IsNotTreatedAsAFileTarget()
    {
        var tool = new ShellTool(shellPreference: ShellPreference.Pwsh);

        var result = await tool.ExecuteAsync(
            "inline-ok",
            new Dictionary<string, object?> { ["command"] = "Write-Output 'inline-ok'" });

        result.Content[0].Value.ShouldContain("inline-ok");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
