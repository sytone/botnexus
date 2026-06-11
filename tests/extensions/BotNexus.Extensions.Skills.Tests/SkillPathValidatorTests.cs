using System.IO.Abstractions.TestingHelpers;
using BotNexus.Extensions.Skills.Security;

namespace BotNexus.Skills.Tests;

/// <summary>
/// Tests for <see cref="SkillPathValidator"/> — ensures symlink-based path traversal
/// is detected and rejected before writes occur.
/// </summary>
public sealed class SkillPathValidatorTests
{
    private const string SkillRoot = "/workspace/skills/my-skill";

    [Fact]
    public void TryValidate_NormalPath_Succeeds()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(SkillRoot);
        fs.AddDirectory($"{SkillRoot}/scripts");

        var target = $"{SkillRoot}/scripts/run.ps1";

        var result = SkillPathValidator.TryValidate(target, SkillRoot, fs, out var resolved, out var error);

        Assert.True(result);
        Assert.Null(error);
        Assert.Contains("my-skill", resolved);
    }

    [Fact]
    public void TryValidate_PathOutsideRoot_Fails()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(SkillRoot);

        var target = "/workspace/skills/other-skill/scripts/evil.ps1";

        var result = SkillPathValidator.TryValidate(target, SkillRoot, fs, out _, out var error);

        Assert.False(result);
        Assert.NotNull(error);
        Assert.Contains("escapes skill directory boundary", error);
    }

    [Fact]
    public void TryValidate_SymlinkDirectoryEscapes_Fails()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(SkillRoot);
        // Create a symlink directory inside the skill that points outside
        fs.AddDirectory("/etc/secrets");
        fs.Directory.CreateSymbolicLink($"{SkillRoot}/scripts", "/etc/secrets");

        var target = $"{SkillRoot}/scripts/passwords.txt";

        var result = SkillPathValidator.TryValidate(target, SkillRoot, fs, out _, out var error);

        Assert.False(result);
        Assert.NotNull(error);
        Assert.Contains("escapes skill directory boundary", error);
    }

    [Fact]
    public void TryValidate_SymlinkFileEscapes_Fails()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory($"{SkillRoot}/scripts");
        // Symlink file pointing outside
        fs.AddFile("/etc/shadow", new MockFileData("secret"));
        fs.File.CreateSymbolicLink($"{SkillRoot}/scripts/shadow", "/etc/shadow");

        var target = $"{SkillRoot}/scripts/shadow";

        var result = SkillPathValidator.TryValidate(target, SkillRoot, fs, out _, out var error);

        Assert.False(result);
        Assert.NotNull(error);
        Assert.Contains("escapes skill directory boundary", error);
    }

    [Fact]
    public void TryValidate_SymlinkWithinSkillDir_Succeeds()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory($"{SkillRoot}/templates");
        fs.AddDirectory($"{SkillRoot}/scripts");
        // Symlink inside skill dir pointing to another location inside skill dir
        fs.Directory.CreateSymbolicLink($"{SkillRoot}/scripts/shared", $"{SkillRoot}/templates");

        var target = $"{SkillRoot}/scripts/shared/default.md";

        var result = SkillPathValidator.TryValidate(target, SkillRoot, fs, out var resolved, out var error);

        Assert.True(result);
        Assert.Null(error);
    }

    [Fact]
    public void TryValidate_PathEqualToRoot_Succeeds()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(SkillRoot);

        var result = SkillPathValidator.TryValidate(SkillRoot, SkillRoot, fs, out _, out var error);

        Assert.True(result);
        Assert.Null(error);
    }

    [Fact]
    public void TryValidate_NewFileInExistingDir_Succeeds()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory($"{SkillRoot}/assets");
        // File doesn't exist yet but directory does and contains no symlinks
        var target = $"{SkillRoot}/assets/new-image.png";

        var result = SkillPathValidator.TryValidate(target, SkillRoot, fs, out _, out var error);

        Assert.True(result);
        Assert.Null(error);
    }
}
