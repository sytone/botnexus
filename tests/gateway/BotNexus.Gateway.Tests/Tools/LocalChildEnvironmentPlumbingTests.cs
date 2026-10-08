using BotNexus.Agent.Core.Tools;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Extensions;
using BotNexus.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Tools;

public sealed class LocalChildEnvironmentPlumbingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Factory_OptionsBindingControlsChildPassThrough(bool configured)
    {
        var name = "BN4749_FACTORY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "synthetic-approved");
        try
        {
            var services = new ServiceCollection();
            if (configured)
            {
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["gateway:localChildEnvironmentPassThrough:0"] = name,
                    ["gateway:shellPreference"] = "pwsh"
                }).Build();
                services.Configure<PlatformConfig>(configuration);
            }
            services.AddBotNexusTools();
            using var provider = services.BuildServiceProvider();
            var policy = provider.GetRequiredService<LocalChildEnvironmentPolicy>();
            var target = new Dictionary<string, string?>();
            LocalChildEnvironment.Populate(target, new Dictionary<string, string?> { [name] = "synthetic-approved" }, policy);
            target.ContainsKey(name).ShouldBe(configured);
            var factory = provider.GetRequiredService<IAgentToolFactory>();
            var tool = factory.CreateTools(WorkingDir.From(Path.GetTempPath()), shellCommand:
                OperatingSystem.IsWindows() ? ["cmd.exe", "/d", "/c"] : ["/bin/sh", "-c"])
                .OfType<ShellTool>().Single();
            var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
                { ["command"] = OperatingSystem.IsWindows() ? "set" : "env" });
            var result = await tool.ExecuteAsync("environment", args);
            string.Join("\n", result.Content.Select(c => c.Value)).Contains("synthetic-approved", StringComparison.Ordinal).ShouldBe(configured);
            if (configured)
                provider.GetRequiredService<IOptions<PlatformConfig>>().Value.Gateway?.LocalChildEnvironmentPassThrough.ShouldBe([name]);
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Fact]
    public async Task Shell_StandaloneSecureDefaultDoesNotExposeSyntheticSecrets()
    {
        var name = "BN4749_SHELL_" + Guid.NewGuid().ToString("N");
        var provider = "BN4749_SHELL_PROVIDER_" + Guid.NewGuid().ToString("N");
        var github = "BN4749_SHELL_GITHUB_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "synthetic-shell-secret");
        Environment.SetEnvironmentVariable(provider, "synthetic-shell-provider");
        Environment.SetEnvironmentVariable(github, "synthetic-shell-github");
        try
        {
            var tool = new ShellTool(shellCommand: OperatingSystem.IsWindows() ? ["cmd.exe", "/d", "/c"] : ["/bin/sh", "-c"]);
            var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
                { ["command"] = OperatingSystem.IsWindows() ? "set" : "env" });
            var result = await tool.ExecuteAsync("environment", args);
            var output = string.Join("\n", result.Content.Select(c => c.Value));
            result.Details.ShouldBeOfType<ShellTool.ShellToolDetails>().ExitCode.ShouldBe(0);
            output.ShouldNotContain("synthetic-shell-secret");
            output.ShouldNotContain("synthetic-shell-provider");
            output.ShouldNotContain("synthetic-shell-github");
            output.ToUpperInvariant().ShouldContain("PATH=");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
            Environment.SetEnvironmentVariable(provider, null);
            Environment.SetEnvironmentVariable(github, null);
        }
    }

    [Fact]
    public void CanonicalConfigPath_PersistsOnlyNamesAndSchemaDoesNotReadAmbientValues()
    {
        const string name = "BN4749_SCHEMA_PROBE";
        const string value = "synthetic-schema-secret";
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            var config = new PlatformConfig();
            var resolver = new ConfigPathResolver();
            resolver.TrySetValue(config, "gateway.localChildEnvironmentPassThrough", "[\"BN4749_SCHEMA_PROBE\"]", out var error).ShouldBeTrue(error);
            config.Gateway?.LocalChildEnvironmentPassThrough.ShouldBe([name]);
            var json = System.Text.Json.JsonSerializer.Serialize(config);
            json.ShouldContain(name);
            json.ShouldNotContain(value);
            var schema = BotNexus.Gateway.Api.Configuration.ConfigSchemaBuilder.Build().ToJsonString();
            schema.ShouldContain("localChildEnvironmentPassThrough");
            schema.ShouldNotContain(value);
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Theory]
    [InlineData("*")]
    [InlineData("TOKEN_*")]
    [InlineData("A=B")]
    [InlineData(" BAD")]
    public void ConfigValidator_RejectsNonExactNames(string name)
    {
        var config = new PlatformConfig { Gateway = new GatewaySettingsConfig { LocalChildEnvironmentPassThrough = [name] } };
        PlatformConfigValidator.Validate(config).ShouldContain(e => e.Contains("gateway.localChildEnvironmentPassThrough", StringComparison.Ordinal));
    }
}
