using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Api.Models;
using BotNexus.Gateway.Agents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests;

public sealed class WorkspaceControllerTests
{
    [Fact]
    public void GetWorkspace_WithFilesAndDirectories_ReturnsDepthLimitedTree()
    {
        const string workspacePath = @"C:\workspace\agent-a";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Path.Combine(workspacePath, "SOUL.md")] = new("soul"),
            [Path.Combine(workspacePath, "memory", "2026-05-15.md")] = new("entry"),
            [Path.Combine(workspacePath, "memory", "archive", "old.md")] = new("archived")
        });

        var controller = CreateController(fileSystem, workspacePath);

        var result = controller.GetWorkspace("agent-a", depth: 1);

        var payload = (result.Result as OkObjectResult)?.Value.ShouldBeOfType<WorkspaceDirectoryResponse>();
        payload.ShouldNotBeNull();
        payload!.DepthLimit.ShouldBe(1);
        payload.Entries.ShouldContain(entry => entry.Path == "SOUL.md" && entry.Type == "file");
        payload.Entries.ShouldContain(entry =>
            entry.Path == "memory"
            && entry.Type == "directory"
            && entry.Children.Any(child => child.Path == "memory/2026-05-15.md")
            && entry.Children.Any(child => child.Path == "memory/archive" && child.Type == "directory"));
    }

    [Fact]
    public void GetWorkspace_WhenWorkspaceMissing_ReturnsEmptyTree()
    {
        const string workspacePath = @"C:\workspace\agent-a";
        var fileSystem = new MockFileSystem();
        var controller = CreateController(fileSystem, workspacePath);

        var result = controller.GetWorkspace("agent-a", depth: 2);

        var payload = (result.Result as OkObjectResult)?.Value.ShouldBeOfType<WorkspaceDirectoryResponse>();
        payload.ShouldNotBeNull();
        payload!.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void GetFile_WhenFileExists_ReturnsFileContent()
    {
        const string workspacePath = @"C:\workspace\agent-a";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Path.Combine(workspacePath, "notes", "today.md")] = new("hello workspace")
        });
        var controller = CreateController(fileSystem, workspacePath);

        var result = controller.GetFile("agent-a", @"notes\today.md");

        var payload = (result.Result as OkObjectResult)?.Value.ShouldBeOfType<WorkspaceFileResponse>();
        payload.ShouldNotBeNull();
        payload!.Path.ShouldBe("notes/today.md");
        payload.Type.ShouldBe("text");
        payload.Content.ShouldBe("hello workspace");
        payload.Encoding.ShouldBe("utf-8");
        payload.IsTruncated.ShouldBeFalse();
    }

    [Fact]
    public void GetFile_WhenFileMissing_ReturnsNotFound()
    {
        const string workspacePath = @"C:\workspace\agent-a";
        var fileSystem = new MockFileSystem();
        var controller = CreateController(fileSystem, workspacePath);

        var result = controller.GetFile("agent-a", "missing.md");

        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public void GetFile_WhenPathIsDirectory_ReturnsDirectoryPayload()
    {
        const string workspacePath = @"C:\workspace\agent-a";
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Path.Combine(workspacePath, "folder", "child.md")] = new("child")
        });
        var controller = CreateController(fileSystem, workspacePath);

        var result = controller.GetFile("agent-a", "folder");

        var payload = (result.Result as OkObjectResult)?.Value.ShouldBeOfType<WorkspaceDirectoryResponse>();
        payload.ShouldNotBeNull();
        payload!.Type.ShouldBe("directory");
        payload.Path.ShouldBe("folder");
        payload.DepthLimit.ShouldBe(0);
        payload.Entries.ShouldContain(entry => entry.Path == "folder/child.md" && entry.Type == "file");
    }

    private static WorkspaceController CreateController(MockFileSystem fileSystem, string workspacePath)
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(new AgentDescriptor
        {
            AgentId = AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "gpt-4.1",
            ApiProvider = "openai"
        });

        var workspaceManager = new Mock<IAgentWorkspaceManager>();
        workspaceManager.Setup(manager => manager.GetWorkspacePath("agent-a")).Returns(workspacePath);

        return new WorkspaceController(registry, workspaceManager.Object, fileSystem);
    }
}

