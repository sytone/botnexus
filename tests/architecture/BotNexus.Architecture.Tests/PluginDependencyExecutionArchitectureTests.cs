using System.Security.Cryptography;
using System.Text.Json;

namespace BotNexus.Architecture.Tests;

/// <summary>Executes plugin policy/parser through the real loader, never testhost's merged dependency graph.</summary>
public sealed class PluginDependencyExecutionArchitectureTests : ArchitectureTest
{
    [Fact]
    public async Task CleanReleasePluginClosure_ExecutesInBothOrders_AndMissingDependencyFails()
    {
        var root = Path.Combine(Path.GetTempPath(), "plugin-loader-" + Guid.NewGuid().ToString("N"));
        var artifacts = Path.Combine(root, "artifacts");
        Directory.CreateDirectory(root);
        try
        {
            using var fixtureDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            // One shared four-root graph, one restore/build, and one practical whole-fixture
            // deadline. Actual remaining runner headroom is unknown until measured.
            var graph = Path.Combine(root, "PluginClosure.csproj");
            var projects = new[]
            {
                "src/gateway/BotNexus.Gateway.Api/BotNexus.Gateway.Api.csproj",
                "src/extensions/BotNexus.Extensions.Mcp/BotNexus.Extensions.Mcp.csproj",
                "src/extensions/BotNexus.Extensions.Plugins.Api/BotNexus.Extensions.Plugins.Api.csproj",
                "tools/BotNexus.ExtensionDependencyProbe/BotNexus.ExtensionDependencyProbe.csproj",
            }.Select(project => new System.Xml.Linq.XElement("ProjectReference",
                new System.Xml.Linq.XAttribute("Include", Repository.Path(project.Split('/')))));
            new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement("Project",
                new System.Xml.Linq.XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new System.Xml.Linq.XElement("PropertyGroup",
                    new System.Xml.Linq.XElement("TargetFramework", "net10.0"),
                    new System.Xml.Linq.XElement("EnableDefaultCompileItems", "false")),
                new System.Xml.Linq.XElement("ItemGroup", projects))).Save(graph);
            var build = await BoundedProbeProcess.RunAsync(Repository.Root,
                ["build", graph, "--configuration", "Release", "--artifacts-path", artifacts, "--nologo", "-v", "minimal", "--disable-build-servers", "/nodeReuse:false", "/p:UseSharedCompilation=false"],
                TimeSpan.FromMinutes(5), fixtureDeadline.Token);
            build.Exit.ShouldBe(0, $"Shared clean Release build:\n{build.Output}\n{build.Error}");
            string Output(string project) => Path.Combine(artifacts, "bin", project, "release");
            var host = Output("BotNexus.Gateway.Api");
            var mcp = Output("BotNexus.Extensions.Mcp");
            var api = Output("BotNexus.Extensions.Plugins.Api");
            var probe = Path.Combine(Output("BotNexus.ExtensionDependencyProbe"), "BotNexus.ExtensionDependencyProbe.dll");
            foreach (var directory in new[] { host, mcp, api })
                Directory.Exists(directory).ShouldBeTrue($"Missing explicitly pinned Release directory: {directory}");
            // Match canonical deployment: copy the source manifest beside its full build output
            // when the project only copied it to the legacy development artifact directory.
            File.Copy(Repository.Path("src", "extensions", "BotNexus.Extensions.Mcp", "botnexus-extension.json"),
                Path.Combine(mcp, "botnexus-extension.json"), overwrite: true);
            File.Copy(probe, Path.Combine(host, Path.GetFileName(probe)));
            var inventories = new[] { host, mcp, api }.ToDictionary(directory => directory,
                directory => Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(
                    path => Path.GetRelativePath(directory, path), path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))));
            Console.WriteLine("PLUGIN_RELEASE_INVENTORY " + JsonSerializer.Serialize(inventories));
            foreach (var order in new[] { "mcp-first", "api-first" })
            {
                var result = await Probe(host, mcp, api, order, Path.Combine(root, order), fixtureDeadline.Token);
                result.Exit.ShouldBe(0, $"{order}:\n{result.Output}\n{result.Error}");
                result.Output.ShouldContain("PLUGIN_CLOSURE_OK " + order);
                result.Output.ShouldContain("POLICY_SKILLS_ONLY");
                result.Output.ShouldContain("NATIVE_ADMISSION_OK");
                result.Output.ShouldContain("PARSER_VALID_AND_INVALID_OK");
            }
            // Remove NJsonSchema from EVERY possible app/private origin in this clean closure.
            // A different extension order or merged testhost must not rescue the missing assembly.
            var removed = new List<string>();
            foreach (var directory in new[] { host, mcp, api })
                foreach (var path in Directory.GetFiles(directory, "NJsonSchema.dll", SearchOption.AllDirectories))
                {
                    removed.Add(path);
                    File.Delete(path);
                }
            removed.ShouldNotBeEmpty("Negative control must remove a real production dependency.");
            var negative = await Probe(host, mcp, api, "mcp-first", Path.Combine(root, "missing-closure"), fixtureDeadline.Token);
            negative.Exit.ShouldNotBe(0, "Missing parser closure must fail, not skip or claim success.");
            negative.Error.ShouldContain("NJsonSchema");
            negative.Output.ShouldNotContain("PLUGIN_CLOSURE_OK");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task<BoundedProbeProcess.Result> Probe(string host, string mcp, string api, string order, string state,
        CancellationToken fixtureToken) => BoundedProbeProcess.RunAsync(host,
        ["exec", "--runtimeconfig", Path.Combine(host, "BotNexus.Gateway.Api.runtimeconfig.json"),
            "--depsfile", Path.Combine(host, "BotNexus.Gateway.Api.deps.json"), Path.Combine(host, "BotNexus.ExtensionDependencyProbe.dll"),
            "plugin-closure", Path.Combine(host, "BotNexus.Gateway.Api.dll"), Path.Combine(mcp, "BotNexus.Extensions.Mcp.dll"),
            Path.Combine(api, "BotNexus.Extensions.Plugins.Api.dll"), order, state], TimeSpan.FromSeconds(45), fixtureToken);
}
