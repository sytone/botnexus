using System.IO.Abstractions.TestingHelpers;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Extensions;
using Shouldly;

namespace BotNexus.Gateway.Tests.Extensions;

/// <summary>
/// Covers the extension probe-root resolution precedence introduced for issue #2376.
/// </summary>
/// <remarks>
/// The container image bakes its extensions at <c>/app/extensions</c> because
/// <c>BOTNEXUS_HOME</c> (<c>/app/config</c>) is a declared Docker <c>VOLUME</c>: anything written
/// under it at build time is shadowed by the caller's mount, so the default
/// <c>{home}/extensions</c> probe path was an empty directory and the published image discovered
/// zero extensions. The <c>BOTNEXUS_EXTENSIONS_PATH</c> override lets the image point the loader at
/// an unshadowed path without overriding it for local development.
/// </remarks>
[Collection("IntegrationTests")]
public sealed class ExtensionsPathResolutionTests : IDisposable
{
    /// <summary>
    /// These tests mutate process-global environment variables, so they must not run concurrently
    /// with each other -- OR with anything else that reads them.
    /// </summary>
    /// <remarks>
    /// #2825: this class previously declared its own private <c>ExtensionsPathEnvironment</c>
    /// collection. That serialised its tests against EACH OTHER but against nothing else, which is
    /// the weaker of the two guarantees it needs: it reassigns <c>BOTNEXUS_HOME</c>, which the
    /// gateway-host and configuration-reload tests read. With
    /// <c>parallelizeTestCollections: true</c> a private collection runs CONCURRENTLY with the
    /// shared one, so the mutation was still visible to classes mid-assertion. Joining the shared
    /// serialising collection is what actually prevents the interleave.
    ///
    /// <para>
    /// The name is retained only so existing references keep compiling; it no longer designates a
    /// separate collection.
    /// </para>
    /// </remarks>
    public const string CollectionName = "IntegrationTests";

    private readonly string? _originalExtensionsPath =
        Environment.GetEnvironmentVariable(ServiceCollectionExtensions.ExtensionsPathEnvVar);

    private readonly string? _originalHome =
        Environment.GetEnvironmentVariable(BotNexusHome.HomeOverrideEnvVar);

    [Fact]
    public void ResolveExtensionsPath_PrefersExplicitConfigOverEnvironment()
    {
        var configured = Path.Combine(Path.GetTempPath(), "botnexus-configured-extensions");
        var environment = Path.Combine(Path.GetTempPath(), "botnexus-env-extensions");
        Environment.SetEnvironmentVariable(ServiceCollectionExtensions.ExtensionsPathEnvVar, environment);

        var resolved = ServiceCollectionExtensions.ResolveExtensionsPath(
            new ExtensionsConfig { Path = configured },
            new MockFileSystem());

        resolved.ShouldBe(Path.GetFullPath(configured));
    }

    [Fact]
    public void ResolveExtensionsPath_UsesEnvironmentOverrideWhenConfigIsSilent()
    {
        var environment = Path.Combine(Path.GetTempPath(), "botnexus-env-extensions");
        Environment.SetEnvironmentVariable(ServiceCollectionExtensions.ExtensionsPathEnvVar, environment);

        var resolved = ServiceCollectionExtensions.ResolveExtensionsPath(
            new ExtensionsConfig(),
            new MockFileSystem());

        resolved.ShouldBe(Path.GetFullPath(environment));
    }

    [Fact]
    public void ResolveExtensionsPath_FallsBackToHomeWhenNeitherIsSet()
    {
        var home = Path.Combine(Path.GetTempPath(), "botnexus-home-fallback");
        Environment.SetEnvironmentVariable(ServiceCollectionExtensions.ExtensionsPathEnvVar, null);
        Environment.SetEnvironmentVariable(BotNexusHome.HomeOverrideEnvVar, home);

        var resolved = ServiceCollectionExtensions.ResolveExtensionsPath(
            extensionConfig: null,
            new MockFileSystem());

        resolved.ShouldBe(Path.Combine(Path.GetFullPath(home), "extensions"));
    }

    [Fact]
    public void ResolveExtensionsPath_IgnoresWhitespaceOnlyEnvironmentOverride()
    {
        var home = Path.Combine(Path.GetTempPath(), "botnexus-home-fallback");
        Environment.SetEnvironmentVariable(ServiceCollectionExtensions.ExtensionsPathEnvVar, "   ");
        Environment.SetEnvironmentVariable(BotNexusHome.HomeOverrideEnvVar, home);

        var resolved = ServiceCollectionExtensions.ResolveExtensionsPath(
            extensionConfig: null,
            new MockFileSystem());

        resolved.ShouldBe(Path.Combine(Path.GetFullPath(home), "extensions"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(
            ServiceCollectionExtensions.ExtensionsPathEnvVar, _originalExtensionsPath);
        Environment.SetEnvironmentVariable(BotNexusHome.HomeOverrideEnvVar, _originalHome);
    }
}
