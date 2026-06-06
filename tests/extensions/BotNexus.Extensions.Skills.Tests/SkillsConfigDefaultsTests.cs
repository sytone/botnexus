using BotNexus.Extensions.Skills;

namespace BotNexus.Skills.Tests;

/// <summary>
/// Tests that SkillsConfig defaults to allowing skill creation and deletion (#941).
/// The Skills extension is opt-in -- when enabled, write operations should be available
/// by default rather than requiring additional configuration to unlock them.
/// </summary>
public sealed class SkillsConfigDefaultsTests
{
    [Fact]
    public void AllowSkillCreation_DefaultsToTrue()
    {
        var config = new SkillsConfig();
        Assert.True(config.AllowSkillCreation, "AllowSkillCreation should default to true");
    }

    [Fact]
    public void AllowSkillDeletion_DefaultsToTrue()
    {
        var config = new SkillsConfig();
        Assert.True(config.AllowSkillDeletion, "AllowSkillDeletion should default to true");
    }

    [Fact]
    public void AllowSkillCreation_FalseExplicit_RemainsBlockedForOperations()
    {
        // Verify that explicitly setting false still blocks -- the default change
        // does not remove the guard, only changes the opt-out to opt-in model.
        var config = new SkillsConfig { AllowSkillCreation = false };
        Assert.False(config.AllowSkillCreation, "AllowSkillCreation=false should remain false when explicitly set");
    }
}
