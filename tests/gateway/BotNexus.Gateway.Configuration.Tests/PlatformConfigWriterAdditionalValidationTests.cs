using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Configuration.Tests;

public sealed class PlatformConfigWriterAdditionalValidationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "botnexus-writer-validation", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task UpdateSectionAsync_AdditionalValidationRejectsCandidateWithoutWriting()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "config.json");
        const string original = """{ "world": { "extensions": { "legacy": { "enabled": true } } } }""";
        await File.WriteAllTextAsync(path, original);
        var writer = new PlatformConfigWriter(path, new FileSystem());
        JsonObject? seenBefore = null;
        JsonObject? seenCandidate = null;

        Func<JsonObject, JsonObject, IReadOnlyList<string>> validation = (before, candidate) =>
        {
            seenBefore = before.DeepClone().AsObject();
            seenCandidate = candidate.DeepClone().AsObject();
            return ["rejected candidate"];
        };

        var ex = await Should.ThrowAsync<PlatformConfigSectionGuardException>(() =>
            writer.UpdateSectionAsync("world", JsonNode.Parse("""{ "extensions": { "legacy": { "enabled": false } } }""")!,
                additionalValidation: validation));

        ex.Message.ShouldContain("rejected candidate");
        seenBefore!["world"]!["extensions"]!["legacy"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();
        seenCandidate!["world"]!["extensions"]!["legacy"]!["enabled"]!.GetValue<bool>().ShouldBeFalse();
        (await File.ReadAllTextAsync(path)).ShouldBe(original);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
