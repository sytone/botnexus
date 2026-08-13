using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// #3037: configuration error severity is keyed on <em>survivability</em> - can the gateway run with
/// the object that was actually bound? - rather than solely on agent scope.
/// </summary>
/// <remarks>
/// <para>The structural error class under test (<c>NoAdditionalPropertiesAllowed</c>) cannot be
/// produced by serialising a typed <see cref="PlatformConfig"/>, because an unknown key has nowhere
/// to live on the typed model. The field occurrence arises when the generated schema and the
/// document disagree (the #3036 <c>featureManagement</c> case). These tests therefore drive the real
/// <see cref="PlatformConfigOptionsValidator.Validate"/> through its internal structural-error seam,
/// which is the same list the production schema validator feeds it.</para>
/// </remarks>
public sealed class PlatformConfigSurvivabilityTests
{
    private const string UnknownKeyError =
        "schema.featureManagement: NoAdditionalPropertiesAllowed (NoAdditionalPropertiesAllowed: #/featureManagement)";

    private static PlatformConfigOptionsValidator ValidatorEmitting(
        ILogger<PlatformConfigOptionsValidator>? logger,
        params string[] structuralErrors)
        => new(logger, _ => structuralErrors);

    // -- AC1: a purely structural unknown-property error is survivable ------------------------

    [Fact]
    public void Validate_WithUnknownPropertyOnly_Succeeds()
    {
        var result = ValidatorEmitting(null, UnknownKeyError).Validate(null, new PlatformConfig());

        result.Succeeded.ShouldBeTrue(
            "An unknown property cannot change the bound PlatformConfig - IConfiguration binding " +
            "ignores unmapped keys - so refusing to start buys nothing: " +
            string.Join("; ", result.Failures ?? []));
    }

    [Fact]
    public void OptionsMonitor_CurrentValue_WithUnknownPropertyOnly_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddOptions<PlatformConfig>();
        services.AddSingleton<IValidateOptions<PlatformConfig>>(
            _ => ValidatorEmitting(null, UnknownKeyError));

        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<PlatformConfig>>();

