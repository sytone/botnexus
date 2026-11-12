using BotNexus.Extensions.Skills.Security;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Hooks;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.IO.Abstractions;
using System.Text.Json;

namespace BotNexus.Extensions.Skills.Tests;

public sealed class SkillReadBoundaryTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "botnexus-boundary-" + Guid.NewGuid().ToString("N"));
    private string Global => Path.Combine(_temp, "skills");
    private string Skill => Path.Combine(Global, "sample");

    public SkillReadBoundaryTests() => WriteSkill(Skill);

    private static void WriteSkill(string path)
    {
        Directory.CreateDirectory(Path.Combine(path, "references"));
        File.WriteAllText(Path.Combine(path, "SKILL.md"), "---\nname: sample\ndescription: Safe sample\n---\nSAFE BODY");
        File.WriteAllText(Path.Combine(path, "references", "safe.md"), "SAFE REFERENCE");
    }

    private string Outside()
    {
        var outside = Path.Combine(_temp, "agents", "other-agent", "workspace", "skills", "sample");
        WriteSkill(outside);
        File.WriteAllText(Path.Combine(outside, "references", "secret.md"), "OUTSIDE SECRET");
        return outside;
    }

    private void ReplaceRoot(string target)
    {
        Directory.Delete(Skill, true);
        Directory.CreateSymbolicLink(Skill, target);
    }

    private IReadOnlyList<SkillDefinition> Discover(ILogger? logger = null)
        => SkillDiscovery.Discover(Global, null, null, logger: logger);

    private SkillTool Tool() => new(Global, null, null, null);

    private static async Task<string> Execute(SkillTool tool, string action, string? file = null)
    {
        var args = new Dictionary<string, object?> { ["action"] = action, ["skillName"] = "sample" };
        if (file is not null) args["filePath"] = file;
        var result = await tool.ExecuteAsync("boundary", args);
        return string.Join("", result.Content.Select(c => c.Value));
    }

    [Fact]
    public void Discover_WholeRootLink_IsRejectedWithActionableDiagnostic()
    {
        ReplaceRoot(Outside());
        var logger = new CaptureLogger();
        Discover(logger).ShouldBeEmpty();
        var diagnostic = string.Join("\n", logger.Messages);
        diagnostic.ShouldContain(Skill);
        diagnostic.ShouldContain("link");
        diagnostic.ShouldContain("ordinary directory");
    }

    [Fact]
    public async Task Tool_CrossAgentRoot_IsNeitherAdvertisedNorLoadedOrViewed()
    {
        ReplaceRoot(Outside());
        var tool = Tool();
        (await Execute(tool, "list")).ShouldNotContain("sample");
        (await Execute(tool, "load")).ShouldContain("not found");
        (await Execute(tool, "view_file", "references/secret.md")).ShouldContain("not found");
    }

    [Fact]
    public async Task Tool_LinkedRoot_OptInListsLoadsAndViewsRepositoryFiles()
    {
        var repository = Outside();
        ReplaceRoot(repository);
        var tool = new SkillTool(Global, null, null, new() { AllowSymlinkedSkills = true });

        var skill = tool.GetDiscoveredSkills().ShouldHaveSingleItem();
        skill.SourcePath.ShouldBe(repository);
        skill.LinkedFiles.ShouldContain(file => file.RelativePath == "references/safe.md");
        (await Execute(tool, "list")).ShouldContain("sample");
        (await Execute(tool, "load")).ShouldContain("SAFE BODY");
        (await Execute(tool, "view_file", "references/safe.md")).ShouldContain("SAFE REFERENCE");

        File.WriteAllText(Path.Combine(repository, "references", "safe.md"), "UPDATED IN REPOSITORY");
        (await Execute(tool, "view_file", "references/safe.md")).ShouldContain("UPDATED IN REPOSITORY");

        var disabled = new SkillTool(Global, null, null, new() { AllowSymlinkedSkills = false });
        (await Execute(disabled, "list")).ShouldNotContain("sample");
        (await Execute(disabled, "load")).ShouldContain("not found");
    }

    [Fact]
    public async Task Tool_LinkedAncestor_OptInUsesCanonicalBoundary()
    {
        var alias = Path.Combine(_temp, "linked-skills");
        Directory.CreateSymbolicLink(alias, Global);
        var tool = new SkillTool(alias, null, null, new() { AllowSymlinkedSkills = true });

        tool.GetDiscoveredSkills().ShouldHaveSingleItem().SourcePath.ShouldBe(Skill);
        (await Execute(tool, "load")).ShouldContain("SAFE BODY");
        (await Execute(tool, "view_file", "references/safe.md")).ShouldContain("SAFE REFERENCE");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prompt_LinkedWorkspaceSkill_UsesConfiguredPermission(bool allow)
    {
        ReplaceRoot(Outside());
        var workspace = Substitute.For<IAgentWorkspaceManager>();
        workspace.GetWorkspacePath(Arg.Any<string>()).Returns(_temp);
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("symlink-test"),
            DisplayName = "Symlink test",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            DefaultExtensionConfig = new Dictionary<string, JsonElement>
            {
                [SkillsExtensionJson.ExtensionId] = JsonSerializer.SerializeToElement(
                    new { allowSymlinkedSkills = allow, autoLoad = new[] { "sample" } }, SkillsExtensionJson.Options)
            }
        };
        var hookEvent = new BeforePromptBuildEvent(
            descriptor.AgentId, descriptor, "prompt", [],
            DiagnosticExecutionOrigin.ForPromptConstruction(descriptor.AgentId, SessionId.From("symlink-session"), null, null));

        var result = await new SkillPromptHookHandler(workspace).HandleAsync(hookEvent);
        var prompt = result?.AppendSystemContext ?? string.Empty;

        if (allow)
            prompt.ShouldContain("SAFE BODY");
        else
            prompt.ShouldNotContain("SAFE BODY");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Discover_OptInStillRejectsEscapingChildren(bool linkedRoot)
    {
        var root = linkedRoot ? Outside() : Skill;
        if (linkedRoot)
            ReplaceRoot(root);
        var outside = Path.Combine(_temp, "unrelated");
        WriteSkill(outside);
        Directory.CreateSymbolicLink(Path.Combine(root, "references", "external"), outside);

        new SkillTool(Global, null, null, new() { AllowSymlinkedSkills = true })
            .GetDiscoveredSkills().ShouldBeEmpty();
    }

    [Fact]
    public void Discover_LinkedRoot_OptInStillScansRepositoryScripts()
    {
        var repository = Outside();
        ReplaceRoot(repository);
        Directory.CreateDirectory(Path.Combine(repository, "scripts"));
        File.WriteAllText(Path.Combine(repository, "scripts", "unsafe.js"), "eval('unsafe');");

        new SkillTool(Global, null, null, new() { AllowSymlinkedSkills = true })
            .GetDiscoveredSkills().ShouldBeEmpty();
    }

    [Fact]
    public void Discover_OptInStillRejectsLinkedMarkdownOutsideSkill()
    {
        File.Delete(Path.Combine(Skill, "SKILL.md"));
        File.CreateSymbolicLink(Path.Combine(Skill, "SKILL.md"), Path.Combine(Outside(), "SKILL.md"));

        new SkillTool(Global, null, null, new() { AllowSymlinkedSkills = true })
            .GetDiscoveredSkills().ShouldBeEmpty();
    }

    [Fact]
    public void Discover_OptInStillRejectsDirectoryCycles()
    {
        var repository = Outside();
        ReplaceRoot(repository);
        Directory.CreateSymbolicLink(Path.Combine(repository, "references", "cycle"), repository);

        new SkillTool(Global, null, null, new() { AllowSymlinkedSkills = true })
            .GetDiscoveredSkills().ShouldBeEmpty();
    }

    [Fact]
    public async Task Tool_OptInCanonicalRootReplacedAfterDiscovery_IsRejected()
    {
        var repository = Outside();
        ReplaceRoot(repository);
        var config = new SkillsConfig { AllowSymlinkedSkills = true };
        var skill = new SkillTool(Global, null, null, config).GetDiscoveredSkills().ShouldHaveSingleItem();
        var cached = new SkillTool([skill], config, new FileSystem());
        var replacement = Path.Combine(_temp, "replacement");
        WriteSkill(replacement);
        File.WriteAllText(Path.Combine(replacement, "references", "safe.md"), "SWAPPED SECRET");
        Directory.Delete(repository, true);
        Directory.CreateSymbolicLink(repository, replacement);

        (await Execute(cached, "view_file", "references/safe.md")).ShouldNotContain("SWAPPED SECRET");
    }

    [Fact]
    public async Task Manager_LinkedRoot_OptInPatchesRepositoryWithoutReplacingLink()
    {
        var repository = Outside();
        ReplaceRoot(repository);
        var manager = new SkillManagerTool(null, Global, new() { AllowSymlinkedSkills = true });

        var result = await manager.ExecuteAsync("linked-management", new Dictionary<string, object?>
        {
            ["action"] = "patch", ["name"] = "sample", ["oldText"] = "SAFE BODY", ["newText"] = "REVIEW IN PR"
        });

        string.Join("", result.Content.Select(c => c.Value)).ShouldNotContain("Error:");
        File.ReadAllText(Path.Combine(repository, "SKILL.md")).ShouldContain("REVIEW IN PR");
        new DirectoryInfo(Skill).LinkTarget.ShouldNotBeNull();
    }

    [Fact]
    public async Task Manager_LinkedRoot_DeleteRemovesLinkNotRepository()
    {
        var repository = Outside();
        ReplaceRoot(repository);
        var manager = new SkillManagerTool(null, Global, new() { AllowSymlinkedSkills = true });

        var result = await manager.ExecuteAsync("linked-delete", new Dictionary<string, object?>
        {
            ["action"] = "delete", ["name"] = "sample"
        });

        string.Join("", result.Content.Select(c => c.Value)).ShouldNotContain("Error:");
        Directory.Exists(Skill).ShouldBeFalse();
        File.ReadAllText(Path.Combine(repository, "SKILL.md")).ShouldContain("SAFE BODY");
    }

    [Fact]
    public void Discover_LinkedSkillMarkdown_IsRejected()
    {
        File.Delete(Path.Combine(Skill, "SKILL.md"));
        File.CreateSymbolicLink(Path.Combine(Skill, "SKILL.md"), Path.Combine(Outside(), "SKILL.md"));
        Discover().ShouldBeEmpty();
    }

    [Fact]
    public void Discover_EscapingChildDirectory_IsRejected()
    {
        Directory.CreateSymbolicLink(Path.Combine(Skill, "references", "external"), Outside());
        Discover().ShouldBeEmpty();
    }

    [Fact]
    public void Discover_EscapingChildFile_IsRejected()
    {
        File.CreateSymbolicLink(Path.Combine(Skill, "references", "secret.md"), Path.Combine(Outside(), "references", "secret.md"));
        Discover().ShouldBeEmpty();
    }

    [Fact]
    public async Task Tool_OrdinaryFiles_AreListedLoadedAndViewed()
    {
        Discover().ShouldHaveSingleItem().LinkedFiles.ShouldHaveSingleItem().RelativePath.ShouldBe("references/safe.md");
        var tool = Tool();
        (await Execute(tool, "load")).ShouldContain("SAFE BODY");
        (await Execute(tool, "view_file", "references/safe.md")).ShouldContain("SAFE REFERENCE");
    }

    [Fact]
    public async Task Tool_RootReplacedAfterDiscovery_IsRejected()
    {
        var skill = Discover().ShouldHaveSingleItem();
        var staticTool = new SkillTool([skill], null, new FileSystem());
        ReplaceRoot(Outside());
        (await Execute(staticTool, "view_file", "references/secret.md")).ShouldNotContain("OUTSIDE SECRET");
        (await Execute(Tool(), "load")).ShouldContain("not found");
    }

    [Fact]
    public async Task Tool_ChildReplacedAfterDiscovery_IsRejected()
    {
        var skill = Discover().ShouldHaveSingleItem();
        var staticTool = new SkillTool([skill], null, new FileSystem());
        Directory.Delete(Path.Combine(Skill, "references"), true);
        Directory.CreateSymbolicLink(Path.Combine(Skill, "references"), Path.Combine(Outside(), "references"));
        (await Execute(staticTool, "view_file", "references/secret.md")).ShouldNotContain("OUTSIDE SECRET");
        (await Execute(Tool(), "load")).ShouldContain("not found");
    }

    [Fact]
    public void Discover_InternalDirectoryCycle_IsRejectedWithoutRecursing()
    {
        Directory.CreateSymbolicLink(Path.Combine(Skill, "references", "cycle"), Skill);
        Discover().ShouldBeEmpty();
    }

    [Fact]
    public void Scanner_EscapingChild_IsRejectedBeforeReadingOutsideSource()
    {
        var outside = Outside();
        File.WriteAllText(Path.Combine(outside, "secret.js"), "eval('outside');");
        Directory.CreateSymbolicLink(Path.Combine(Skill, "scripts"), outside);
        var scan = SkillSecurityScanner.ScanDirectory(Skill);
        scan.ScannedFiles.ShouldBe(0);
        scan.Critical.ShouldBe(1);
        var finding = scan.Findings.ShouldHaveSingleItem();
        finding.RuleId.ShouldBe("invalid-installation");
        finding.Severity.ShouldBe(ScanSeverity.Critical);
        finding.Message.ShouldNotContain(outside);
        finding.Evidence.ShouldBeEmpty();
    }

    [Fact]
    public async Task Tool_ContainedChildLinks_AreDiscoveredLoadedViewedAndWritable()
    {
        Directory.CreateDirectory(Path.Combine(Skill, "bundled"));
        File.WriteAllText(Path.Combine(Skill, "bundled", "guide.md"), "CONTAINED GUIDE");
        Directory.CreateSymbolicLink(Path.Combine(Skill, "references", "linked"), Path.Combine(Skill, "bundled"));
        File.CreateSymbolicLink(Path.Combine(Skill, "references", "alias.md"), Path.Combine(Skill, "references", "safe.md"));
        var skill = Discover().ShouldHaveSingleItem();
        skill.LinkedFiles.ShouldContain(f => f.RelativePath == "references/linked/guide.md");
        skill.LinkedFiles.ShouldContain(f => f.RelativePath == "references/alias.md");
        var tool = Tool();
        (await Execute(tool, "load")).ShouldContain("references/linked/guide.md");
        (await Execute(tool, "view_file", "references/linked/guide.md")).ShouldContain("CONTAINED GUIDE");
        (await Execute(tool, "view_file", "references/alias.md")).ShouldContain("SAFE REFERENCE");
        var result = await Manage("patch", new() { ["filePath"] = "references/linked/guide.md", ["oldText"] = "CONTAINED", ["newText"] = "UPDATED" });
        result.ShouldNotContain("blocked");
        File.ReadAllText(Path.Combine(Skill, "bundled", "guide.md")).ShouldBe("UPDATED GUIDE");
    }

    [Fact]
    public void Discover_ChainedChildLinkEscapingRoot_IsRejected()
    {
        var outside = Outside();
        Directory.CreateSymbolicLink(Path.Combine(Skill, "hop"), outside);
        Directory.CreateSymbolicLink(Path.Combine(Skill, "references", "chain"), Path.Combine(Skill, "hop"));
        Discover().ShouldBeEmpty();
        SkillPathValidator.TryValidate(Path.Combine(Skill, "references", "chain", "references", "secret.md"),
            SkillPath.CreateRoot(Skill, new FileSystem()), new FileSystem(), out _, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Manager_PatchInvalidInstallation_RestoresOriginal()
    {
        Directory.CreateSymbolicLink(Path.Combine(Skill, "scripts"), Outside());
        var path = Path.Combine(Skill, "SKILL.md");
        var original = File.ReadAllText(path);
        var result = await Manage("patch", new() { ["oldText"] = "SAFE BODY", ["newText"] = "MUTATED BODY" });
        result.ShouldContain("blocked");
        result.ShouldContain("invalid-installation");
        File.ReadAllText(path).ShouldBe(original);
    }

    [Fact]
    public async Task Manager_WriteInvalidInstallation_RemovesNewFile()
    {
        Directory.CreateSymbolicLink(Path.Combine(Skill, "scripts"), Outside());
        var result = await Manage("write_file", new() { ["filePath"] = "references/new.md", ["content"] = "MUTATION" });
        result.ShouldContain("blocked");
        File.Exists(Path.Combine(Skill, "references", "new.md")).ShouldBeFalse();
    }

    [Fact]
    public async Task Manager_WriteInvalidInstallation_RestoresExistingFile()
    {
        Directory.CreateSymbolicLink(Path.Combine(Skill, "scripts"), Outside());
        var result = await Manage("write_file", new() { ["filePath"] = "references/safe.md", ["content"] = "MUTATION" });
        result.ShouldContain("blocked");
        File.ReadAllText(Path.Combine(Skill, "references", "safe.md")).ShouldBe("SAFE REFERENCE");
    }

    [Fact]
    public void PathValidator_LinkCycle_FailsWithoutThrowing()
    {
        File.CreateSymbolicLink(Path.Combine(Skill, "references", "loop-a.md"), "loop-b.md");
        File.CreateSymbolicLink(Path.Combine(Skill, "references", "loop-b.md"), "loop-a.md");
        SkillPathValidator.TryValidate(Path.Combine(Skill, "references", "loop-a.md"),
            SkillPath.CreateRoot(Skill, new FileSystem()), new FileSystem(), out _, out _).ShouldBeFalse();
        Discover().ShouldBeEmpty();
        SkillSecurityScanner.ScanDirectory(Skill).Critical.ShouldBe(1);
    }

    [Fact]
    public void PathValidator_BranchingLinkGraph_RejectsExcessiveResolutionWork()
    {
        Directory.CreateSymbolicLink(Path.Combine(Skill, "l0"), ".");
        for (var i = 1; i <= 8; i++)
            Directory.CreateSymbolicLink(Path.Combine(Skill, $"l{i}"), $"l{i - 1}/l{i - 1}");
        SkillPathValidator.TryValidate(Path.Combine(Skill, "l8"),
            SkillPath.CreateRoot(Skill, new FileSystem()), new FileSystem(), out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void Scanner_MissingInstallation_FailsClosed()
    {
        SkillSecurityScanner.ScanDirectory(Path.Combine(_temp, "missing")).Critical.ShouldBe(1);
    }

    [Fact]
    public void Discover_LinkedAncestor_IsRejected()
    {
        var alias = Path.Combine(_temp, "linked-skills");
        Directory.CreateSymbolicLink(alias, Global);
        SkillDiscovery.Discover(alias, null, null).ShouldBeEmpty();
        SkillSecurityScanner.ScanDirectory(Path.Combine(alias, "sample")).Critical.ShouldBe(1);
    }

    [Fact]
    public async Task Tool_ContainedRelativeChain_UsesCanonicalIdentity()
    {
        File.CreateSymbolicLink(Path.Combine(Skill, "references", "hop.md"), "safe.md");
        File.CreateSymbolicLink(Path.Combine(Skill, "references", "chain.md"), "hop.md");
        Discover().ShouldHaveSingleItem().LinkedFiles.ShouldContain(f => f.RelativePath == "references/chain.md");
        SkillPathValidator.TryValidate(Path.Combine(Skill, "references", "chain.md"),
            SkillPath.CreateRoot(Skill, new FileSystem()), new FileSystem(), out var resolved, out _).ShouldBeTrue();
        resolved.Value.ShouldBe(Path.Combine(Skill, "references", "safe.md"));
        (await Execute(Tool(), "view_file", "references/chain.md")).ShouldContain(resolved.Value);
        var result = await Manage("write_file", new() { ["filePath"] = "references/chain.md", ["content"] = "UPDATED THROUGH ALIAS" });
        result.ShouldNotContain("Error:");
        File.ReadAllText(resolved.Value).ShouldBe("UPDATED THROUGH ALIAS");
    }

    [Fact]
    public void Scanner_ContainedScriptAlias_IsScannedNotSkipped()
    {
        File.WriteAllText(Path.Combine(Skill, "payload.txt"), "eval('contained');");
        Directory.CreateDirectory(Path.Combine(Skill, "scripts"));
        File.CreateSymbolicLink(Path.Combine(Skill, "scripts", "alias.js"), "../payload.txt");
        var scan = SkillSecurityScanner.ScanDirectory(Skill);
        scan.Critical.ShouldBe(1);
        scan.Findings.ShouldHaveSingleItem().RuleId.ShouldBe("dynamic-code-execution");
        Discover().ShouldBeEmpty();
    }

    [Fact]
    public async Task Manager_EditInvalidInstallation_RestoresOriginal()
    {
        Directory.CreateSymbolicLink(Path.Combine(Skill, "scripts"), Outside());
        var path = Path.Combine(Skill, "SKILL.md");
        var original = File.ReadAllText(path);
        var result = await Manage("edit", new() { ["content"] = original.Replace("SAFE BODY", "MUTATED BODY", StringComparison.Ordinal) });
        result.ShouldContain("blocked");
        File.ReadAllText(path).ShouldBe(original);
    }

    private async Task<string> Manage(string action, Dictionary<string, object?> args)
    {
        var manager = new SkillManagerTool(null, Global, new() { AllowSkillCreation = true });
        args["action"] = action;
        args["name"] = "sample";
        var result = await manager.ExecuteAsync("boundary-management", args);
        return string.Join("", result.Content.Select(c => c.Value));
    }

    public void Dispose()
    {
        // Remove links themselves, never recursively traverse their targets during cleanup.
        RemoveDirectory(_temp);
    }

    private static void RemoveDirectory(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0) Directory.Delete(entry);
                else RemoveDirectory(entry);
            }
            else File.Delete(entry);
        }
        Directory.Delete(path);
    }

    private sealed class CaptureLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