/// <summary>
/// Regression coverage for #2333: entries deleted between enumeration and the lazy stat
/// must be skipped, not abort the whole listing with a 500.
/// </summary>
public sealed class WorkspaceControllerTocTouTests
{
    private const string WorkspacePath = @"C:\workspace\agent-a";

    [Fact]
    public void GetWorkspace_WhenFileVanishesBeforeStat_SkipsEntryAndReturnsOk()
    {
        var vanishedPath = Path.Combine(WorkspacePath, "tmp", "scratch.txt");
        var inner = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Path.Combine(WorkspacePath, "SOUL.md")] = new("soul"),
            [Path.Combine(WorkspacePath, "tmp", "keep.txt")] = new("keep"),
            [vanishedPath] = new("about to be deleted")
        });

        var vanishTriggers = new List<string>();
        var fileSystem = CreateFileSystem(inner, vanishedFilePath: vanishedPath, vanishedDirectoryPath: null, vanishTriggers);
        var controller = CreateController(fileSystem, WorkspacePath);

        var result = controller.GetWorkspace("agent-a", depth: 2);

        var payload = (result.Result as OkObjectResult)?.Value.ShouldBeOfType<WorkspaceDirectoryResponse>();
        payload.ShouldNotBeNull();
        payload!.Entries.ShouldContain(entry => entry.Path == "SOUL.md");

        var tmp = payload.Entries.Single(entry => entry.Path == "tmp");
        tmp.Children.ShouldContain(child => child.Path == "tmp/keep.txt");
        tmp.Children.ShouldNotContain(child => child.Path == "tmp/scratch.txt");

        // Guard against a vacuous pass: if the mock never matched the path (as happened on Linux
        // when the comparison was separator-sensitive) nothing actually vanished and the
        // assertions above prove nothing.
        vanishTriggers.ShouldNotBeEmpty();
    }

    [Fact]
    public void GetWorkspace_WhenDirectoryVanishesMidWalk_ReturnsOkWithEmptyChildren()
    {
        var vanishedDirectory = Path.Combine(WorkspacePath, "tmp");
        var inner = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Path.Combine(WorkspacePath, "SOUL.md")] = new("soul"),
            [Path.Combine(vanishedDirectory, "scratch.txt")] = new("scratch")
        });

        var vanishTriggers = new List<string>();
        var fileSystem = CreateFileSystem(inner, vanishedFilePath: null, vanishedDirectoryPath: vanishedDirectory, vanishTriggers);
        var controller = CreateController(fileSystem, WorkspacePath);

        var result = controller.GetWorkspace("agent-a", depth: 2);

        var payload = (result.Result as OkObjectResult)?.Value.ShouldBeOfType<WorkspaceDirectoryResponse>();
        payload.ShouldNotBeNull();
        payload!.Entries.ShouldContain(entry => entry.Path == "SOUL.md");

        var tmp = payload.Entries.Single(entry => entry.Path == "tmp" && entry.Type == "directory");
        tmp.Children.ShouldBeEmpty();

        // Guard against a vacuous pass (see the file-vanish test).
        vanishTriggers.ShouldNotBeEmpty();
    }

    /// <summary>
    /// Wraps a <see cref="MockFileSystem"/> so a single file's lazy <c>Length</c> stat, or a single
    /// directory's enumeration, fails the way a concurrently deleted entry does on a real filesystem.
    /// </summary>
    private static IFileSystem CreateFileSystem(
        MockFileSystem inner,
        string? vanishedFilePath,
        string? vanishedDirectoryPath,
        List<string> vanishTriggers)
    {
        var directory = new Mock<IDirectory>();
        directory
            .Setup(target => target.Exists(It.IsAny<string?>()))
            .Returns((string? path) => inner.Directory.Exists(path));
        directory
            .Setup(target => target.EnumerateFileSystemEntries(It.IsAny<string>()))
            .Returns((string path) =>
            {
                if (!IsSamePath(path, vanishedDirectoryPath))
                {
                    return inner.Directory.EnumerateFileSystemEntries(path);
                }

                vanishTriggers.Add(path);
                throw new DirectoryNotFoundException(path);
            });

        var vanishedFile = new Mock<IFileInfo>();
        vanishedFile.Setup(target => target.LinkTarget).Returns((string?)null);
        vanishedFile.Setup(target => target.Length).Throws(new FileNotFoundException("deleted", vanishedFilePath));

        var fileInfoFactory = new Mock<IFileInfoFactory>();
        fileInfoFactory
            .Setup(target => target.New(It.IsAny<string>()))
            .Returns((string path) =>
            {
                if (!IsSamePath(path, vanishedFilePath))
                {
                    return inner.FileInfo.New(path);
                }

                vanishTriggers.Add(path);
                return vanishedFile.Object;
            });

        var fileSystem = new Mock<IFileSystem>();
        fileSystem.Setup(target => target.Path).Returns(inner.Path);
        fileSystem.Setup(target => target.File).Returns(inner.File);
        fileSystem.Setup(target => target.Directory).Returns(directory.Object);
        fileSystem.Setup(target => target.DirectoryInfo).Returns(inner.DirectoryInfo);
        fileSystem.Setup(target => target.FileInfo).Returns(fileInfoFactory.Object);
        return fileSystem.Object;
    }

    /// <summary>
    /// Compares two paths without depending on the host's directory separator.
    /// <para>
    /// The tests use Windows-style literals (<c>C:\workspace\agent-a</c>), but on Linux
    /// <see cref="Path.Combine(string, string)"/> joins with <c>/</c> while
    /// <see cref="MockFileSystem"/> hands back a normalised, leading-slash form
    /// (<c>/C:\workspace\agent-a/tmp/scratch.txt</c>). An ordinal comparison therefore never
    /// matches off-Windows, the vanish is never simulated, and the assertions pass vacuously
    /// on Windows while failing on the Linux CI runner. Normalise both separators and ignore a
    /// leading slash so the mock triggers identically on every platform.
    /// </para>
    /// </summary>
    private static bool IsSamePath(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        return string.Equals(Normalise(left), Normalise(right), StringComparison.Ordinal);

        static string Normalise(string path) =>
            path.Replace('\\', '/').TrimStart('/');
    }

    private static WorkspaceController CreateController(IFileSystem fileSystem, string workspacePath)
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(new AgentDescriptor
        {
            AgentId = AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "gpt-4.1",
            ApiProvider = "openai"
        });

        var workspaceManager = new Mock<IAgentWorkspaceManager>();
        workspaceManager.Setup(manager => manager.GetWorkspacePath("agent-a")).Returns(workspacePath);

        return new WorkspaceController(registry, workspaceManager.Object, fileSystem);
    }
}

