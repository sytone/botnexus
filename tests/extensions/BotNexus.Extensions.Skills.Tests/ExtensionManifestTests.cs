using System.Text.Json;

namespace BotNexus.Extensions.Skills.Tests;

public sealed class ExtensionManifestTests
{
    [Fact]
    public void SkillsExtensionManifest_DeclaresHookHandlerType()
    {
        var manifestPath = Path.Combine(FindRepositoryRoot(), "src", "extensions", "BotNexus.Extensions.Skills", "botnexus-extension.json");
        File.Exists(manifestPath).ShouldBeTrue();

        using var json = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var extensionTypes = json.RootElement.GetProperty("extensionTypes")
            .EnumerateArray()
            .Select(value => value.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();

        extensionTypes.ShouldContain("hook-handler");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root could not be resolved from test base path.");
    }
}