        Should.NotThrow(() => monitor.CurrentValue);
    }

    [Fact]
    public void OptionsMonitor_CurrentValue_WithFatalError_StillThrows()
    {
        // Proves the monitor assertion above is not vacuous: the same harness DOES throw when the
        // error is one the classifier considers fatal.
        var services = new ServiceCollection();
        services.AddOptions<PlatformConfig>();
        services.AddSingleton<IValidateOptions<PlatformConfig>>(
            _ => ValidatorEmitting(null, "schema.gateway.listenUrl: StringExpected (StringExpected: #/gateway/listenUrl)"));

        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<PlatformConfig>>();

        Should.Throw<OptionsValidationException>(() => monitor.CurrentValue);
    }

    // -- AC2: every currently-fatal class stays fatal (regression fence) ----------------------

    [Fact]
    public void Validate_WithUnparseableEnum_StillFails()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig { LogLevel = "definitely-not-a-level" }
        };

        new PlatformConfigOptionsValidator().Validate(null, config).Failed.ShouldBeTrue(
            "An unparseable enum describes a value that IS bound and must stay fatal.");
    }

    [Fact]
    public void Validate_WithOutOfRangeAnnotatedValue_StillFails()
    {
        var config = new PlatformConfig
        {
            AgentDefaults = new AgentDefaultsConfig
            {
                Heartbeat = new BotNexus.Gateway.Abstractions.Models.HeartbeatAgentConfig
                {
                    IntervalMinutes = 0
                }
            }
        };

        new PlatformConfigOptionsValidator().Validate(null, config).Failed.ShouldBeTrue(
            "An out-of-range annotated value describes a bound value and must stay fatal.");
    }

    [Fact]
    public void Validate_WithMissingRequiredField_StillFails()
    {
        // A real missing-required-field rule on a NON-agent-scoped node: quarantine cannot absorb
        // it, and survivability must not either.
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                SessionStore = new SessionStoreConfig { Type = "File" } // FilePath deliberately omitted
            }
        };

        var result = new PlatformConfigOptionsValidator().Validate(null, config);

        result.Failed.ShouldBeTrue("A missing required field must stay fatal.");
    }

    [Fact]
    public void Validate_WithCrossFieldViolation_StillFails()
    {
        // gateway.listenUrl scheme validation is a PlatformConfigValidator cross-field rule reached
        // through PlatformConfig.Validate, not a per-field annotation.
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig { ListenUrl = "ftp://localhost:5005" }
        };

        var result = new PlatformConfigOptionsValidator().Validate(null, config);

        result.Failed.ShouldBeTrue(
            "A cross-field violation invalidates the object the gateway will use and must stay fatal.");
    }

    // -- AC3: existing agent semantics preserved ---------------------------------------------

    [Fact]
    public void Validate_WithStructuralErrorUnderAgentsDefaults_StillFails()
    {
        var result = ValidatorEmitting(
                null,
                "schema.agents.defaults.mystery: NoAdditionalPropertiesAllowed (NoAdditionalPropertiesAllowed: #/agents/defaults/mystery)")
            .Validate(null, new PlatformConfig());

        result.Failed.ShouldBeTrue(
            "agents.defaults seeds every agent, so even a structural error there stays fatal.");
    }

    [Fact]
    public void Validate_WithStructuralErrorUnderNamedAgent_RemainsQuarantined()
    {
        var result = ValidatorEmitting(
                null,
                "schema.agents.coder.mystery: NoAdditionalPropertiesAllowed (NoAdditionalPropertiesAllowed: #/agents/coder/mystery)")
            .Validate(null, new PlatformConfig());

        result.Succeeded.ShouldBeTrue("Named-agent quarantine is unchanged by #3037.");
    }

    [Fact]
    public void IsSurvivableStructuralError_ClassifiesDeliberately()
    {
        PlatformConfigOptionsValidator.IsSurvivableStructuralError(UnknownKeyError).ShouldBeTrue();
        PlatformConfigOptionsValidator.IsSurvivableStructuralError(
            "schema.gateway.listenUrl: StringExpected (StringExpected: #/gateway/listenUrl)").ShouldBeFalse();
        PlatformConfigOptionsValidator.IsSurvivableStructuralError(
            "schema.agents.defaults.x: NoAdditionalPropertiesAllowed (#/agents/defaults/x)").ShouldBeFalse();
        PlatformConfigOptionsValidator.IsSurvivableStructuralError("").ShouldBeFalse();
    }

    // -- AC4: exactly one warning naming the path, and none when the key is absent ------------

    [Fact]
    public void Validate_WithUnknownProperty_EmitsExactlyOneWarningNamingThePath()
    {
        var logger = new CapturingLogger();
        var validator = ValidatorEmitting(logger, UnknownKeyError);

        // Re-validated deliberately: IOptionsMonitor re-runs the validator on every CurrentValue.
        validator.Validate(null, new PlatformConfig());
        validator.Validate(null, new PlatformConfig());

        var warnings = logger.Entries.Where(e => e.Level >= LogLevel.Warning).ToArray();
        warnings.Length.ShouldBe(1, "the operator must be told once, not once per config read.");
        warnings[0].Message.Contains("featureManagement", StringComparison.Ordinal).ShouldBeTrue(
            $"the warning must name the offending property path; got: {warnings[0].Message}");
    }

    [Fact]
    public void Validate_WithoutUnknownProperty_EmitsNoWarning()
    {
        var logger = new CapturingLogger();

        ValidatorEmitting(logger).Validate(null, new PlatformConfig()).Succeeded.ShouldBeTrue();

        logger.Entries.ShouldNotContain(e => e.Level >= LogLevel.Warning);
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger<PlatformConfigOptionsValidator>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }
}
