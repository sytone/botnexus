using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// Verifies that operator-supplied secret redaction patterns are validated by
/// <see cref="PlatformConfigValidator"/> (#2727 AC1/AC3).
///
/// The whole point of validating here is that a malformed pattern must be an ERROR the operator sees
/// at startup, naming the offending pattern, rather than an exception thrown later on the logging
/// path or - worse - a silently disabled redactor.
/// </summary>
public sealed class SecretRedactionConfigValidationTests
{
    private static PlatformConfig ConfigWith(params string[] patterns)
        => new()
        {
            Gateway = new GatewaySettingsConfig
            {
                SecretRedaction = new SecretRedactionConfig { Patterns = [.. patterns] },
            },
        };

    [Fact]
    public void Validate_ValidPattern_ProducesNoErrors()
    {
        var errors = PlatformConfigValidator.Validate(ConfigWith("deployment-secret-[a-z-]+"));

        errors.ShouldNotContain(e => e.Contains("secretRedaction", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_AbsentSection_ProducesNoErrors()
    {
        var errors = PlatformConfigValidator.Validate(new PlatformConfig());

        errors.ShouldNotContain(e => e.Contains("secretRedaction", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_MalformedRegex_ReportsErrorNamingThePattern()
    {
        var errors = PlatformConfigValidator.Validate(ConfigWith("(unclosed"));

        errors.ShouldContain(e =>
            e.Contains("gateway.secretRedaction.patterns", StringComparison.Ordinal)
            && e.Contains("(unclosed", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_EmptyPattern_ReportsError()
    {
        var errors = PlatformConfigValidator.Validate(ConfigWith(""));

        errors.ShouldContain(e =>
            e.Contains("gateway.secretRedaction.patterns", StringComparison.Ordinal)
            && e.Contains("empty", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_WhitespacePattern_ReportsError()
    {
        var errors = PlatformConfigValidator.Validate(ConfigWith("   "));

        errors.ShouldContain(e => e.Contains("gateway.secretRedaction.patterns", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_MatchEverythingPattern_ReportsError()
    {
        var errors = PlatformConfigValidator.Validate(ConfigWith(".*"));

        errors.ShouldContain(e =>
            e.Contains("gateway.secretRedaction.patterns", StringComparison.Ordinal)
            && e.Contains(".*", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ReportsEveryOffendingPattern_NotJustTheFirst()
    {
        var errors = PlatformConfigValidator.Validate(ConfigWith("(unclosed", "deployment-secret-[a-z-]+", "[bad"));

        errors.Count(e => e.Contains("gateway.secretRedaction.patterns", StringComparison.Ordinal)).ShouldBe(2);
    }

    [Fact]
    public void Validate_NegativeMatchTimeout_ReportsError()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                SecretRedaction = new SecretRedactionConfig
                {
                    Patterns = ["deployment-secret-[a-z-]+"],
                    MatchTimeoutMilliseconds = 0,
                },
            },
        };

        var errors = PlatformConfigValidator.Validate(config);

        errors.ShouldContain(e =>
            e.Contains("gateway.secretRedaction.matchTimeoutMilliseconds", StringComparison.Ordinal));
    }

    /// <summary>
    /// The validator and the redactor must agree: anything the validator accepts must construct, and
    /// anything it rejects must be rejected at construction too. Two independent copies of the rules
    /// would drift, and the drift would be silent.
    /// </summary>
    [Theory]
    [InlineData("(unclosed")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".*")]
    public void Validate_RejectedPattern_AlsoRejectedByOptionsCompilation(string pattern)
    {
        PlatformConfigValidator.Validate(ConfigWith(pattern))
            .ShouldContain(e => e.Contains("gateway.secretRedaction.patterns", StringComparison.Ordinal));

        Should.Throw<ArgumentException>(() => new SecretRedactionOptions([pattern]).Compile());
    }

    [Fact]
    public void Validate_AcceptedPattern_CompilesSuccessfully()
    {
        PlatformConfigValidator.Validate(ConfigWith("deployment-secret-[a-z-]+"))
            .ShouldNotContain(e => e.Contains("secretRedaction", StringComparison.OrdinalIgnoreCase));

        Should.NotThrow(() => new SecretRedactionOptions(["deployment-secret-[a-z-]+"]).Compile());
    }
}
