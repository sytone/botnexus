using BotNexus.Extensions.Mcp.Plugins;

namespace BotNexus.Extensions.Mcp.Tests.Plugins;

/// <summary>
/// Pins how a plugin's MCP declaration file is located and read (#2686 AC1).
/// </summary>
public sealed class PluginMcpDeclarationReaderTests : IDisposable
{
    private readonly string _root;

    public PluginMcpDeclarationReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "botnexus-plugin-mcp-read", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Not worth failing a test over.
        }
    }

    private void Write(string relativePath, string content)
    {
        var file = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
    }

    [Fact]
    public void ConventionalPath_IsDiscoveredWhenTheManifestNamesNone()
    {
        Write(".botnexus-plugin/mcp.json", """{ "mcpServers": { "a": { "command": "node" } } }""");

        var declaration = PluginMcpDeclarationReader.Read(_root, declaredPath: null);

        declaration.IsValid.ShouldBeTrue();
        declaration.Servers.Keys.ShouldBe(["a"]);
    }

    [Fact]
    public void BareRootMap_IsAccepted()
    {
        // The ecosystem's .mcp.json is a bare map; refusing it would reject a correct plugin.
        Write(".mcp.json", """{ "a": { "command": "node" } }""");

        var declaration = PluginMcpDeclarationReader.Read(_root, declaredPath: null);

        declaration.IsValid.ShouldBeTrue();
        declaration.Servers.Keys.ShouldBe(["a"]);
    }

    [Fact]
    public void MissingDeclaration_IsSuccessWithNoServers_NotAnError()
    {
        var declaration = PluginMcpDeclarationReader.Read(_root, declaredPath: null);

        declaration.IsValid.ShouldBeTrue("a plugin that declares no servers is not a broken plugin");
        declaration.Servers.ShouldBeEmpty();
    }

    [Fact]
    public void ExplicitPathThatDoesNotExist_IsAnError()
    {
        // Distinct from the convention case: the manifest PROMISED a file, so its absence is a
        // defect in the plugin rather than an absence of declaration.
        var declaration = PluginMcpDeclarationReader.Read(_root, declaredPath: "config/servers.json");

        declaration.IsValid.ShouldBeFalse();
        declaration.Error.ShouldNotBeNull().ShouldContain("does not exist");
    }

    [Fact]
    public void MalformedJson_IsReportedRatherThanTreatedAsEmpty()
    {
        Write(".botnexus-plugin/mcp.json", "{ not json ");

        var declaration = PluginMcpDeclarationReader.Read(_root, declaredPath: null);

        declaration.IsValid.ShouldBeFalse();
        declaration.Error.ShouldNotBeNull().ShouldContain("not valid JSON");
    }

    [Fact]
    public void PathTraversal_IsRefused()
    {
        var declaration = PluginMcpDeclarationReader.Read(_root, declaredPath: "../escape.json");

        declaration.IsValid.ShouldBeFalse();
        declaration.Error.ShouldNotBeNull().ShouldContain("outside the plugin directory");
    }

    [Fact]
    public void WrapperKey_WinsOverAReDeserialisationOfTheSameDocument()
    {
        Write(".botnexus-plugin/mcp.json", """{ "mcpServers": { "a": { "command": "node" } } }""");

        var declaration = PluginMcpDeclarationReader.Read(_root, declaredPath: null);

        // "mcpServers" must not itself be interpreted as a server name by the bare-map fallback.
        declaration.Servers.Keys.ShouldBe(["a"]);
        declaration.Servers.ShouldNotContainKey("mcpServers");
    }
}
