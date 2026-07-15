using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Smoke tests for the additive config-parity metadata introduced in #1609 (PBI 1/6 of #1579):
/// the <see cref="ConfigFieldAttribute"/> UI-hint attribute plus standard DataAnnotations
/// (<see cref="DisplayAttribute"/>, <see cref="DefaultValueAttribute"/>, validation attributes)
/// on the PlatformConfig tree.
///
/// These assert the annotations are REFLECTABLE (the contract downstream layers depend on) and
/// that adding them did not change config load/validation behaviour. They are intentionally
/// strict: removing an annotated attribute from the source makes the corresponding test fail.
/// </summary>
public sealed class ConfigFieldAnnotationTests
{
    private static PropertyInfo GetProp(Type type, string name)
        => type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
           ?? throw new InvalidOperationException($"Property '{name}' not found on {type.Name}.");

    // -- 1. [Display] is reflectable off a known annotated PlatformConfig field --

    [Fact]
    public void Display_OnDateTimeInjectionEnabled_ExposesNameAndDescription()
    {
        var prop = GetProp(typeof(DateTimeInjectionConfig), nameof(DateTimeInjectionConfig.Enabled));

        var display = prop.GetCustomAttribute<DisplayAttribute>();

        display.ShouldNotBeNull("Enabled should carry a [Display] annotation for config parity.");
        display!.GetName().ShouldNotBeNullOrWhiteSpace();
        display.GetDescription().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Display_OnPlatformVersion_ExposesNameAndDescription()
    {
        var prop = GetProp(typeof(PlatformConfig), nameof(PlatformConfig.PlatformVersion));

        var display = prop.GetCustomAttribute<DisplayAttribute>();

        display.ShouldNotBeNull("PlatformVersion should carry a [Display] annotation (common-case field).");
        display!.GetName().ShouldNotBeNullOrWhiteSpace();
        display.GetDescription().ShouldNotBeNullOrWhiteSpace();
    }

    // -- 2. [ConfigField] Widget is reflectable off the DateTimeInjectionConfig canary --

    [Fact]
    public void ConfigField_OnDateTimeInjectionEnabled_IsToggleWidget()
    {
        var prop = GetProp(typeof(DateTimeInjectionConfig), nameof(DateTimeInjectionConfig.Enabled));

        var field = prop.GetCustomAttribute<ConfigFieldAttribute>();

        field.ShouldNotBeNull("Enabled (the canary) should carry a [ConfigField] annotation.");
        field!.Widget.ShouldBe(ConfigFieldWidget.Toggle);
    }

    [Fact]
    public void ConfigField_OnDateTimeInjectionTimezone_IsSelectWidget()
    {
        var prop = GetProp(typeof(DateTimeInjectionConfig), nameof(DateTimeInjectionConfig.Timezone));

        var field = prop.GetCustomAttribute<ConfigFieldAttribute>();

        field.ShouldNotBeNull("Timezone (the canary) should carry a [ConfigField] annotation.");
        field!.Widget.ShouldBe(ConfigFieldWidget.Select);
    }

    [Fact]
    public void ConfigField_OnDateTimeInjectionFormat_IsSelectWidget()
    {
        var prop = GetProp(typeof(DateTimeInjectionConfig), nameof(DateTimeInjectionConfig.Format));

        var field = prop.GetCustomAttribute<ConfigFieldAttribute>();

        field.ShouldNotBeNull("Format (the canary) should carry a [ConfigField] annotation.");
        field!.Widget.ShouldBe(ConfigFieldWidget.Select);
    }

    // -- 3. The ConfigField attribute type itself exposes the 5 expected Widget members --

    [Fact]
    public void ConfigFieldWidget_HasExactlyTheFiveExpectedMembers()
    {
        var names = Enum.GetNames<ConfigFieldWidget>();

        names.ShouldBe(new[] { "Toggle", "Text", "Number", "Select", "Secret" }, ignoreOrder: true);
        names.Length.ShouldBe(5);
    }

    [Fact]
    public void ConfigFieldAttribute_ExposesGroupOrderAndSecretMetadata()
    {
        // Defaults: Text widget, no group, order 0, not secret.
        var attribute = new ConfigFieldAttribute();
        attribute.Widget.ShouldBe(ConfigFieldWidget.Text);
        attribute.Group.ShouldBeNull();
        attribute.Order.ShouldBe(0);
        attribute.Secret.ShouldBeFalse();

        // The members are settable (the metadata surface downstream layers rely on).
        var configured = new ConfigFieldAttribute
        {
            Widget = ConfigFieldWidget.Secret,
            Group = "auth",
            Order = 7,
            Secret = true,
        };
        configured.Widget.ShouldBe(ConfigFieldWidget.Secret);
        configured.Group.ShouldBe("auth");
        configured.Order.ShouldBe(7);
        configured.Secret.ShouldBeTrue();
    }

    // -- 4. Secret-flagged config field carries the Secret marker (ApiKey common-case) --

    [Fact]
    public void ConfigField_OnProviderApiKey_IsSecret()
    {
        var prop = GetProp(typeof(ProviderConfig), nameof(ProviderConfig.ApiKey));

        var field = prop.GetCustomAttribute<ConfigFieldAttribute>();

        field.ShouldNotBeNull("Provider ApiKey should be annotated as a secret config field.");
        field!.Secret.ShouldBeTrue();
        field.Widget.ShouldBe(ConfigFieldWidget.Secret);
    }

    // -- 5. [DefaultValue] is reflectable on a known annotated field --

    [Fact]
    public void DefaultValue_OnDateTimeInjectionFormat_IsIso8601()
    {
        var prop = GetProp(typeof(DateTimeInjectionConfig), nameof(DateTimeInjectionConfig.Format));

        var defaultValue = prop.GetCustomAttribute<DefaultValueAttribute>();

        defaultValue.ShouldNotBeNull("Format should carry a [DefaultValue] annotation.");
        defaultValue!.Value.ShouldBe("iso8601");
    }

    // -- 5b. Extended coverage (#2013): secret-bearing and common-case fields across the
    //         broader config POCO tree now carry [ConfigField]. These lock the coverage in. --
    [Theory]
    [InlineData(typeof(SatelliteConfig), nameof(SatelliteConfig.ApiKey))]
    [InlineData(typeof(BotNexus.Extensions.Channels.Telegram.TelegramBotConfig), nameof(BotNexus.Extensions.Channels.Telegram.TelegramBotConfig.BotToken))]
    [InlineData(typeof(BotNexus.Extensions.Channels.Telegram.TelegramBotConfig), nameof(BotNexus.Extensions.Channels.Telegram.TelegramBotConfig.WebhookSecretToken))]
    [InlineData(typeof(BotNexus.Extensions.Channels.Telegram.TelegramGatewayOptions), nameof(BotNexus.Extensions.Channels.Telegram.TelegramGatewayOptions.BotToken))]
    [InlineData(typeof(BotNexus.Extensions.Channels.Telegram.TelegramGatewayOptions), nameof(BotNexus.Extensions.Channels.Telegram.TelegramGatewayOptions.WebhookSecretToken))]
    public void ConfigField_OnSecretBearingProperty_IsMarkedSecret(Type type, string propertyName)
    {
        var prop = GetProp(type, propertyName);
        var field = prop.GetCustomAttribute<ConfigFieldAttribute>();
        field.ShouldNotBeNull($"{type.Name}.{propertyName} should carry a [ConfigField] annotation.");
        field!.Secret.ShouldBeTrue($"{type.Name}.{propertyName} is secret-bearing and must be marked Secret.");
        field.Widget.ShouldBe(ConfigFieldWidget.Secret);
    }

    [Theory]
    [InlineData(typeof(SubAgentOptions), nameof(SubAgentOptions.MaxConcurrentPerSession))]
    [InlineData(typeof(GatewayOptions), nameof(GatewayOptions.MaxCallChainDepth))]
    [InlineData(typeof(SessionWarmupOptions), nameof(SessionWarmupOptions.Enabled))]
    [InlineData(typeof(SessionCleanupOptions), nameof(SessionCleanupOptions.SessionTtl))]
    [InlineData(typeof(ConversationRetentionOptions), nameof(ConversationRetentionOptions.AutoArchiveEnabled))]
    [InlineData(typeof(CanvasToolOptions), nameof(CanvasToolOptions.MaxKeyLength))]
    [InlineData(typeof(DelayToolOptions), nameof(DelayToolOptions.MaxDelaySeconds))]
    [InlineData(typeof(FileWatcherToolOptions), nameof(FileWatcherToolOptions.MaxTimeoutSeconds))]
    [InlineData(typeof(AgentExchangeOptions), nameof(AgentExchangeOptions.AccessPolicy))]
    [InlineData(typeof(SatelliteConfig), nameof(SatelliteConfig.Enabled))]
    public void ConfigField_OnUserFacingProperty_IsPresent(Type type, string propertyName)
    {
        var prop = GetProp(type, propertyName);
        var field = prop.GetCustomAttribute<ConfigFieldAttribute>();
        field.ShouldNotBeNull($"{type.Name}.{propertyName} should carry a [ConfigField] annotation (#2013 coverage).");
        var display = prop.GetCustomAttribute<DisplayAttribute>();
        display.ShouldNotBeNull($"{type.Name}.{propertyName} should carry a [Display] annotation.");
        display!.GetName().ShouldNotBeNullOrWhiteSpace();
    }

    // -- 6. Behaviour-unchanged guard: a representative config still validates clean --

    [Fact]
    public void Annotations_DoNotChangeValidation_RepresentativeConfigStillValidatesClean()
    {
        // Mirrors SchemaValidationTests' happy-path config plus an explicit DateTimeInjection
        // section so the annotated canary participates in schema validation. If any additive
        // annotation (e.g. a [Required]/[Range]/[RegularExpression]) leaked into the NJsonSchema
        // generated from PlatformConfig in a way that rejects a valid config, this fails.
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                ListenUrl = "http://localhost:5005",
                DefaultAgentId = "assistant",
                LogLevel = "Information",
                DateTimeInjection = new DateTimeInjectionConfig
                {
                    Enabled = true,
                    Timezone = "UTC",
                    Format = "iso8601",
                },
            },
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["copilot"] = new() { ApiKey = "provider-key", DefaultModel = "gpt-4.1" },
            },
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["assistant"] = new() { Provider = "copilot", Model = "gpt-4.1", Enabled = true },
            },
        };

        // Both validation paths used by the options validator must stay clean.
        PlatformConfigLoader.Validate(config).ShouldBeEmpty();
        PlatformConfigSchema.ValidateObject(config).ShouldBeEmpty();
    }
}