public sealed class WorkspaceControllerDeleteTests
{
    private const string WorkspacePath = @"C:\workspace\agent-a";

    // ── happy paths ──────────────────────────────────────────────────────────

    [Fact]
    public void DeleteItem_WhenFileExists_Returns204()
    {
        var filePath = Path.Combine(WorkspacePath, "notes.md");
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [filePath] = new("hello")
        });
        var controller = CreateController(fileSystem, WorkspacePath);

        var result = controller.DeleteItem("agent-a", "notes.md", force: false);

        result.ShouldBeOfType<NoContentResult>();
        fileSystem.File.Exists(filePath).ShouldBeFalse();
    }

    [Fact]
    public void DeleteItem_WhenEmptyDirectoryExists_Returns204()
    {
        var dirPath = Path.Combine(WorkspacePath, "emptydir");
        var fileSystem = new MockFileSystem();
        fileSystem.Directory.CreateDirectory(dirPath);
        var controller = CreateController(fileSystem, WorkspacePath);

        var result = controller.DeleteItem("agent-a", "emptydir", force: false);

        result.ShouldBeOfType<NoContentResult>();
        fileSystem.Directory.Exists(dirPath).ShouldBeFalse();
    }

    [Fact]
    public void DeleteItem_WhenNonEmptyDirectoryAndForceTrue_Returns204()
    {
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Path.Combine(WorkspacePath, "logs", "app.log")] = new("log data")
        });
        var controller = CreateController(fileSystem, WorkspacePath);

        var result = controller.DeleteItem("agent-a", "logs", force: true);

        result.ShouldBeOfType<NoContentResult>();
        fileSystem.Directory.Exists(Path.Combine(WorkspacePath, "logs")).ShouldBeFalse();
    }

    // ── sad paths ─────────────────────────────────────────────────────────────

    [Fact]
    public void DeleteItem_WhenAgentUnknown_Returns404()
    {
        var controller = CreateController(new MockFileSystem(), WorkspacePath);

        var result = controller.DeleteItem("unknown-agent", "notes.md", force: false);

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public void DeleteItem_WhenPathEmpty_Returns400()
    {
        var controller = CreateController(new MockFileSystem(), WorkspacePath);

        var result = controller.DeleteItem("agent-a", "   ", force: false);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void DeleteItem_WhenPathRooted_Returns400()
    {
        var controller = CreateController(new MockFileSystem(), WorkspacePath);

        var result = controller.DeleteItem("agent-a", @"C:\absolute\path.md", force: false);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void DeleteItem_WhenPathContainsNullByte_Returns400()
    {
        var controller = CreateController(new MockFileSystem(), WorkspacePath);

        var result = controller.DeleteItem("agent-a", "bad\0path", force: false);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void DeleteItem_WhenFileDoesNotExist_Returns404()
    {
        var controller = CreateController(new MockFileSystem(new Dictionary<string, MockFileData>
        {
            // workspace root exists but target file does not
            [Path.Combine(WorkspacePath, "other.md")] = new("x")
        }), WorkspacePath);

        var result = controller.DeleteItem("agent-a", "missing.md", force: false);

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public void DeleteItem_WhenNonEmptyDirectoryAndForceFalse_Returns409()
    {
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Path.Combine(WorkspacePath, "logs", "app.log")] = new("log data")
        });
        var controller = CreateController(fileSystem, WorkspacePath);

        var result = controller.DeleteItem("agent-a", "logs", force: false);

        result.ShouldBeOfType<ConflictObjectResult>();
    }

    private static WorkspaceController CreateController(MockFileSystem fileSystem, string workspacePath)
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(new AgentDescriptor
        {
            AgentId = AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "gpt-4.1",
            ApiProvider = "openai"
        });

        var workspaceManager = new Mock<IAgentWorkspaceManager>();
        workspaceManager.Setup(manager => manager.GetWorkspacePath("agent-a")).Returns(workspacePath);

        return new WorkspaceController(registry, workspaceManager.Object, fileSystem);
    }
}

