using BotNexus.Core.Models;
using FluentAssertions;
using Xunit;

namespace BotNexus.Tests.Unit.Tests;

/// <summary>
/// Tests for nullable Temperature and MaxTokens in GenerationSettings.
/// Verifies that null values don't crash and are preserved correctly.
/// </summary>
public class GenerationSettingsNullableTests
{
    [Fact]
    public void GenerationSettings_NullTemperature_DoesNotCrash()
    {
        var settings = new GenerationSettings
        {
            Model = "gpt-4o",
            Temperature = null
        };

        settings.Temperature.Should().BeNull();
        settings.Model.Should().Be("gpt-4o");
    }

    [Fact]
    public void GenerationSettings_NullMaxTokens_DoesNotCrash()
    {
        var settings = new GenerationSettings
        {
            Model = "gpt-4o",
            MaxTokens = null
        };

        settings.MaxTokens.Should().BeNull();
        settings.Model.Should().Be("gpt-4o");
    }

    [Fact]
    public void GenerationSettings_ExplicitZeroTemperature_IsPreserved()
    {
        var settings = new GenerationSettings
        {
            Temperature = 0.0
        };

        settings.Temperature.Should().Be(0.0);
    }

    [Fact]
    public void GenerationSettings_ExplicitZeroMaxTokens_IsPreserved()
    {
        var settings = new GenerationSettings
        {
            MaxTokens = 0
        };

        settings.MaxTokens.Should().Be(0);
    }

    [Fact]
    public void GenerationSettings_ExplicitValues_ArePreserved()
    {
        var settings = new GenerationSettings
        {
            Model = "gpt-4o",
            Temperature = 0.7,
            MaxTokens = 1000
        };

        settings.Temperature.Should().Be(0.7);
        settings.MaxTokens.Should().Be(1000);
    }

    [Fact]
    public void GenerationSettings_BothNull_DoesNotCrash()
    {
        var settings = new GenerationSettings
        {
            Model = "gpt-4o",
            Temperature = null,
            MaxTokens = null
        };

        settings.Temperature.Should().BeNull();
        settings.MaxTokens.Should().BeNull();
        settings.Model.Should().Be("gpt-4o");
    }

    [Fact]
    public void GenerationSettings_CanSetNullAfterValue()
    {
        var settings = new GenerationSettings
        {
            Temperature = 0.8
        };

        settings.Temperature = null;
        settings.Temperature.Should().BeNull();
    }

    [Fact]
    public void GenerationSettings_DefaultsAreNull()
    {
        var settings = new GenerationSettings();

        settings.Temperature.Should().BeNull();
        settings.MaxTokens.Should().BeNull();
        settings.Model.Should().BeNull(); // Model is now nullable
        settings.MaxToolIterations.Should().Be(40); // Default iterations
    }
}
