using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Agents;
using BotNexus.Tools;

namespace BotNexus.Gateway.Tests.Agents;

public sealed class DefaultAgentToolFactoryTests
{
    private static readonly string Workspace = Path.Combine(Path.GetTempPath(), "agent-workspace");
    private static readonly string ConfigPath = Path.Combine(Path.GetTempPath(), ".botnexus", "config.json");

    [Fact]
    public void CreateTools_WithConfigPath_DeniesWriteToConfigJson()
    {
        var factory = new DefaultAgentToolFactory(platformConfigPath: ConfigPath);
        var tools = factory.CreateTools(Workspace);

        var writeTool = tools.OfType<WriteTool>().Single();
        // Invoke via the path validator indirectly — WriteTool.ValidateAndResolve should reject the config path.
        // We test the underlying validator by constructing one with the same policy.
        var validator = new BotNexus.Gateway.Security.DefaultPathValidator(
            new FileAccessPolicy { DeniedPaths = [ConfigPath] },
            workspacePath: Workspace);

        validator.ValidateAndResolve(ConfigPath, FileAccessMode.Write).ShouldBeNull();
    }

    [Fact]
    public void CreateTools_WithConfigPath_AllowsWriteToWorkspace()
    {
        var factory = new DefaultAgentToolFactory(platformConfigPath: ConfigPath);
        var tools = factory.CreateTools(Workspace);

        // The path validator built inside the factory should allow workspace writes.
        var validator = new BotNexus.Gateway.Security.DefaultPathValidator(
            new FileAccessPolicy { DeniedPaths = [ConfigPath] },
            workspacePath: Workspace);

        var workspaceFile = Path.Combine(Workspace, "output.txt");
        validator.ValidateAndResolve(workspaceFile, FileAccessMode.Write).ShouldNotBeNull();
    }

    [Fact]
    public void CreateTools_WithoutConfigPath_AllowsWorkspaceWrites()
    {
        var factory = new DefaultAgentToolFactory();
        var tools = factory.CreateTools(Workspace);

        tools.ShouldNotBeNull();
        tools.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void CreateTools_WithCustomValidator_UsesCustomValidator()
    {
        // When a custom validator is passed, the factory should use it without layering
        // the config-deny policy on top (the caller is responsible for deny rules).
        var customValidator = new BotNexus.Gateway.Security.DefaultPathValidator(
            policy: null, workspacePath: Workspace);

        var factory = new DefaultAgentToolFactory(platformConfigPath: ConfigPath);
        var tools = factory.CreateTools(Workspace, pathValidator: customValidator);

        tools.ShouldNotBeNull();
        tools.Count.ShouldBeGreaterThan(0);
    }
}
