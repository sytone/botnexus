using System.Text.RegularExpressions;
using BotNexus.Gateway.Tests.Api;

namespace BotNexus.Gateway.Tests.Architecture;

/// <summary>
/// Keeps tests that install the process-wide SQLite identity from overlapping physical-store tests.
/// ConfigurationBuilder.AddPlatformConfiguration only builds a provider; the IServiceCollection
/// overload also calls SqliteStoreIdentityGuard.Configure and therefore needs exclusive execution.
/// </summary>
public sealed class SqliteIdentityTestIsolationTests
{
    [Fact]
    public void IdentityMutatingFixtures_JoinTheExistingNonparallelCollection()
    {
        var root = FindTestProjectRoot();
        var violations = SourceFiles(root)
            .Where(path => Path.GetFileName(path) != nameof(SqliteIdentityTestIsolationTests) + ".cs")
            .Where(path => MutatesIdentity(File.ReadAllText(path)))
            .Where(path => !StripComments(File.ReadAllText(path)).Contains("[Collection(\"IntegrationTests\")]", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        violations.ShouldBeEmpty("Process-wide identity mutators must not overlap physical SQLite tests.");
    }

    [Fact]
    public void IdentityMutatorFence_FindsAllThreeKnownCompositionFixtures()
    {
        var mutators = SourceFiles(FindTestProjectRoot())
            .Where(path => Path.GetFileName(path) != nameof(SqliteIdentityTestIsolationTests) + ".cs")
            .Where(path => MutatesIdentity(File.ReadAllText(path)))
            .Select(Path.GetFileName)
            .ToList();

        mutators.ShouldContain("PlatformConfigurationTests.cs");
        mutators.ShouldContain("GatewayHomeWorldCompositionTests.cs");
        mutators.ShouldContain("WorldIdTests.cs");
        mutators.Count.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Theory]
    [InlineData("var services = new ServiceCollection(); services.AddPlatformConfiguration(path);", true)]
    [InlineData("IServiceCollection registrations = GetServices(); registrations.AddPlatformConfiguration(path);", true)]
    [InlineData("SqliteStoreIdentityGuard.Configure(identity);", true)]
    [InlineData("SqliteStoreIdentityGuard.Reset();", true)]
    [InlineData("new ConfigurationBuilder().AddPlatformConfiguration(path).Build();", false)]
    [InlineData("// services.AddPlatformConfiguration(path);", false)]
    [InlineData("/* SqliteStoreIdentityGuard.Reset(); */", false)]
    public void IdentityMutatorFence_DistinguishesServiceCompositionFromProviderOnlyCalls(string code, bool expected)
        => MutatesIdentity(code).ShouldBe(expected);

    [Fact]
    public void ExistingCollection_IsNonparallelAndOwnsIdentityTeardown()
    {
        var definition = typeof(IntegrationTestsCollection)
            .GetCustomAttributes(typeof(CollectionDefinitionAttribute), inherit: false)
            .Cast<CollectionDefinitionAttribute>().Single();
        var definitionData = typeof(IntegrationTestsCollection).CustomAttributes
            .Single(attribute => attribute.AttributeType == typeof(CollectionDefinitionAttribute));
        definitionData.ConstructorArguments.Single().Value.ShouldBe("IntegrationTests");
        definition.DisableParallelization.ShouldBeTrue();
        typeof(ICollectionFixture<SqliteIdentityCollectionFixture>)
            .IsAssignableFrom(typeof(IntegrationTestsCollection)).ShouldBeTrue();
        foreach (var owner in new[]
        {
            typeof(SqliteIdentityCollectionFixture), typeof(PlatformConfigurationTests),
            typeof(WorldIdTests), typeof(Configuration.GatewayHomeWorldCompositionTests)
        })
            typeof(IDisposable).IsAssignableFrom(owner).ShouldBeTrue();
        typeof(IntegrationTestsCollection).Assembly.ShouldBe(typeof(SqliteIdentityTestIsolationTests).Assembly);
    }

    private static bool MutatesIdentity(string source)
    {
        var code = StripComments(source);
        if (Regex.IsMatch(code, @"SqliteStoreIdentityGuard\s*\.\s*(?:Configure|Reset)\s*\(", RegexOptions.None, TimeSpan.FromSeconds(5)))
            return true;

        // Track the declared service-collection receivers rather than flagging the unrelated
        // IConfigurationBuilder extension with the same method name. This is a source fence,
        // not full interprocedural analysis; host booting is also covered by ConfigPathTestIsolationTests.
        var receivers = Regex.Matches(code,
            @"(?:var|ServiceCollection|IServiceCollection)\s+(\w+)\s*=\s*new\s+ServiceCollection\s*\(|IServiceCollection\s+(\w+)",
            RegexOptions.None, TimeSpan.FromSeconds(5));
        return receivers.Cast<Match>().Any(match =>
        {
            var name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            return Regex.IsMatch(code, $@"\b{Regex.Escape(name)}\s*\.\s*AddPlatformConfiguration\s*\(",
                RegexOptions.None, TimeSpan.FromSeconds(5));
        });
    }

    private static string StripComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline, TimeSpan.FromSeconds(5));
        return Regex.Replace(withoutBlocks, @"//[^\r\n]*", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(5));
    }

    private static IEnumerable<string> SourceFiles(string root)
        => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"));

    private static string FindTestProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")))
                return Path.Combine(current.FullName, "tests", "gateway", "BotNexus.Gateway.Tests");
            current = current.Parent;
        }
        throw new InvalidOperationException("Could not locate repository root from test base directory.");
    }
}
