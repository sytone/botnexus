using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Core.Tests;

public sealed class GenerationContractArchitectureTests
{
    [Fact]
    public void AgentGenerationSettings_UseSemanticContract_NotMixedProviderOptions()
    {
        typeof(AgentOptions).GetProperty(nameof(AgentOptions.GenerationSettings))?.PropertyType.ShouldBe(typeof(GenerationOptions));
        typeof(AgentLoopConfig).GetProperty(nameof(AgentLoopConfig.GenerationSettings))?.PropertyType.ShouldBe(typeof(GenerationOptions));
    }

    [Fact]
    public void AgentCoreAssembly_DoesNotExposeProviderPrivateStreamOptions()
    {
        var publicContractTypes = typeof(AgentOptions).Assembly
            .GetExportedTypes()
            .SelectMany(type => type.GetProperties().Select(property => property.PropertyType)
                .Concat(type.GetMethods().Select(method => method.ReturnType))
                .Concat(type.GetMethods().SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType))))
            .ToArray();

        publicContractTypes.ShouldNotContain(typeof(StreamOptions));
        publicContractTypes.ShouldNotContain(typeof(SimpleStreamOptions));
        typeof(GenerationOptions).GetProperties().Select(property => property.PropertyType).ShouldNotContain(typeof(Transport));
    }
}
