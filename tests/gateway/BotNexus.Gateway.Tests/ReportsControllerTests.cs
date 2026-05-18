using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Api.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Gateway.Tests;

public sealed class ReportsControllerTests
{
    [Fact]
    public void GetReports_WithMarkdownFiles_ReturnsMetadataForMarkdownOnly()
    {
        var (controller, fileSystem, workspacePath) = CreateController();
        var reportsPath = NormalizePath(fileSystem, Path.Combine(workspacePath, "reports"));
        fileSystem.Directory.CreateDirectory(reportsPath);
        fileSystem.AddFile(NormalizePath(fileSystem, Path.Combine(reportsPath, "daily.md")), new MockFileData("daily report"));
        fileSystem.AddFile(NormalizePath(fileSystem, Path.Combine(reportsPath, "notes.txt")), new MockFileData("ignore me"));
        fileSystem.AddFile(NormalizePath(fileSystem, Path.Combine(reportsPath, "weekly.MD")), new MockFileData("weekly report"));
        fileSystem.Directory.CreateDirectory(NormalizePath(fileSystem, Path.Combine(reportsPath, "folder.md")));

        var result = controller.GetReports("agent-a");

        var payload = (result.Result as OkObjectResult)?.Value.ShouldBeOfType<ReportsListResponse>();
        payload.ShouldNotBeNull();
        payload!.Reports.Select(report => report.Name).ShouldBe(["daily.md", "weekly.MD"]);
        payload.Reports.ShouldAllBe(report => report.Size > 0);
    }

    [Fact]
    public void GetReports_WhenReportsDirectoryMissing_ReturnsEmptyList()
    {
        var (controller, _, _) = CreateController();

        var result = controller.GetReports("agent-a");

        var payload = (result.Result as OkObjectResult)?.Value.ShouldBeOfType<ReportsListResponse>();
        payload.ShouldNotBeNull();
        payload!.Reports.ShouldBeEmpty();
    }

    [Fact]
    public void GetReport_WhenReportExists_ReturnsContent()
    {
        var (controller, fileSystem, workspacePath) = CreateController();
        var reportsPath = NormalizePath(fileSystem, Path.Combine(workspacePath, "reports"));
        fileSystem.Directory.CreateDirectory(reportsPath);
        fileSystem.AddFile(NormalizePath(fileSystem, Path.Combine(reportsPath, "daily.md")), new MockFileData("daily report"));

        var result = controller.GetReport("agent-a", "daily.md");

        var payload = (result.Result as OkObjectResult)?.Value.ShouldBeOfType<ReportContentResponse>();
        payload.ShouldNotBeNull();
        payload!.Name.ShouldBe("daily.md");
        payload.Content.ShouldBe("daily report");
        payload.Encoding.ShouldBe("utf-8");
    }

    [Fact]
    public void GetReport_WhenReportMissing_ReturnsNotFound()
    {
        var (controller, fileSystem, workspacePath) = CreateController();
        fileSystem.Directory.CreateDirectory(NormalizePath(fileSystem, Path.Combine(workspacePath, "reports")));

        var result = controller.GetReport("agent-a", "missing.md");

        result.Result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public void GetReport_WhenNameTargetsDirectory_ReturnsBadRequest()
    {
        var (controller, fileSystem, workspacePath) = CreateController();
        fileSystem.Directory.CreateDirectory(NormalizePath(fileSystem, Path.Combine(workspacePath, "reports", "monthly.md")));

        var result = controller.GetReport("agent-a", "monthly.md");

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("..\\outside.md")]
    [InlineData("reports/outside.md")]
    [InlineData("notes.txt")]
    public void GetReport_WithUnsafeName_ReturnsBadRequest(string name)
    {
        var (controller, _, _) = CreateController();

        var result = controller.GetReport("agent-a", name);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void GetReport_WithSymlinkEscape_ReturnsForbidden()
    {
        var (controller, fileSystem, workspacePath) = CreateController();
        var reportsPath = NormalizePath(fileSystem, Path.Combine(workspacePath, "reports"));
        fileSystem.Directory.CreateDirectory(reportsPath);

        var outsideTarget = NormalizePath(fileSystem, @"C:\outside\secret.md");
        fileSystem.AddFile(outsideTarget, new MockFileData("secret"));

        var linkPath = NormalizePath(fileSystem, Path.Combine(reportsPath, "escape.md"));
        fileSystem.Directory.CreateSymbolicLink(linkPath, outsideTarget);

        var result = controller.GetReport("agent-a", "escape.md");

        var payload = result.Result.ShouldBeOfType<StatusCodeResult>();
        payload.StatusCode.ShouldBe(403);
    }

    [Fact]
    public void GetReports_SkipsSymlinkEscapesOutsideReportsDirectory()
    {
        var (controller, fileSystem, workspacePath) = CreateController();
        var reportsPath = NormalizePath(fileSystem, Path.Combine(workspacePath, "reports"));
        fileSystem.Directory.CreateDirectory(reportsPath);
        fileSystem.AddFile(NormalizePath(fileSystem, Path.Combine(reportsPath, "kept.md")), new MockFileData("ok"));

        var outsideTarget = NormalizePath(fileSystem, Path.Combine(workspacePath, "private", "secret.md"));
        fileSystem.Directory.CreateDirectory(NormalizePath(fileSystem, Path.Combine(workspacePath, "private")));
        fileSystem.AddFile(outsideTarget, new MockFileData("secret"));

        var linkPath = NormalizePath(fileSystem, Path.Combine(reportsPath, "escape.md"));
        fileSystem.Directory.CreateSymbolicLink(linkPath, outsideTarget);

        var result = controller.GetReports("agent-a");

        var payload = (result.Result as OkObjectResult)?.Value.ShouldBeOfType<ReportsListResponse>();
        payload.ShouldNotBeNull();
        payload!.Reports.Select(report => report.Name).ShouldBe(["kept.md"]);
    }

    private static (ReportsController Controller, MockFileSystem FileSystem, string WorkspacePath) CreateController()
    {
        var fileSystem = new MockFileSystem();
        var botNexusRoot = NormalizePath(fileSystem, @"C:\botnexus-home");
        var botNexusHome = new BotNexusHome(fileSystem, botNexusRoot);
        var workspaceManager = new FileAgentWorkspaceManager(botNexusHome, fileSystem);
        var workspacePath = workspaceManager.GetWorkspacePath("agent-a");
        fileSystem.Directory.CreateDirectory(workspacePath);

        var registry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
        registry.Register(new AgentDescriptor
        {
            AgentId = AgentId.From("agent-a"),
            DisplayName = "Agent A",
            ModelId = "gpt-4.1",
            ApiProvider = "openai"
        });

        var controller = new ReportsController(registry, workspaceManager, fileSystem, Options.Create(new PlatformConfig()));
        return (controller, fileSystem, workspacePath);
    }

    private static string NormalizePath(MockFileSystem fileSystem, string path)
        => fileSystem.Path.GetFullPath(path);
}
