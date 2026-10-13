using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using BotNexus.Gateway.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BotNexus.Gateway.Tests.Api;

// CWD and PATH are process-wide: this collection must not overlap other collections.
[CollectionDefinition("IntegrationTests", DisableParallelization = true)]
public sealed class IntegrationTestsCollection;

[Collection("IntegrationTests")]
public sealed class VersionProvenanceTests
{
    [Fact]
    public async Task VersionEndpoint_ForeignWorkingDirectory_ReturnsEmbeddedCommitNotCheckoutHead()
    {
        const string foreignSha = "1111111111111111111111111111111111111111";
        var directory = Path.Combine(Path.GetTempPath(), "botnexus-version-" + Guid.NewGuid().ToString("N"));
        var originalDirectory = Environment.CurrentDirectory;
        Directory.CreateDirectory(Path.Combine(directory, ".git", "refs", "heads"));
        Directory.CreateDirectory(Path.Combine(directory, ".git", "objects"));
        File.WriteAllText(Path.Combine(directory, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(directory, ".git", "refs", "heads", "main"), foreignSha + "\n");
        File.WriteAllText(Path.Combine(directory, ".git", "config"), "[core]\nrepositoryformatversion = 0\nbare = false\n");
        try
        {
            await using var factory = CreateTestFactory();
            using var client = factory.CreateClient();
            Environment.CurrentDirectory = directory;

            using var response = await client.GetAsync("/api/version");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            AssertVersion(document.RootElement);
            document.RootElement.GetProperty("commit").GetString().ShouldNotBe(foreignSha[..7]);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task VersionEndpoint_GitUnavailable_ReturnsEmbeddedCommitAndSameVersionShape()
    {
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            await using var factory = CreateTestFactory();
            using var client = factory.CreateClient();
            Environment.SetEnvironmentVariable("PATH", string.Empty);

            using var response = await client.GetAsync("/api/version");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            AssertVersion(document.RootElement);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Theory]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData("  ", "unknown")]
    [InlineData("unknown", "unknown")]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123", "abcdef0123456789abcdef0123456789abcdef0123")]
    public void EmbeddedCommit_MissingBlankUnknownOrKnownMetadata_IsExplicitAndNeverTruncated(string? metadata, string expected)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("VersionMetadata" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.RunAndCollect);
        if (metadata is not null)
        {
            var constructor = typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)]);
            constructor.ShouldNotBeNull();
            assembly.SetCustomAttribute(new CustomAttributeBuilder(constructor, ["CommitSha", metadata]));
        }

        // Exercise the same metadata reader used to initialize the real gateway identity.
        var reader = typeof(GatewayBuildInfo).GetMethod("ReadCommitSha", BindingFlags.NonPublic | BindingFlags.Static);
        reader.ShouldNotBeNull("build identity must normalize missing/blank metadata without consulting runtime Git");
        reader.Invoke(null, [assembly]).ShouldBe(expected);
    }

    private static void AssertVersion(JsonElement root)
    {
        root.EnumerateObject().Select(property => property.Name).Order().ShouldBe(new[] { "commit", "version" });
        var embeddedCommit = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "CommitSha")?.Value;
        var expectedCommit = string.IsNullOrWhiteSpace(embeddedCommit) ? "unknown" : embeddedCommit;
        root.GetProperty("commit").GetString().ShouldBe(expectedCommit);
        root.GetProperty("commit").GetString().ShouldBe(GatewayBuildInfo.CommitSha);
        var version = root.GetProperty("version").GetString();
        version.ShouldNotBeNull();
        version.ShouldMatch("^[0-9]{14}$");
        version.ShouldBe(File.GetLastWriteTimeUtc(typeof(Program).Assembly.Location).ToString("yyyyMMddHHmmss"));
    }

    private static WebApplicationFactory<Program> CreateTestFactory()
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseUrls("http://127.0.0.1:0");
            builder.ConfigureServices(services =>
            {
                foreach (var descriptor in services.Where(service => service.ServiceType == typeof(IHostedService)).ToList())
                    services.Remove(descriptor);
            });
        });
}
