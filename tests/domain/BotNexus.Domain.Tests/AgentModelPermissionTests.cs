using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Domain.Tests;

public sealed class AgentModelPermissionTests
{
    [Theory]
    [InlineData("gpt-5", true)]
    [InlineData("GPT-5", true)]
    [InlineData("gpt-4o", false)]
    public void IsPermitted_WithConfiguredAllowList_UsesCaseInsensitiveMembership(string modelId, bool expected)
    {
        AgentModelPermission.IsPermitted(CreateDescriptor(["gpt-5"]), modelId).ShouldBe(expected);
    }

    [Fact]
    public void IsPermitted_WithEmptyAllowList_IsUnrestricted()
    {
        AgentModelPermission.IsPermitted(CreateDescriptor([]), "any-registered-model").ShouldBeTrue();
    }

    private static AgentDescriptor CreateDescriptor(IReadOnlyList<string> allowedModelIds) => new()
    {
        AgentId = AgentId.From("permission-test"),
        DisplayName = "Permission Test",
        ModelId = "gpt-5",
        ApiProvider = "test",
        AllowedModelIds = allowedModelIds
    };
}
