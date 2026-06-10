using BotNexus.Gateway.Isolation;

namespace BotNexus.Gateway.Tests.Isolation;

/// <summary>
/// Tests for <see cref="SandboxSkillPathRewriter"/> which rewrites host-absolute
/// skill paths to sandbox-relative paths when agents run in Docker containers.
/// </summary>
public sealed class SandboxSkillPathRewriterTests
{
    [Fact]
    public void RewritePaths_ReplacesBackslashWindowsPath()
    {
        var content = @"Use skill at C:\Users\jobullen\.botnexus\skills\teams\scripts\SendMessageToChat.ps1";
        var hostDir = @"C:\Users\jobullen\.botnexus\skills";

        var result = SandboxSkillPathRewriter.RewritePaths(content, hostDir);

        Assert.Equal("Use skill at /workspace/skills/teams/scripts/SendMessageToChat.ps1", result);
    }

    [Fact]
    public void RewritePaths_ReplacesForwardSlashPath()
    {
        var content = "Use skill at C:/Users/jobullen/.botnexus/skills/teams/scripts/SendMessageToChat.ps1";
        var hostDir = @"C:\Users\jobullen\.botnexus\skills";

        var result = SandboxSkillPathRewriter.RewritePaths(content, hostDir);

        Assert.Equal("Use skill at /workspace/skills/teams/scripts/SendMessageToChat.ps1", result);
    }

    [Fact]
    public void RewritePaths_IsCaseInsensitive()
    {
        var content = @"Use skill at c:\users\JOBULLEN\.botnexus\skills\teams\scripts\Tool.ps1";
        var hostDir = @"C:\Users\jobullen\.botnexus\skills";

        var result = SandboxSkillPathRewriter.RewritePaths(content, hostDir);

        Assert.Equal("Use skill at /workspace/skills/teams/scripts/Tool.ps1", result);
    }

    [Fact]
    public void RewritePaths_ReplacesMultipleOccurrences()
    {
        var content = @"Call C:\Users\jobullen\.botnexus\skills\teams\scripts\Send.ps1 or C:\Users\jobullen\.botnexus\skills\mail\scripts\Read.ps1";
        var hostDir = @"C:\Users\jobullen\.botnexus\skills";

        var result = SandboxSkillPathRewriter.RewritePaths(content, hostDir);

        Assert.Equal("Call /workspace/skills/teams/scripts/Send.ps1 or /workspace/skills/mail/scripts/Read.ps1", result);
    }

    [Fact]
    public void RewritePaths_WithCustomSandboxPath()
    {
        var content = @"Script: C:\Users\jobullen\.botnexus\skills\calendar\scripts\List.ps1";
        var hostDir = @"C:\Users\jobullen\.botnexus\skills";

        var result = SandboxSkillPathRewriter.RewritePaths(content, hostDir, "/app/skills");

        Assert.Equal("Script: /app/skills/calendar/scripts/List.ps1", result);
    }

    [Fact]
    public void RewritePaths_PreservesContentWithoutHostPaths()
    {
        var content = "This content has no paths to rewrite.";
        var hostDir = @"C:\Users\jobullen\.botnexus\skills";

        var result = SandboxSkillPathRewriter.RewritePaths(content, hostDir);

        Assert.Equal("This content has no paths to rewrite.", result);
    }

    [Fact]
    public void RewritePaths_HandlesNullOrEmptyContent()
    {
        Assert.Equal("", SandboxSkillPathRewriter.RewritePaths("", @"C:\skills"));
        Assert.Equal("some text", SandboxSkillPathRewriter.RewritePaths("some text", ""));
    }

    [Fact]
    public void RewritePaths_HandlesLinuxHostPath()
    {
        var content = "Use /home/user/.botnexus/skills/teams/scripts/Send.ps1";
        var hostDir = "/home/user/.botnexus/skills";

        var result = SandboxSkillPathRewriter.RewritePaths(content, hostDir);

        Assert.Equal("Use /workspace/skills/teams/scripts/Send.ps1", result);
    }

    [Fact]
    public void RewriteMultiplePaths_RewritesAllKnownDirs()
    {
        var content = @"Global: C:\Users\jobullen\.botnexus\skills\teams\scripts\Send.ps1
Agent: C:\Users\jobullen\.botnexus\agents\farnsworth\skills\custom\scripts\Run.ps1
Workspace: C:\Users\jobullen\.botnexus\agents\farnsworth\workspace\skills\local\Tool.ps1";

        var hostDirs = new[]
        {
            @"C:\Users\jobullen\.botnexus\skills",
            @"C:\Users\jobullen\.botnexus\agents\farnsworth\skills",
            @"C:\Users\jobullen\.botnexus\agents\farnsworth\workspace\skills"
        };

        var result = SandboxSkillPathRewriter.RewriteMultiplePaths(content, hostDirs);

        Assert.Contains("/workspace/skills/teams/scripts/Send.ps1", result);
        Assert.Contains("/workspace/skills/custom/scripts/Run.ps1", result);
        Assert.Contains("/workspace/skills/local/Tool.ps1", result);
        Assert.DoesNotContain(@"C:\Users", result);
    }

    [Fact]
    public void RewriteMultiplePaths_SkipsNullAndEmptyDirs()
    {
        var content = @"Path: C:\Users\jobullen\.botnexus\skills\teams\Send.ps1";
        var hostDirs = new[] { "", null!, @"C:\Users\jobullen\.botnexus\skills" };

        var result = SandboxSkillPathRewriter.RewriteMultiplePaths(content, hostDirs);

        Assert.Equal("Path: /workspace/skills/teams/Send.ps1", result);
    }

    [Fact]
    public void DefaultSandboxSkillsPath_IsLinuxWorkspaceSkills()
    {
        Assert.Equal("/workspace/skills", SandboxSkillPathRewriter.DefaultSandboxSkillsPath);
    }
}