public sealed class WorkspaceControllerWriteTests
{
    private const string WorkspacePath = @"C:\workspace\agent-a";

    // ── happy paths ──────────────────────────────────────────────────────────

    [Fact]
    public void WriteFile_WhenNewFile_Returns204AndFileExists()
    {
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            // workspace root must exist; create a dummy so directory exists
            [Path.Combine(WorkspacePath, ".keep")] = new(string.Empty)
        });
        var controller = CreateController(fileSystem, WorkspacePath);

        var result = controller.WriteFile("agent-a", "newfile.md",
            new BotNexus.Gateway.Api.Models.WorkspaceWriteRequest { Content = "# Hello" });

        result.ShouldBeOfType<NoContentResult>();
        fileSystem.File.ReadAllText(Path.Combine(WorkspacePath, "newfile.md")).ShouldBe("# Hello");
    }

    [Fact]
    public void WriteFile_WhenExistingFile_OverwritesContent()
    {
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Path.Combine(WorkspacePath, "notes.md")] = new("old content")
        });
        var controller = CreateController(fileSystem, WorkspacePath);

        var result = controller.WriteFile("agent-a", "notes.md",
            new BotNexus.Gateway.Api.Models.WorkspaceWriteRequest { Content = "new content" });

        result.ShouldBeOfType<NoContentResult>();
        fileSystem.File.ReadAllText(Path.Combine(WorkspacePath, "notes.md")).ShouldBe("new content");
    }

    [Fact]
    public void WriteFile_NoBomWritten_RawBytesDoNotStartWithUtf8BomBytes()
    {
        // Regression for #869: Encoding.UTF8 emits a UTF-8 BOM on Windows which breaks
        // YAML frontmatter parsers (SkillParser, YAML loaders). The workspace editor must
        // write BOM-free UTF-8 so any consumer can parse the file without special handling.
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Path.Combine(WorkspacePath, ".keep")] = new(string.Empty)
        });
        var controller = CreateController(fileSystem, WorkspacePath);

        controller.WriteFile("agent-a", "skill.md",
            new BotNexus.Gateway.Api.Models.WorkspaceWriteRequest { Content = "---\nname: test\n---\nbody" });

        var bytes = fileSystem.File.ReadAllBytes(Path.Combine(WorkspacePath, "skill.md"));
        // UTF-8 BOM is 0xEF 0xBB 0xBF
        bytes.Length.ShouldBeGreaterThan(0);
        (bytes[0] == 0xEF && bytes.Length > 2 && bytes[1] == 0xBB && bytes[2] == 0xBF).ShouldBeFalse(
            "WriteFile must not emit a UTF-8 BOM");
        // Verify content is readable (no invisible prefix)
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        text.ShouldStartWith("---");
    }

    [Fact]
    public void WriteFile_ContentWithLeadingBom_BomNotDoubled()
    {
        // If content arrives already containing a BOM (from a client-side quirk), WriteAllText
        // with BOM-free encoding must not add a second BOM. The written bytes should contain
        // exactly one BOM sequence.
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Path.Combine(WorkspacePath, ".keep")] = new(string.Empty)
        });
        var controller = CreateController(fileSystem, WorkspacePath);

        // Content with an explicit BOM prefix
        var contentWithBom = "\uFEFF# title";
        controller.WriteFile("agent-a", "bom-input.md",
            new BotNexus.Gateway.Api.Models.WorkspaceWriteRequest { Content = contentWithBom });

        var bytes = fileSystem.File.ReadAllBytes(Path.Combine(WorkspacePath, "bom-input.md"));
        // BOM-free encoding writes the content as-is. If content had a BOM character, it is
        // preserved as a Unicode code point (3 bytes 0xEF 0xBB 0xBF) but there is exactly one.
        // Double-BOM would be 6 bytes of BOM at the start, which should never occur.
        var bomCount = 0;
        for (var i = 0; i <= bytes.Length - 3; i++)
        {
            if (bytes[i] == 0xEF && bytes[i + 1] == 0xBB && bytes[i + 2] == 0xBF)
                bomCount++;
        }
        bomCount.ShouldBeLessThanOrEqualTo(1, "WriteFile must not double-add a BOM");
    }

    // ── sad paths ─────────────────────────────────────────────────────────────

    [Fact]
    public void WriteFile_WhenAgentUnknown_Returns404()
    {
        var controller = CreateController(new MockFileSystem(), WorkspacePath);

        var result = controller.WriteFile("unknown-agent", "notes.md",
            new BotNexus.Gateway.Api.Models.WorkspaceWriteRequest { Content = "x" });

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public void WriteFile_WhenPathEmpty_Returns400()
    {
        var controller = CreateController(new MockFileSystem(), WorkspacePath);

        var result = controller.WriteFile("agent-a", "  ",
            new BotNexus.Gateway.Api.Models.WorkspaceWriteRequest { Content = "x" });

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void WriteFile_WhenPathRooted_Returns400()
    {
        var controller = CreateController(new MockFileSystem(), WorkspacePath);

        var result = controller.WriteFile("agent-a", @"C:\absolute\file.md",
            new BotNexus.Gateway.Api.Models.WorkspaceWriteRequest { Content = "x" });

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void WriteFile_WhenPathContainsNullByte_Returns400()
    {
        var controller = CreateController(new MockFileSystem(), WorkspacePath);

        var result = controller.WriteFile("agent-a", "bad\0file",
            new BotNexus.Gateway.Api.Models.WorkspaceWriteRequest { Content = "x" });

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void WriteFile_WhenPathIsDirectory_Returns400()
    {
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Path.Combine(WorkspacePath, "subdir", "child.md")] = new("x")
        });
        var controller = CreateController(fileSystem, WorkspacePath);

        var result = controller.WriteFile("agent-a", "subdir",
            new BotNexus.Gateway.Api.Models.WorkspaceWriteRequest { Content = "x" });

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void WriteFile_WhenParentDirectoryMissing_Returns400()
    {
        // Only workspace root exists, no "nonexistent" subdirectory
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [Path.Combine(WorkspacePath, ".keep")] = new(string.Empty)
        });
        var controller = CreateController(fileSystem, WorkspacePath);

        var result = controller.WriteFile("agent-a", "nonexistent/file.md",
            new BotNexus.Gateway.Api.Models.WorkspaceWriteRequest { Content = "x" });

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    private static WorkspaceController CreateController(MockFileSystem fileSystem, string workspacePath)
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(new AgentDescriptor
        {
            AgentId = AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "gpt-4.1",
            ApiProvider = "openai"
        });

        var workspaceManager = new Mock<IAgentWorkspaceManager>();
        workspaceManager.Setup(manager => manager.GetWorkspacePath("agent-a")).Returns(workspacePath);

        return new WorkspaceController(registry, workspaceManager.Object, fileSystem);
    }
}

