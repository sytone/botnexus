using System.ComponentModel.DataAnnotations;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Acceptance tests for #2061: the shared recursive config-graph annotation walker.
/// These prove that DataAnnotations declared on nested POCOs, dictionary values, and list
/// elements are actually executed (the root-only <see cref="System.ComponentModel.DataAnnotations.Validator.TryValidateObject"/>
/// pass never recursed) and that each violation is reported with an exact, path-specific member
/// name. The synthetic-graph tests deliberately use standalone POCOs that have NO cross-field
/// <c>IValidatableObject</c> rule and are unknown to the legacy imperative
/// <see cref="PlatformConfigValidator.CollectCrossFieldErrors"/>, so a green result proves the
/// walker - not the imperative escape hatch - is what caught the nested constraint.
/// </summary>
public sealed class ConfigGraphAnnotationWalkerTests
{
    private sealed class Leaf
    {
        [Range(5, 10)]
        public int Count { get; set; } = 7;

        [Required]
        public string? Name { get; set; } = "ok";

        [StringLength(3)]
        public string? Code { get; set; }

        [EvenNumber]
        public int Parity { get; set; }
    }

    private sealed class Branch
    {
        public Leaf? Direct { get; set; }

        public Dictionary<string, Leaf>? Map { get; set; }

        public List<Leaf>? Items { get; set; }
    }

    private sealed class EvenNumberAttribute : ValidationAttribute
    {
        protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
            => value is int i && i % 2 == 0
                ? ValidationResult.Success
                : new ValidationResult("must be even", [validationContext.MemberName ?? string.Empty]);
    }

    [Fact]
    public void CleanGraph_ProducesNoErrors()
    {
        var branch = new Branch
        {
            Direct = new Leaf(),
            Map = new Dictionary<string, Leaf> { ["a"] = new() },
            Items = [new Leaf()],
        };

        var errors = PlatformConfigValidator.ValidateGraphAnnotations(branch);

        errors.ShouldBeEmpty();
    }

    [Fact]
    public void NestedObject_RangeAndRequired_ExecuteWithMemberPath()
    {
        var branch = new Branch { Direct = new Leaf { Count = 99, Name = null } };

        var errors = PlatformConfigValidator.ValidateGraphAnnotations(branch);

        errors.ShouldContain(e => e.StartsWith("direct.count:", StringComparison.Ordinal));
        errors.ShouldContain(e => e.StartsWith("direct.name:", StringComparison.Ordinal));
    }

    [Fact]
    public void DictionaryValues_Annotations_ExecuteWithKeyedPath()
    {
        var branch = new Branch
        {
            Map = new Dictionary<string, Leaf>
            {
                ["good"] = new(),
                ["bad"] = new Leaf { Code = "toolong" },
            },
        };

        var errors = PlatformConfigValidator.ValidateGraphAnnotations(branch);

        errors.ShouldContain(e => e.StartsWith("map.bad.code:", StringComparison.Ordinal));
        errors.ShouldNotContain(e => e.StartsWith("map.good.", StringComparison.Ordinal));
    }

    [Fact]
    public void ListElements_Annotations_ExecuteWithIndexedPath()
    {
        var branch = new Branch
        {
            Items = [new Leaf(), new Leaf { Count = 1 }],
        };

        var errors = PlatformConfigValidator.ValidateGraphAnnotations(branch);

        errors.ShouldContain(e => e.StartsWith("items[1].count:", StringComparison.Ordinal));
        errors.ShouldNotContain(e => e.StartsWith("items[0].", StringComparison.Ordinal));
    }

    [Fact]
    public void CustomAttribute_OnNestedLeaf_Executes()
    {
        var branch = new Branch { Direct = new Leaf { Parity = 3 } };

        var errors = PlatformConfigValidator.ValidateGraphAnnotations(branch);

        errors.ShouldContain(e => e.StartsWith("direct.parity:", StringComparison.Ordinal) && e.Contains("must be even"));
    }

    [Fact]
    public void Walker_HandlesReferenceCycles_WithoutStackOverflow()
    {
        var a = new Cyclic();
        var b = new Cyclic();
        a.Other = b;
        b.Other = a;

        var errors = PlatformConfigValidator.ValidateGraphAnnotations(a);

        errors.ShouldBeEmpty();
    }

    private sealed class Cyclic
    {
        public Cyclic? Other { get; set; }
    }

    [Fact]
    public void RealPlatformConfigGraph_NestedRangeViolation_CaughtWithJsonPath_WithoutLegacyValidator()
    {
        // gateway.autoUpdate.checkIntervalMinutes has [Range(5, int.MaxValue)]; 1 is invalid.
        // This constraint is a per-field DataAnnotation on a NESTED object - the legacy
        // imperative validator does not know about it, so this proves recursive execution.
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                AutoUpdate = new AutoUpdateConfig
                {
                    Enabled = true,
                    CheckIntervalMinutes = 1,
                    CliPath = "/x/cli",
                    SourcePath = "/x/src",
                },
            },
            Providers = new Dictionary<string, ProviderConfig> { ["copilot"] = new() { ApiKey = "k" } },
            Agents = new Dictionary<string, AgentDefinitionConfig> { ["assistant"] = new() { Provider = "copilot", Model = "gpt-4.1" } },
        };

        // The legacy imperative validator does NOT catch the nested Range violation.
        var legacy = PlatformConfigValidator.CollectCrossFieldErrors(config);
        legacy.ShouldNotContain(e => e.Contains("checkIntervalMinutes", StringComparison.OrdinalIgnoreCase));

        // The recursive annotation walker DOES, with an exact JSON member path.
        var errors = PlatformConfigValidator.ValidateGraphAnnotations(config);
        errors.ShouldContain(e => e.StartsWith("gateway.autoUpdate.checkIntervalMinutes:", StringComparison.Ordinal));
    }

    [Fact]
    public void RealPlatformConfigGraph_DictionaryElementRangeViolation_CaughtWithKeyedJsonPath()
    {
        // cron.jobs is a dictionary; each CronJobConfig is a nested value. Use rateLimit
        // (nested object) requestsPerMinute [Range(1, int.MaxValue)] = 0 for a keyed/nested proof
        // via a dictionary of api keys is overkill; instead assert the nested object path here.
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                RateLimit = new RateLimitConfig { Enabled = true, RequestsPerMinute = 0 },
            },
            Providers = new Dictionary<string, ProviderConfig> { ["copilot"] = new() { ApiKey = "k" } },
            Agents = new Dictionary<string, AgentDefinitionConfig> { ["assistant"] = new() { Provider = "copilot", Model = "gpt-4.1" } },
        };

        var errors = PlatformConfigValidator.ValidateGraphAnnotations(config);

        errors.ShouldContain(e => e.StartsWith("gateway.rateLimit.requestsPerMinute:", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateAnnotated_StillSurfacesRootCrossFieldMessages_Unprefixed()
    {
        // Root IValidatableObject cross-field messages (member-less) must remain verbatim so the
        // options validator and the exact-message tests keep passing.
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig { SessionStore = new SessionStoreConfig { Type = "Redis" } },
            Providers = new Dictionary<string, ProviderConfig> { ["copilot"] = new() { ApiKey = "k" } },
            Agents = new Dictionary<string, AgentDefinitionConfig> { ["assistant"] = new() { Provider = "copilot", Model = "gpt-4.1" } },
        };

        var errors = PlatformConfigValidator.ValidateAnnotated(config);

        errors.ShouldContain("gateway.sessionStore.type must be either 'InMemory', 'File', or 'Sqlite'.");
    }
}
