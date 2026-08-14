using System.Text.Json;
using BotNexus.Cron.Tools;
using Moq;

namespace BotNexus.Cron.Tests;

/// <summary>
/// #3128: an agent authoring a time-boxed job reads ONLY the emitted tool schema, never the C# XML
/// docs. The <c>expiresAt</c> description described suppression-without-mutation correctly but never
/// named <c>deleteJobAfterRun</c>, so every time-boxed job leaked a permanently inert row. These
/// tests assert the schema TEXT itself, because the cross-reference is the deliverable - a future
/// edit that drops the edge must fail here rather than silently regress the guidance.
/// </summary>
public sealed class CronToolLifecycleSchemaTests
{
    private static string DescriptionOf(string property)
    {
        var tool = CronToolFailureAlertSurfaceTests.CreateTool(new Mock<ICronStore>().Object);
        var properties = tool.Definition.Parameters.GetProperty("properties");
        properties.TryGetProperty(property, out JsonElement element).ShouldBeTrue(
            $"the cron tool schema must declare '{property}'");
        return element.GetProperty("description").GetString()!;
    }

    /// <summary>AC1: expiry suppresses execution and does not delete or disable the job.</summary>
    [Fact]
    public void ExpiresAt_Description_StatesSuppressionWithoutDeleteOrDisable()
    {
        var description = DescriptionOf("expiresAt");

        description.ShouldContain("NOT deleted or disabled");
        description.ShouldContain("suppresses");
    }

    /// <summary>AC2/AC4: the expiry description names the flag that actually removes the job.</summary>
    [Fact]
    public void ExpiresAt_Description_NamesDeleteJobAfterRun()
        => DescriptionOf("expiresAt").ShouldContain("deleteJobAfterRun");

    /// <summary>
    /// AC3: suppression-without-mutation is deliberate (#2634). The human-extend guarantee must be
    /// retained alongside the new cross-reference, not replaced by it.
    /// </summary>
    [Fact]
    public void ExpiresAt_Description_RetainsHumanExtendGuarantee()
        => DescriptionOf("expiresAt").ShouldContain("visible for a human to extend");

    /// <summary>
    /// AC5: no lifecycle property description is a dead end - each names the siblings it is most
    /// likely to be confused with.
    /// </summary>
    [Theory]
    [InlineData("expiresAt", "deleteJobAfterRun")]
    [InlineData("deleteAfterRun", "deleteJobAfterRun")]
    [InlineData("deleteJobAfterRun", "deleteAfterRun")]
    [InlineData("deleteJobAfterRun", "expiresAt")]
    public void LifecycleDescriptions_CrossReferenceTheirSiblings(string property, string sibling)
        => DescriptionOf(property).ShouldContain(sibling);
}