public sealed class WorkspaceControllerProtectedFileTests
{
    private const string WorkspacePath = @"C:\workspace\agent-a";

    [Theory]
    [InlineData("SOUL.md")]
    [InlineData("soul.md")]  // case-insensitive
    [InlineData("IDENTITY.md")]
    [InlineData("MEMORY.md")]
    [InlineData("AGENTS.md")]
    [InlineData("USER.md")]
    [InlineData("WORLD.md")]
    [InlineData("TOOLS.md")]
    [InlineData("HEARTBEAT.md")]
    [InlineData("heartbeat.md")]  // case-insensitive
    public void DeleteItem_ProtectedFile_Returns403(string fileName)
    {
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [System.IO.Path.Combine(WorkspacePath, fileName)] = new("protected content")
        });
        var controller = CreateController(fileSystem, WorkspacePath);

        var result = controller.DeleteItem("agent-a", fileName, force: false);

        var statusResult = result.ShouldBeOfType<ObjectResult>();
        statusResult.StatusCode.ShouldBe(403);
        // File should still exist (not deleted)
        fileSystem.File.Exists(System.IO.Path.Combine(WorkspacePath, fileName)).ShouldBeTrue();
    }

    [Fact]
    public void DeleteItem_ProtectedFile_InSubdirectory_Returns403()
    {
        // Protection is filename-based, so playbook/SOUL.md is also protected
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [System.IO.Path.Combine(WorkspacePath, "playbook", "SOUL.md")] = new("protected")
        });
        var controller = CreateController(fileSystem, WorkspacePath);

        var result = controller.DeleteItem("agent-a", "playbook/SOUL.md", force: false);

        var statusResult = result.ShouldBeOfType<ObjectResult>();
        statusResult.StatusCode.ShouldBe(403);
    }

    [Fact]
    public void DeleteItem_NonProtectedFile_DeletesSuccessfully()
    {
        var fileSystem = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [System.IO.Path.Combine(WorkspacePath, "notes.md")] = new("hello")
        });
        var controller = CreateController(fileSystem, WorkspacePath);

        var result = controller.DeleteItem("agent-a", "notes.md", force: false);

        result.ShouldBeOfType<NoContentResult>();
        fileSystem.File.Exists(System.IO.Path.Combine(WorkspacePath, "notes.md")).ShouldBeFalse();
    }

    [Fact]
    public void ProtectedFiles_Set_ContainsAllExpectedFiles()
    {
        // Ensure the protected set is consistent and contains the expected entries
        WorkspaceController.ProtectedFiles.ShouldContain("SOUL.md");
        WorkspaceController.ProtectedFiles.ShouldContain("IDENTITY.md");
        WorkspaceController.ProtectedFiles.ShouldContain("MEMORY.md");
        WorkspaceController.ProtectedFiles.ShouldContain("AGENTS.md");
        WorkspaceController.ProtectedFiles.ShouldContain("USER.md");
        WorkspaceController.ProtectedFiles.ShouldContain("WORLD.md");
        WorkspaceController.ProtectedFiles.ShouldContain("TOOLS.md");
        WorkspaceController.ProtectedFiles.ShouldContain("HEARTBEAT.md");
    }

    private static WorkspaceController CreateController(MockFileSystem fileSystem, string workspacePath)
    {
        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(new AgentDescriptor
        {
            AgentId = AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "gpt-4.1",
            ApiProvider = "openai"
        });

        var workspaceManager = new Moq.Mock<IAgentWorkspaceManager>();
        workspaceManager.Setup(manager => manager.GetWorkspacePath("agent-a")).Returns(workspacePath);

        return new WorkspaceController(registry, workspaceManager.Object, fileSystem);
    }
}