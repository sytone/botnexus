using BotNexus.Gateway.Extensions;

namespace BotNexus.Gateway.Tests.Extensions;

/// <summary>
/// Guards the shared-assembly contract of <see cref="ExtensionAssemblyLoadContext"/>. Dynamically
/// loaded channel extensions must receive the host's type identity for the configuration
/// assemblies; otherwise DI cannot satisfy an <c>IConfiguration</c> constructor parameter and
/// silently injects null, breaking config self-binding (regression: Service Bus channel adapter).
/// </summary>
public class ExtensionAssemblyLoadContextSharedAssemblyTests
{
    [Theory]
    [InlineData("Microsoft.Extensions.Configuration.Abstractions")]
    [InlineData("Microsoft.Extensions.Configuration.Binder")]
    [InlineData("Microsoft.Extensions.Options")]
    [InlineData("Microsoft.Extensions.DependencyInjection.Abstractions")]
    [InlineData("Microsoft.Extensions.Logging.Abstractions")]
    // System.IO.Abstractions assemblies must be shared so an extension's IFileSystem endpoint/tool
    // parameter keeps the host's type identity and stays bindable as a DI service (regression #2184).
    [InlineData("Testably.Abstractions.FileSystem.Interface")]
    [InlineData("TestableIO.System.IO.Abstractions.Wrappers")]
    public void IsHostAssembly_returns_true_for_shared_configuration_and_di_assemblies(string assemblyName)
    {
        ExtensionAssemblyLoadContext.IsHostAssembly(assemblyName).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Azure.Messaging.ServiceBus")]
    [InlineData("Azure.Identity")]
    [InlineData("SomeRandom.Extension.PrivateDependency")]
    public void IsHostAssembly_returns_false_for_extension_private_assemblies(string assemblyName)
    {
        ExtensionAssemblyLoadContext.IsHostAssembly(assemblyName).ShouldBeFalse();
    }
}
