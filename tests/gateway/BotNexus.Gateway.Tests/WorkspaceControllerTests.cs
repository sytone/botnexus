using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Api.Models;
using BotNexus.Gateway.Agents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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