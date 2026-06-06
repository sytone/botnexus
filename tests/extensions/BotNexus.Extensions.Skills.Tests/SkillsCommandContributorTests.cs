using System.Reflection;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Extensions.Skills.Tests;

public sealed class SkillsCommandContributorTests
{
    [Fact]
    public void GetCommands_ReturnsSkillsCommand()
    {
        var contributor = CreateContributor();

        var command = contributor.GetCommands().Single(value => value.Name == "/skills");

        command.SubCommands!.Select(value => value.Name).ShouldBe(new[] { "list", "info", "add", "remove", "reload", "create", "delete" });
    }

    [Fact]
    public async Task List_ReturnsGroupedSkills()
    {
        var (contributor, skillTool) = CreateContributorWithSkillTool();

        var result = await ExecuteAsync(contributor, skillTool, "list");

        result.Body.ShouldMatch("(?is).*loaded.*available.*denied.*");
    }

    [Fact]
    public async Task Info_KnownSkill_ReturnsMetadata()
    {
        var (contributor, skillTool) = CreateContributorWithSkillTool();

        var result = await ExecuteAsync(contributor, skillTool, "info", "available-skill");

        result.Body.ShouldContain("available-skill");
    }

    [Fact]
    public async Task Info_UnknownSkill_ReturnsError()
    {
        var (contributor, skillTool) = CreateContributorWithSkillTool();

        var result = await ExecuteAsync(contributor, skillTool, "info", "missing-skill");

        result.IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task Add_ValidSkill_LoadsAndReturnsConfirmation()
    {
        var (contributor, skillTool) = CreateContributorWithSkillTool();

        var result = await ExecuteAsync(contributor, skillTool, "add", "available-skill");

        (result.IsError is false && result.Body.Contains("available-skill", StringComparison.OrdinalIgnoreCase)).ShouldBeTrue();
    }

    [Fact]
    public async Task Add_AlreadyLoaded_ReturnsError()
    {
        var (contributor, skillTool) = CreateContributorWithSkillTool();
        await skillTool.ExecuteAsync("call-0", Args("load", "available-skill"));

        var result = await ExecuteAsync(contributor, skillTool, "add", "available-skill");

        result.IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task Add_DeniedSkill_ReturnsError()
    {
        var (contributor, skillTool) = CreateContributorWithSkillTool();

        var result = await ExecuteAsync(contributor, skillTool, "add", "denied-skill");

        result.IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task Remove_LoadedSkill_UnloadsAndReturnsConfirmation()
    {
        var (contributor, skillTool) = CreateContributorWithSkillTool();
        await skillTool.ExecuteAsync("call-0", Args("load", "available-skill"));

        var result = await ExecuteAsync(contributor, skillTool, "remove", "available-skill");

        (result.IsError is false && !skillTool.SessionLoadedSkills.Contains("available-skill")).ShouldBeTrue();
    }

    [Fact]
    public async Task Remove_NotLoaded_ReturnsError()
    {
        var (contributor, skillTool) = CreateContributorWithSkillTool();

        var result = await ExecuteAsync(contributor, skillTool, "remove", "available-skill");

        result.IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task Reload_ReturnsDiscoverySummary()
    {
        var (contributor, skillTool) = CreateContributorWithSkillTool();

        var result = await ExecuteAsync(contributor, skillTool, "reload");

        result.Body.ShouldMatch("(?is).*reload.*");
    }

    [Fact]
    public async Task Execute_NoSubCommand_DefaultsToList()
    {
        var (contributor, skillTool) = CreateContributorWithSkillTool();

        var result = await ExecuteAsync(contributor, skillTool, subCommand: null);

        result.Body.ShouldMatch("(?is).*available.*");
    }

    private static (ICommandContributor Contributor, SkillTool SkillTool) CreateContributorWithSkillTool()
    {
        var skills = new[]
        {
            MakeSkill("loaded-skill"),
            MakeSkill("available-skill"),
            MakeSkill("denied-skill")
        };
        var config = new SkillsConfig
        {
            AutoLoad = ["loaded-skill"],
            Disabled = ["denied-skill"],
            MaxLoadedSkills = 20,
            MaxSkillContentChars = 100_000
        };
        var skillTool = new SkillTool(skills, config);

        return (CreateContributor(), skillTool);
    }

    private static ICommandContributor CreateContributor()
    {
        var contributorType = ResolveType("BotNexus.Extensions.Skills.SkillsCommandContributor, BotNexus.Extensions.Skills");
        var instance = CreateInstance(contributorType);
        return instance as ICommandContributor
               ?? throw new InvalidOperationException("SkillsCommandContributor must implement ICommandContributor.");
    }

    private static Type ResolveType(string assemblyQualifiedName)
        => Type.GetType(assemblyQualifiedName)
           ?? throw new InvalidOperationException($"Type not found: {assemblyQualifiedName}");

    private static object CreateInstance(Type type)
    {
        foreach (var constructor in type
                     .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                     .OrderByDescending(value => value.GetParameters().Length))
        {
            if (!TryBuildArguments(constructor.GetParameters(), out var arguments))
                continue;

            try
            {
                return constructor.Invoke(arguments);
            }
            catch
            {
                // Try next constructor.
            }
        }

        throw new InvalidOperationException($"Unable to construct {type.FullName} for testing.");
    }

    private static bool TryBuildArguments(IReadOnlyList<ParameterInfo> parameters, out object?[] arguments)
    {
        arguments = new object?[parameters.Count];
        for (var index = 0; index < parameters.Count; index++)
        {
            if (!TryCreateDefault(parameters[index].ParameterType, out var value))
                return false;

            arguments[index] = value;
        }

        return true;
    }

    private static bool TryCreateDefault(Type parameterType, out object? value)
    {
        if (parameterType == typeof(string))
        {
            value = @"Q:\repos\botnexus";
            return true;
        }

        if (parameterType.IsGenericType && parameterType.GetGenericTypeDefinition() == typeof(ILogger<>))
        {
            var loggerType = typeof(NullLogger<>).MakeGenericType(parameterType.GetGenericArguments()[0]);
            value = loggerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
            return true;
        }

        if (parameterType.IsValueType)
        {
            value = Activator.CreateInstance(parameterType);
            return true;
        }

        if (parameterType.GetConstructor(Type.EmptyTypes) is not null)
        {
            value = Activator.CreateInstance(parameterType);
            return true;
        }

        value = null;
        return true;
    }

    private static async Task<CommandResult> ExecuteAsync(
        ICommandContributor contributor,
        SkillTool skillTool,
        string? subCommand,
        params string[] arguments)
    {
        var context = new CommandExecutionContext
        {
            RawInput = $"/skills {subCommand} {string.Join(" ", arguments)}".Trim(),
            SubCommand = subCommand,
            Arguments = arguments,
            AgentId = "test-agent",
            SessionId = "session-1",
            HomeDirectory = @"Q:\repos\botnexus",
            ResolveSessionTool = name => string.Equals(name, "skills", StringComparison.OrdinalIgnoreCase) ? skillTool : null
        };

        return await contributor.ExecuteAsync("/skills", context, CancellationToken.None);
    }

    private static SkillDefinition MakeSkill(string name)
        => new()
        {
            Name = name,
            Description = $"{name} description",
            Content = $"content for {name}",
            Source = SkillSource.Global,
            SourcePath = $"/skills/{name}"
        };

    private static IReadOnlyDictionary<string, object?> Args(string action, string? skillName = null)
    {
        var dict = new Dictionary<string, object?> { ["action"] = action };
        if (skillName is not null)
            dict["skillName"] = skillName;
        return dict;
    }

    // ── create / delete tests ─────────────────────────────────────────────

    [Fact]
    public async Task Create_WhenManagerToolNotAvailable_ReturnsDisabledError()
    {
        var contributor = CreateContributor();
        // No skill_manage tool registered
        var context = new CommandExecutionContext
        {
            RawInput = "/skills create my-skill",
            SubCommand = "create",
            Arguments = ["my-skill"],
            AgentId = "test-agent",
            SessionId = "session-1",
            HomeDirectory = @"Q:\repos\botnexus",
            ResolveSessionTool = _ => null
        };

        var result = await contributor.ExecuteAsync("/skills", context, CancellationToken.None);

        result.IsError.ShouldBeTrue();
        result.Body.ShouldContain("AllowSkillCreation");
    }

    [Fact]
    public async Task Create_MissingName_ReturnsError()
    {
        var (contributor, _, managerTool) = CreateContributorWithBothTools();
        var context = BuildWriteContext("create", [], managerTool);

        var result = await contributor.ExecuteAsync("/skills", context, CancellationToken.None);

        result.IsError.ShouldBeTrue();
        result.Body.ShouldContain("name");
    }

    [Fact]
    public async Task Create_WithName_ReturnsInstructionMessage()
    {
        var (contributor, _, managerTool) = CreateContributorWithBothTools();
        var context = BuildWriteContext("create", ["my-skill"], managerTool);

        var result = await contributor.ExecuteAsync("/skills", context, CancellationToken.None);

        result.IsError.ShouldBeFalse();
        result.Body.ShouldContain("my-skill");
        result.Body.ShouldContain("skill_manage");
    }

    [Fact]
    public async Task Delete_WhenManagerToolNotAvailable_ReturnsDisabledError()
    {
        var contributor = CreateContributor();
        var context = new CommandExecutionContext
        {
            RawInput = "/skills delete my-skill --confirm",
            SubCommand = "delete",
            Arguments = ["my-skill", "--confirm"],
            AgentId = "test-agent",
            SessionId = "session-1",
            HomeDirectory = @"Q:\repos\botnexus",
            ResolveSessionTool = _ => null
        };

        var result = await contributor.ExecuteAsync("/skills", context, CancellationToken.None);

        result.IsError.ShouldBeTrue();
        result.Body.ShouldContain("AllowSkillDeletion");
    }

    [Fact]
    public async Task Delete_MissingName_ReturnsError()
    {
        var (contributor, _, managerTool) = CreateContributorWithBothTools();
        var context = BuildWriteContext("delete", [], managerTool);

        var result = await contributor.ExecuteAsync("/skills", context, CancellationToken.None);

        result.IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task Delete_WithoutConfirm_ReturnsConfirmationPrompt()
    {
        var (contributor, _, managerTool) = CreateContributorWithBothTools();
        var context = BuildWriteContext("delete", ["my-skill"], managerTool);

        var result = await contributor.ExecuteAsync("/skills", context, CancellationToken.None);

        result.IsError.ShouldBeFalse();
        result.Body.ShouldContain("--confirm");
    }

    [Fact]
    public async Task Delete_WithConfirm_AndSkillExists_ExecutesDelete()
    {
        // Arrange: create a real skill directory in a mock filesystem
        var fs = new System.IO.Abstractions.TestingHelpers.MockFileSystem();
        const string agentSkillsDir = @"C:\agent-skills";
        const string skillName = "test-skill";
        var skillDir = Path.Combine(agentSkillsDir, skillName);
        fs.AddDirectory(skillDir);
        fs.AddFile(Path.Combine(skillDir, "SKILL.md"), new System.IO.Abstractions.TestingHelpers.MockFileData("""
            ---
            name: test-skill
            description: A test skill
            ---
            Body.
            """));

        var config = new SkillsConfig { AllowSkillCreation = true, AllowSkillDeletion = true };
        var managerTool = new SkillManagerTool(agentSkillsDir, null, config, fs);
        var contributor = CreateContributor();
        var context = BuildWriteContext("delete", [skillName, "--confirm"], managerTool);

        var result = await contributor.ExecuteAsync("/skills", context, CancellationToken.None);

        result.IsError.ShouldBeFalse();
        result.Title.ShouldContain(skillName);
        fs.Directory.Exists(skillDir).ShouldBeFalse();
    }

    private static (ICommandContributor Contributor, SkillTool SkillTool, SkillManagerTool ManagerTool) CreateContributorWithBothTools()
    {
        var skills = new[]
        {
            MakeSkill("loaded-skill"),
            MakeSkill("available-skill"),
            MakeSkill("denied-skill")
        };
        var config = new SkillsConfig
        {
            AutoLoad = ["loaded-skill"],
            Disabled = ["denied-skill"],
            MaxLoadedSkills = 20,
            MaxSkillContentChars = 100_000,
            AllowSkillCreation = true,
            AllowSkillDeletion = true
        };
        var skillTool = new SkillTool(skills, config);
        var managerTool = new SkillManagerTool(null, null, config);

        return (CreateContributor(), skillTool, managerTool);
    }

    private static CommandExecutionContext BuildWriteContext(
        string subCommand,
        string[] arguments,
        SkillManagerTool managerTool)
    {
        return new CommandExecutionContext
        {
            RawInput = $"/skills {subCommand} {string.Join(" ", arguments)}".Trim(),
            SubCommand = subCommand,
            Arguments = arguments,
            AgentId = "test-agent",
            SessionId = "session-1",
            HomeDirectory = @"Q:\repos\botnexus",
            ResolveSessionTool = name => string.Equals(name, "skill_manage", StringComparison.OrdinalIgnoreCase)
                ? managerTool
                : null
        };
    }
}
