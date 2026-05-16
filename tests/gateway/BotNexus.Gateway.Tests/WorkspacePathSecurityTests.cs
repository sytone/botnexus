using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests;

public sealed class WorkspacePathSecurityTests
{
    [Fact]
    public void GetFile_WithParentTraversal_ReturnsForbidden()
    {
        var (controller, _, workspacePath) = CreateController();

        var result = controller.GetFile("agent-a", "..\\outside.md");

        result.Result.ShouldBeOfType<ForbidResult>();
    }

    [Fact]
    public void GetFile_WithUnixParentTraversal_ReturnsForbidden()
    {
        var (controller, _, _) = CreateController();

        var result = controller.GetFile("agent-a", "../outside.md");

        result.Result.ShouldBeOfType<ForbidResult>();
    }

    [Fact]
    public void GetFile_WithAbsolutePath_ReturnsBadRequest()
    {
        var (controller, _, _) = CreateController();
        var absolutePath = Path.GetFullPath(Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory)!, "outside.md"));

        var result = controller.GetFile("agent-a", absolutePath);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void GetFile_WithUnknownAgent_ReturnsNotFound()
    {
        var (controller, _, _) = CreateController();

        var result = controller.GetFile("missing-agent", "notes.md");

        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public void GetFile_WithNulByte_ReturnsBadRequest()
    {
        var (controller, _, _) = CreateController();

        var result = controller.GetFile("agent-a", $"notes.md{'\0'}");

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void GetWorkspace_WithInvalidDepth_ReturnsBadRequest()
    {
        var (controller, _, _) = CreateController();

        var result = controller.GetWorkspace("agent-a", depth: 99);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void GetFile_WithWorkspaceSymlinkEscape_ReturnsForbidden()
    {
        var (controller, fileSystem, workspacePath) = CreateController();
        var outsideTarget = NormalizePath(fileSystem, @"C:\outside\secrets.txt");
        fileSystem.AddFile(outsideTarget, new MockFileData("secret"));

        var workspaceLink = NormalizePath(fileSystem, Path.Combine(workspacePath, "escape-link"));
        fileSystem.Directory.CreateDirectory(workspacePath);
        fileSystem.Directory.CreateSymbolicLink(workspaceLink, outsideTarget);

        var result = controller.GetFile("agent-a", "escape-link");

        result.Result.ShouldBeOfType<ForbidResult>();
    }

    [Fact]
    public void GetReport_WithReportTraversal_ReturnsBadRequest()
    {
        var (controller, _, _, _) = CreateReportsController();

        var result = controller.GetReport("agent-a", "..\\outside.md");

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void GetReport_WithReportSymlinkEscape_ReturnsForbidden()
    {
        var (controller, fileSystem, _, reportsPath) = CreateReportsController();
        var outsideTarget = NormalizePath(fileSystem, @"C:\outside\reports\secret.md");
        fileSystem.AddFile(outsideTarget, new MockFileData("secret"));

        var escapePath = NormalizePath(fileSystem, Path.Combine(reportsPath, "escape.md"));
        fileSystem.File.CreateSymbolicLink(escapePath, outsideTarget);

        var result = controller.GetReport("agent-a", "escape.md");

        var payload = result.Result.ShouldBeOfType<StatusCodeResult>();
        payload.StatusCode.ShouldBe(403);
    }

    private static (ReportsController Controller, MockFileSystem FileSystem, string WorkspacePath, string ReportsPath) CreateReportsController()
    {
        var fileSystem = new MockFileSystem();
        var botNexusRoot = NormalizePath(fileSystem, @"C:\botnexus-home");
        var botNexusHome = new BotNexusHome(fileSystem, botNexusRoot);
        var workspaceManager = new FileAgentWorkspaceManager(botNexusHome, fileSystem);
        var workspacePath = workspaceManager.GetWorkspacePath("agent-a");
        var reportsPath = NormalizePath(fileSystem, Path.Combine(workspacePath, "reports"));
        fileSystem.Directory.CreateDirectory(reportsPath);
        fileSystem.AddFile(NormalizePath(fileSystem, Path.Combine(reportsPath, "weekly.md")), new MockFileData("weekly"));

        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(new AgentDescriptor
        {
            AgentId = AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "gpt-4.1",
            ApiProvider = "openai"
        });

        var controller = new ReportsController(registry, workspaceManager, fileSystem);
        return (controller, fileSystem, workspacePath, reportsPath);
    }

    private static (WorkspaceController Controller, MockFileSystem FileSystem, string WorkspacePath) CreateController()
    {
        var fileSystem = new MockFileSystem();
        var botNexusRoot = NormalizePath(fileSystem, @"C:\botnexus-home");
        var botNexusHome = new BotNexusHome(fileSystem, botNexusRoot);
        var workspaceManager = new FileAgentWorkspaceManager(botNexusHome, fileSystem);
        var workspacePath = workspaceManager.GetWorkspacePath("agent-a");
        fileSystem.Directory.CreateDirectory(workspacePath);
        fileSystem.AddFile(NormalizePath(fileSystem, Path.Combine(workspacePath, "notes.md")), new MockFileData("hello"));

        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(new AgentDescriptor
        {
            AgentId = AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "gpt-4.1",
            ApiProvider = "openai"
        });

        var controller = new WorkspaceController(registry, workspaceManager, fileSystem);
        return (controller, fileSystem, workspacePath);
    }

    private static string NormalizePath(MockFileSystem fileSystem, string path)
        => fileSystem.Path.GetFullPath(path);
}
