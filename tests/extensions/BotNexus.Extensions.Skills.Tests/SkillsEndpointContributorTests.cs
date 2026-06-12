using System.IO.Abstractions.TestingHelpers;
using Microsoft.AspNetCore.Http;
using Shouldly;

namespace BotNexus.Extensions.Skills.Tests;

public sealed class SkillsEndpointContributorTests
{
    private const string SkillsRoot = "/home/user/.botnexus/skills";

    [Fact]
    public void GetSkillsRoot_WhenDirectoryDoesNotExist_ReturnsEmptyResponse()
    {
        var fs = new MockFileSystem();

        var result = SkillsEndpointContributor.GetSkillsRoot(fs, SkillsRoot);

        var okResult = result.ShouldBeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<SkillsDirectoryResponse>>();
        okResult.Value!.Entries.ShouldBeEmpty();
        okResult.Value.Path.ShouldBe(string.Empty);
    }

    [Fact]
    public void GetSkillsRoot_WhenDirectoryExists_ReturnsEntries()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [$"{SkillsRoot}/my-skill/SKILL.md"] = new MockFileData("# My Skill"),
            [$"{SkillsRoot}/my-skill/scripts/run.ps1"] = new MockFileData("Write-Host 'hello'")
        });

        var result = SkillsEndpointContributor.GetSkillsRoot(fs, SkillsRoot, depth: 2);

        var okResult = result.ShouldBeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<SkillsDirectoryResponse>>();
        okResult.Value!.Entries.Count.ShouldBe(1);
        okResult.Value.Entries[0].Name.ShouldBe("my-skill");
        okResult.Value.Entries[0].Type.ShouldBe("directory");
    }

    [Fact]
    public void GetSkillsRoot_InvalidDepth_ReturnsBadRequest()
    {
        var fs = new MockFileSystem();

        var result = SkillsEndpointContributor.GetSkillsRoot(fs, SkillsRoot, depth: -1);

        result.ShouldBeAssignableTo<Microsoft.AspNetCore.Http.IResult>();
        // Bad request for invalid depth
        result.GetType().Name.ShouldContain("BadRequest");
    }

    [Fact]
    public void GetSkillsPath_WhenFileExists_ReturnsTextContent()
    {
        var content = "# My Skill\nDescription here.";
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [$"{SkillsRoot}/my-skill/SKILL.md"] = new MockFileData(content)
        });

        var result = SkillsEndpointContributor.GetSkillsPath("my-skill/SKILL.md", fs, SkillsRoot);

        var okResult = result.ShouldBeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<SkillsFileResponse>>();
        okResult.Value!.Content.ShouldBe(content);
        okResult.Value.Type.ShouldBe("text");
        okResult.Value.Path.ShouldBe("my-skill/SKILL.md");
    }

    [Fact]
    public void GetSkillsPath_WhenDirectoryExists_ReturnsDirectoryListing()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [$"{SkillsRoot}/my-skill/SKILL.md"] = new MockFileData("content"),
            [$"{SkillsRoot}/my-skill/scripts/run.ps1"] = new MockFileData("script")
        });

        var result = SkillsEndpointContributor.GetSkillsPath("my-skill", fs, SkillsRoot);

        var okResult = result.ShouldBeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<SkillsDirectoryResponse>>();
        okResult.Value!.Type.ShouldBe("directory");
        okResult.Value.Entries.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void GetSkillsPath_AbsolutePath_ReturnsBadRequest()
    {
        var fs = new MockFileSystem();

        var result = SkillsEndpointContributor.GetSkillsPath("/etc/passwd", fs, SkillsRoot);

        result.GetType().Name.ShouldContain("BadRequest");
    }

    [Fact]
    public void GetSkillsPath_PathTraversal_ReturnsForbid()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [$"{SkillsRoot}/my-skill/SKILL.md"] = new MockFileData("content"),
            ["/home/user/secret.txt"] = new MockFileData("secret")
        });

        var result = SkillsEndpointContributor.GetSkillsPath("../secret.txt", fs, SkillsRoot);

        result.GetType().Name.ShouldContain("Forbid");
    }

    [Fact]
    public void GetSkillsPath_NonExistentFile_ReturnsNotFound()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [$"{SkillsRoot}/my-skill/SKILL.md"] = new MockFileData("content")
        });

        var result = SkillsEndpointContributor.GetSkillsPath("my-skill/missing.md", fs, SkillsRoot);

        result.GetType().Name.ShouldContain("NotFound");
    }

    [Fact]
    public void GetSkillsPath_NullCharInPath_ReturnsBadRequest()
    {
        var fs = new MockFileSystem();

        var result = SkillsEndpointContributor.GetSkillsPath("my-skill\0/SKILL.md", fs, SkillsRoot);

        result.GetType().Name.ShouldContain("BadRequest");
    }

    [Fact]
    public void DeleteSkillsPath_WhenFileExists_DeletesAndReturnsNoContent()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [$"{SkillsRoot}/my-skill/temp.md"] = new MockFileData("temporary")
        });

        var result = SkillsEndpointContributor.DeleteSkillsPath("my-skill/temp.md", fs, SkillsRoot);

        result.GetType().Name.ShouldContain("NoContent");
        fs.File.Exists($"{SkillsRoot}/my-skill/temp.md").ShouldBeFalse();
    }

    [Fact]
    public void DeleteSkillsPath_NonEmptyDirectoryWithoutForce_ReturnsConflict()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [$"{SkillsRoot}/my-skill/SKILL.md"] = new MockFileData("content")
        });

        var result = SkillsEndpointContributor.DeleteSkillsPath("my-skill", fs, SkillsRoot, force: false);

        result.GetType().Name.ShouldContain("Conflict");
    }

    [Fact]
    public void DeleteSkillsPath_NonEmptyDirectoryWithForce_Deletes()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [$"{SkillsRoot}/my-skill/SKILL.md"] = new MockFileData("content")
        });

        var result = SkillsEndpointContributor.DeleteSkillsPath("my-skill", fs, SkillsRoot, force: true);

        result.GetType().Name.ShouldContain("NoContent");
        fs.Directory.Exists($"{SkillsRoot}/my-skill").ShouldBeFalse();
    }

    [Fact]
    public void DeleteSkillsPath_NonExistentFile_ReturnsNotFound()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [$"{SkillsRoot}/my-skill/SKILL.md"] = new MockFileData("content")
        });

        var result = SkillsEndpointContributor.DeleteSkillsPath("my-skill/missing.md", fs, SkillsRoot);

        result.GetType().Name.ShouldContain("NotFound");
    }

    [Fact]
    public void DeleteSkillsPath_PathTraversal_ReturnsForbid()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [$"{SkillsRoot}/my-skill/SKILL.md"] = new MockFileData("content"),
            ["/home/user/important.txt"] = new MockFileData("important")
        });

        var result = SkillsEndpointContributor.DeleteSkillsPath("../../important.txt", fs, SkillsRoot);

        result.GetType().Name.ShouldContain("Forbid");
    }
}
