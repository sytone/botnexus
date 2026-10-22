using System.Text.Json;
using System.IO.Abstractions.TestingHelpers;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Skills;
using BotNexus.Gateway.Abstractions.Models;
using Shouldly;

namespace BotNexus.Extensions.Skills.Tests;

public sealed class SkillsConfigResolverTests
{
    [Fact]
    public void AgentDefaultFalse_DisablesSharedManagementForInheritingAgent()
    {
        var resolved = SkillsConfigResolver.Resolve(Descriptor(defaultJson: """{"allowSharedSkillManagement":false}"""));

        resolved.ShouldNotBeNull();
        resolved.AllowSharedSkillManagement.ShouldBeFalse();
    }

    [Fact]
    public void NamedFalse_OverridesAgentDefaultTrue()
    {
        var resolved = SkillsConfigResolver.Resolve(Descriptor(
            namedJson: """{"allowSharedSkillManagement":false}""",
            defaultJson: """{"allowSharedSkillManagement":true}"""));

        resolved.ShouldNotBeNull();
        resolved.AllowSharedSkillManagement.ShouldBeFalse();
    }

    [Fact]
    public void NamedPartialConfig_InheritsAgentDefaultFalse()
    {
        var resolved = SkillsConfigResolver.Resolve(Descriptor(
            namedJson: """{"maxLoadedSkills":7}""",
            defaultJson: """{"allowSharedSkillManagement":false}"""));

        resolved.ShouldNotBeNull();
        resolved.MaxLoadedSkills.ShouldBe(7);
        resolved.AllowSharedSkillManagement.ShouldBeFalse();
    }

    [Fact]
    public void NamedTrue_OverridesAgentDefaultFalse()
    {
        var resolved = SkillsConfigResolver.Resolve(Descriptor(
            namedJson: """{"allowSharedSkillManagement":true}""",
            defaultJson: """{"allowSharedSkillManagement":false}"""));

        resolved.ShouldNotBeNull();
        resolved.AllowSharedSkillManagement.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Resolve_DuplicatePermission_MatchesTypedBinding(bool namedScope, bool caseVariant, bool finalValue)
    {
        var json = DuplicatePermission(caseVariant, finalValue);
        var descriptor = namedScope
            ? Descriptor(namedJson: json)
            : Descriptor(defaultJson: json);
        var bound = namedScope
            ? ExtensionConfigBinder.BindNamedAgent<SkillsConfig>(descriptor, SkillsExtensionJson.ExtensionId)
            : ExtensionConfigBinder.BindAgentDefaults<SkillsConfig>(descriptor, SkillsExtensionJson.ExtensionId);

        bound.ShouldNotBeNull();
        bound.AllowSharedSkillManagement.ShouldBe(finalValue);
        var resolved = SkillsConfigResolver.Resolve(descriptor);
        resolved.ShouldNotBeNull();
        resolved.AllowSharedSkillManagement.ShouldBe(bound.AllowSharedSkillManagement);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Resolve_DuplicatePermission_PreservesScopePrecedence(bool namedScope, bool caseVariant, bool finalValue)
    {
        var duplicateJson = DuplicatePermission(caseVariant, finalValue);
        var oppositeJson = JsonSerializer.Serialize(new { allowSharedSkillManagement = !finalValue });
        var descriptor = namedScope
            ? Descriptor(namedJson: duplicateJson, defaultJson: oppositeJson)
            : Descriptor(namedJson: """{"maxLoadedSkills":7}""", defaultJson: duplicateJson);

        var resolved = SkillsConfigResolver.Resolve(descriptor);
        resolved.ShouldNotBeNull();
        resolved.AllowSharedSkillManagement.ShouldBe(finalValue);
        if (!namedScope)
            resolved.MaxLoadedSkills.ShouldBe(7);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Resolve_DuplicatePermissionEndingFalse_BlocksSharedWrite(bool namedScope, bool caseVariant)
    {
        var json = DuplicatePermission(caseVariant, finalValue: false);
        var descriptor = namedScope
            ? Descriptor(namedJson: json, defaultJson: """{"allowSharedSkillManagement":true}""")
            : Descriptor(namedJson: """{"maxLoadedSkills":7}""", defaultJson: json);
        var resolved = SkillsConfigResolver.Resolve(descriptor);
        resolved.ShouldNotBeNull();
        var fs = new MockFileSystem();
        var tool = new SkillManagerTool("/agent/skills", "/workspace/skills", "/shared/skills", resolved, fs);

        var result = await tool.ExecuteAsync("duplicate-permission", new Dictionary<string, object?>
        {
            ["action"] = "create",
            ["name"] = "team-skill",
            ["scope"] = "shared",
            ["content"] = "---\nname: team-skill\ndescription: A test skill\n---\n# Team skill\nBody."
        });

        string.Join("", result.Content.Select(c => c.Value)).ShouldContain("AllowSharedSkillManagement");
        fs.Directory.Exists("/shared/skills/team-skill").ShouldBeFalse();
    }

    private static string DuplicatePermission(bool caseVariant, bool finalValue)
    {
        var finalName = caseVariant ? "AllowSharedSkillManagement" : "allowSharedSkillManagement";
        return $$"""{"allowSharedSkillManagement":{{JsonSerializer.Serialize(!finalValue)}},"{{finalName}}":{{JsonSerializer.Serialize(finalValue)}}} """;
    }

    private static AgentDescriptor Descriptor(string? namedJson = null, string? defaultJson = null)
        => new()
        {
            AgentId = AgentId.From("test-agent"),
            DisplayName = "Test Agent",
            ModelId = "model",
            ApiProvider = "provider",
            ExtensionConfig = Bag(namedJson),
            DefaultExtensionConfig = Bag(defaultJson)
        };

    private static IReadOnlyDictionary<string, JsonElement> Bag(string? json)
    {
        if (json is null)
            return new Dictionary<string, JsonElement>();

        using var document = JsonDocument.Parse(json);
        return new Dictionary<string, JsonElement>
        {
            [SkillsExtensionJson.ExtensionId] = document.RootElement.Clone()
        };
    }
}
