using System.Text.Json;
using BotNexus.Gateway.Abstractions.Models;
using Shouldly;

namespace BotNexus.Extensions.WebTools.Tests;

/// <summary>
/// Proves the #3492 binding fix generally rather than only for <c>SkillsConfig</c>.
/// </summary>
/// <remarks>
/// The defect was never specific to Skills - it affected every extension that bound its config from
/// the raw element bag. Skills was merely the one where a default-<see langword="false"/> flag made
/// the silence audible. Covering a second, unrelated config type is what distinguishes "we fixed
/// the reported symptom" from "we fixed the class".
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
    /// camelCase keys bind to the PascalCase members of a non-Skills configuration type, including
    /// through a NESTED object.
    /// </summary>
    /// <remarks>
    /// Nesting matters: case-insensitivity has to apply at every level, and a fix that only
    /// normalised the outermost object would pass a flat test and still lose nested settings.
    /// Every asserted value differs from its C# default (<c>provider</c> defaults to "brave",
    /// <c>maxResults</c> to 5, <c>allowPrivateNetworks</c> to false).
    /// </remarks>
    [Fact]
    public void WebToolsConfig_BindsCamelCaseJson()
    {
        var descriptor = DescriptorWith(
            "botnexus-web",
            """{"search":{"provider":"google","maxResults":9},"fetch":{"allowPrivateNetworks":true,"timeoutSeconds":77}}""");

        var config = ExtensionConfigBinder.Bind<WebToolsConfig>(descriptor, "botnexus-web");

        config.ShouldNotBeNull();
        config.Search.ShouldNotBeNull();
        config.Search.Provider.ShouldBe("google");
        config.Search.MaxResults.ShouldBe(9);
        config.Fetch.ShouldNotBeNull();
        config.Fetch.AllowPrivateNetworks.ShouldBeTrue();
        config.Fetch.TimeoutSeconds.ShouldBe(77);
    }
}
