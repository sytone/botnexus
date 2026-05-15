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
