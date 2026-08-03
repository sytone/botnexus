using System.Reflection;
using BotNexus.Gateway.Channels.Startup;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BotNexus.Gateway.Tests.Extensions;

/// <summary>
/// Pins the #2731 discrimination: an <see cref="IHostedService"/> contributed by a CHANNEL
/// extension is registered behind <see cref="ChannelFaultBarrierHostedService"/>, while a hosted
/// service from any NON-channel extension keeps the container's default <c>StopHost</c> semantics.
///
/// The discrimination is the whole point of the fix. Wrapping everything would be a blanket
/// <c>BackgroundServiceExceptionBehavior.Ignore</c> in disguise - it would equally swallow faults
/// in config hydration, the memory indexer and session cleanup, services whose failure genuinely
/// means the process is unfit to serve. Wrapping nothing leaves the 2026-08-01 outage shape intact,
/// where a missing Telegram BotToken terminated the entire host. Only a test that asserts BOTH
/// sides can tell those three outcomes apart.
/// </summary>
public sealed class ChannelExtensionHostedServiceBarrierTests
{
    private sealed class StubHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static ExtensionManifest Manifest(string id, params string[] extensionTypes) => new()
    {
        Id = id,
        Name = id,
        Version = "1.0.0",
        EntryAssembly = $"{id}.dll",
        ExtensionTypes = extensionTypes,
    };

    /// <summary>
    /// Drives the loader's real registration path rather than a reimplementation of it, so the
    /// assertions cannot drift from the branch actually taken at startup.
    /// </summary>
    private static IServiceCollection RegisterHostedService(ExtensionManifest manifest)
    {
        var services = new ServiceCollection();
        var loader = (AssemblyLoadContextExtensionLoader)System.Runtime.CompilerServices
            .RuntimeHelpers.GetUninitializedObject(typeof(AssemblyLoadContextExtensionLoader));

        typeof(AssemblyLoadContextExtensionLoader)
            .GetField("_services", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(loader, services);
        typeof(AssemblyLoadContextExtensionLoader)
            .GetField("_registeredExtensionServices", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(loader, new List<(Type, Type)>());
        typeof(AssemblyLoadContextExtensionLoader)
            .GetField("_channelHostedServiceDescriptors", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(loader, new Dictionary<Type, List<ServiceDescriptor>>());

        var register = typeof(AssemblyLoadContextExtensionLoader)
            .GetMethod("RegisterServices", BindingFlags.Instance | BindingFlags.NonPublic)!;

        register.Invoke(loader, [
            new List<(Type ServiceContract, Type Implementation)>
            {
                (typeof(IHostedService), typeof(StubHostedService)),
            },
            manifest,
        ]);

        return services;
    }

    private static bool IsBehindBarrier(IServiceCollection services) =>
        services.Any(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationFactory is not null);

    [Theory]
    [InlineData("channel")]
    [InlineData("Channel")]
    public void ChannelExtensionHostedService_IsPlacedBehindTheFaultBarrier(string extensionType)
    {
        var services = RegisterHostedService(Manifest("telegram", extensionType));

        IsBehindBarrier(services).ShouldBeTrue();
        services.Any(d => d.ServiceType == typeof(ChannelStartupReport)).ShouldBeTrue();
    }

    /// <summary>
    /// The load-bearing half. A hosted service from a non-channel extension must NOT be contained:
    /// its failure must still reach <c>StopHost</c>. Without this clause the fix is indistinguishable
    /// from a blanket suppression.
    /// </summary>
    [Theory]
    [InlineData("tool")]
    [InlineData("media-handler")]
    public void NonChannelExtensionHostedService_KeepsDefaultStopHostSemantics(string extensionType)
    {
        var services = RegisterHostedService(Manifest("skills", extensionType));

        IsBehindBarrier(services).ShouldBeFalse();
        services.Any(d => d.ServiceType == typeof(ChannelStartupReport)).ShouldBeFalse();
    }

    /// <summary>
    /// An extension declaring no types at all is not a channel, so it must fall through to the
    /// default path. Pins the null/empty branch of the manifest check rather than assuming it.
    /// </summary>
    [Fact]
    public void ExtensionWithNoDeclaredTypes_IsNotTreatedAsAChannel()
    {
        var services = RegisterHostedService(Manifest("mystery"));

        IsBehindBarrier(services).ShouldBeFalse();
    }
}
