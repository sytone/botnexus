using System.IO.Abstractions.TestingHelpers;
using BotNexus.Gateway.Prompts;

namespace BotNexus.Gateway.Prompts.Tests;

public sealed class FilePromptOverrideResolverTests
{
    private const string PromptsDir = "/home/user/.botnexus/prompts";

    [Fact]
    public void TryResolveOverride_SectionFileExists_ReturnsLines()
    {
        var fs = new MockFileSystem();
        fs.AddFile($"{PromptsDir}/system/shell-efficiency.md", new MockFileData("Use scripts over inline commands\nMinimize shell invocations"));
        var resolver = new FilePromptOverrideResolver(PromptsDir, fs);

        var result = resolver.TryResolveOverride("shell-efficiency");

        result.ShouldNotBeNull();
        result.Count.ShouldBe(2);
        result[0].ShouldBe("Use scripts over inline commands");
        result[1].ShouldBe("Minimize shell invocations");
    }

    [Fact]
    public void TryResolveOverride_SectionFileDoesNotExist_ReturnsNull()
    {
        var fs = new MockFileSystem();
        var resolver = new FilePromptOverrideResolver(PromptsDir, fs);

        var result = resolver.TryResolveOverride("nonexistent-section");

        result.ShouldBeNull();
    }

    [Fact]
    public void TryResolveOverride_EmptyFile_ReturnsNull()
    {
        var fs = new MockFileSystem();
        fs.AddFile($"{PromptsDir}/system/shell-efficiency.md", new MockFileData("   "));
        var resolver = new FilePromptOverrideResolver(PromptsDir, fs);

        var result = resolver.TryResolveOverride("shell-efficiency");

        result.ShouldBeNull();
    }

    [Fact]
    public void TryResolveOverride_NonOverridableSection_ReturnsNull()
    {
        var fs = new MockFileSystem();
        fs.AddFile($"{PromptsDir}/system/safety.md", new MockFileData("Overridden safety content"));
        var resolver = new FilePromptOverrideResolver(PromptsDir, fs);

        var result = resolver.TryResolveOverride("safety");

        result.ShouldBeNull();
    }

    [Fact]
    public void TryResolveOverride_RuntimeDataSection_ReturnsNull()
    {
        var fs = new MockFileSystem();
        fs.AddFile($"{PromptsDir}/system/runtime-data.md", new MockFileData("Overridden runtime data"));
        var resolver = new FilePromptOverrideResolver(PromptsDir, fs);

        var result = resolver.TryResolveOverride("runtime-data");

        result.ShouldBeNull();
    }

    [Fact]
    public void TryResolveOverride_IdentitySection_ReturnsNull()
    {
        var fs = new MockFileSystem();
        fs.AddFile($"{PromptsDir}/system/identity.md", new MockFileData("Overridden identity"));
        var resolver = new FilePromptOverrideResolver(PromptsDir, fs);

        var result = resolver.TryResolveOverride("identity");

        result.ShouldBeNull();
    }

    [Fact]
    public void TryResolveOverride_ModelGuidanceWithFamily_ChecksModelDir()
    {
        var fs = new MockFileSystem();
        fs.AddFile($"{PromptsDir}/system/model/claude.md", new MockFileData("Claude-specific guidance\nPrefer edit tool"));
        var resolver = new FilePromptOverrideResolver(PromptsDir, fs);

        var result = resolver.TryResolveOverride("model-guidance", modelFamily: "claude");

        result.ShouldNotBeNull();
        result.Count.ShouldBe(2);
        result[0].ShouldBe("Claude-specific guidance");
    }

    [Fact]
    public void TryResolveOverride_ModelGuidanceWithFamily_FallsBackToGeneral()
    {
        var fs = new MockFileSystem();
        // No model/gpt.md but general model-guidance.md exists
        fs.AddFile($"{PromptsDir}/system/model-guidance.md", new MockFileData("General model guidance"));
        var resolver = new FilePromptOverrideResolver(PromptsDir, fs);

        var result = resolver.TryResolveOverride("model-guidance", modelFamily: "gpt");

        result.ShouldNotBeNull();
        result[0].ShouldBe("General model guidance");
    }

    [Fact]
    public void TryResolveOverride_ModelGuidanceWithFamily_PrefersModelSpecific()
    {
        var fs = new MockFileSystem();
        fs.AddFile($"{PromptsDir}/system/model/claude.md", new MockFileData("Claude-specific"));
        fs.AddFile($"{PromptsDir}/system/model-guidance.md", new MockFileData("General fallback"));
        var resolver = new FilePromptOverrideResolver(PromptsDir, fs);

        var result = resolver.TryResolveOverride("model-guidance", modelFamily: "claude");

        result.ShouldNotBeNull();
        result[0].ShouldBe("Claude-specific");
    }

    [Fact]
    public void TryResolveOverride_ModelGuidanceNoFamily_ChecksGeneral()
    {
        var fs = new MockFileSystem();
        fs.AddFile($"{PromptsDir}/system/model-guidance.md", new MockFileData("General guidance"));
        var resolver = new FilePromptOverrideResolver(PromptsDir, fs);

        var result = resolver.TryResolveOverride("model-guidance");

        result.ShouldNotBeNull();
        result[0].ShouldBe("General guidance");
    }

    [Fact]
    public void TryResolveOverride_HotReload_ReadsNewContentOnNextCall()
    {
        var fs = new MockFileSystem();
        var resolver = new FilePromptOverrideResolver(PromptsDir, fs);

        // First call — no file
        resolver.TryResolveOverride("shell-efficiency").ShouldBeNull();

        // Create file after resolver instantiation
        fs.AddFile($"{PromptsDir}/system/shell-efficiency.md", new MockFileData("New content"));

        // Second call — should pick up new file without restart
        var result = resolver.TryResolveOverride("shell-efficiency");
        result.ShouldNotBeNull();
        result[0].ShouldBe("New content");
    }

    [Fact]
    public void Constructor_ThrowsOnNullOrWhitespaceDir()
    {
        Should.Throw<ArgumentException>(() => new FilePromptOverrideResolver(""));
        Should.Throw<ArgumentException>(() => new FilePromptOverrideResolver("   "));
    }

    [Fact]
    public void TryResolveOverride_ThrowsOnNullOrWhitespaceSectionId()
    {
        var fs = new MockFileSystem();
        var resolver = new FilePromptOverrideResolver(PromptsDir, fs);

        Should.Throw<ArgumentException>(() => resolver.TryResolveOverride(""));
        Should.Throw<ArgumentException>(() => resolver.TryResolveOverride("   "));
    }
}
