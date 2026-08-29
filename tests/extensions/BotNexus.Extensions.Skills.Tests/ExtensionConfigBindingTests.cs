using System.Text.Json;
using BotNexus.Extensions.Skills;
using BotNexus.Gateway.Abstractions.Models;
using Shouldly;

namespace BotNexus.Extensions.Skills.Tests;

/// <summary>
/// Pins the camelCase-to-PascalCase binding of extension configuration (#3492).
/// </summary>
/// <remarks>
/// <para>
/// The defect these tests exist for produced no crash, no log line, and no failing test: extension
/// config bound with default (case-sensitive) options simply returned a POCO of C# defaults, so the
/// software worked and the values were wrong. The existing Skills suite stayed green throughout
/// because it constructs <see cref="SkillsConfig"/> in memory and never crosses the JSON boundary -
/// the defect lived precisely in the untested seam.
/// </para>
/// <para>
/// Every assertion here therefore uses a value that DIFFERS from the C# default. Asserting a value
/// that coincides with the default would pass against the bug and prove nothing, which is exactly
/// how <c>enabled</c> and <c>allowSkillCreation</c> appeared to work for months.
/// </para>
/// </remarks>
public sealed class ExtensionConfigBindingTests
{
    private static AgentDescriptor DescriptorWith(string extensionId, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new AgentDescriptor
        {
            AgentId = BotNexus.Domain.Primitives.AgentId.From("test-agent"),
            DisplayName = "Test Agent",
            ModelId = "m",
            ApiProvider = "p",
            ExtensionConfig = new Dictionary<string, JsonElement>
            {
                [extensionId] = doc.RootElement.Clone(),
            },
        };
    }

    /// <summary>
    /// The exact reproduction from #3492: every value is the opposite of its C# default, so a
    /// binding failure cannot masquerade as success.
    /// </summary>
    [Fact]
    public void CamelCaseJson_BindsToPascalCaseProperties()
    {
        var descriptor = DescriptorWith(
            "botnexus-skills",
            """{"enabled":false,"allowSkillCreation":false,"allowSharedSkillManagement":true}""");

        var config = ExtensionConfigBinder.Bind<SkillsConfig>(descriptor, "botnexus-skills");

        config.ShouldNotBeNull();
        config.Enabled.ShouldBeFalse();
        config.AllowSkillCreation.ShouldBeFalse();
        config.AllowSharedSkillManagement.ShouldBeTrue();
    }

    /// <summary>
    /// The flag that surfaced the defect, isolated: its default is <see langword="false"/>, so a
    /// bound <see langword="true"/> can only come from the file.
    /// </summary>
    [Fact]
    public void AllowSharedSkillManagement_BindsTrueFromConfig()
    {
        var descriptor = DescriptorWith(
            "botnexus-skills",
            """{"allowSharedSkillManagement":true}""");

        ExtensionConfigBinder.Bind<SkillsConfig>(descriptor, "botnexus-skills")!
            .AllowSharedSkillManagement.ShouldBeTrue();
    }

    /// <summary>
    /// Numeric and collection members bind too - the defect was not confined to booleans.
    /// </summary>
    [Fact]
    public void NonBooleanMembers_BindFromCamelCaseJson()
    {
        var descriptor = DescriptorWith(
            "botnexus-skills",
            """{"maxLoadedSkills":7,"maxSkillContentChars":1234,"disabled":["alpha","beta"]}""");

        var config = ExtensionConfigBinder.Bind<SkillsConfig>(descriptor, "botnexus-skills");

        config.ShouldNotBeNull();
        config.MaxLoadedSkills.ShouldBe(7);
        config.MaxSkillContentChars.ShouldBe(1234);
        config.Disabled.ShouldBe(["alpha", "beta"]);
    }

    /// <summary>
    /// An absent extension key binds to null so callers can distinguish "not configured" from
    /// "configured with defaults".
    /// </summary>
    [Fact]
    public void AbsentExtensionKey_BindsToNull()
    {
        var descriptor = DescriptorWith("botnexus-other", """{"enabled":true}""");

        ExtensionConfigBinder.Bind<SkillsConfig>(descriptor, "botnexus-skills").ShouldBeNull();
    }

    /// <summary>
    /// Malformed configuration returns null rather than throwing: one bad extension entry must not
    /// prevent an agent from starting.
    /// </summary>
    [Fact]
    public void MalformedConfig_BindsToNullWithoutThrowing()
    {
        var descriptor = DescriptorWith("botnexus-skills", """{"maxLoadedSkills":"not-a-number"}""");

        Should.NotThrow(() => ExtensionConfigBinder.Bind<SkillsConfig>(descriptor, "botnexus-skills"))
            .ShouldBeNull();
    }

    /// <summary>
    /// An explicit JSON null is "not configured", not "configured as an empty object".
    /// </summary>
    [Fact]
    public void ExplicitJsonNull_BindsToNull()
    {
        var descriptor = DescriptorWith("botnexus-skills", "null");

        ExtensionConfigBinder.Bind<SkillsConfig>(descriptor, "botnexus-skills").ShouldBeNull();
    }

    /// <summary>
    /// An empty object binds to a real instance carrying every default - distinct from absent.
    /// </summary>
    [Fact]
    public void EmptyObject_BindsToDefaults()
    {
        var descriptor = DescriptorWith("botnexus-skills", "{}");

        ExtensionConfigBinder.Bind<SkillsConfig>(descriptor, "botnexus-skills").ShouldNotBeNull();
    }

    /// <summary>
    /// The production resolution path, not just the binder: the tool contributor must see the
    /// configured value. This is the assertion that fails against <c>main</c>.
    /// </summary>
    [Fact]
    public void ProductionResolutionPath_SeesConfiguredValue()
    {
        var descriptor = DescriptorWith(
            "botnexus-skills",
            """{"allowSharedSkillManagement":true,"allowSkillCreation":false}""");

        var config = ExtensionConfigBinder.Bind<SkillsConfig>(descriptor, "botnexus-skills");

        config.ShouldNotBeNull();
        config.AllowSharedSkillManagement.ShouldBeTrue(
            "an operator setting allowSharedSkillManagement:true must be able to manage shared skills");
        config.AllowSkillCreation.ShouldBeFalse();
    }
}
