using BotNexus.Extensions.Skills.Security;
using System.IO.Abstractions.TestingHelpers;
using System.Text;

namespace BotNexus.Extensions.Skills.Tests;

public sealed class SkillTrustVerifierTests
{
    private const string SkillDir = "/skills/test-skill";

    [Fact]
    public void Verify_NoCatalog_ReturnsUntrusted()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(SkillDir);

        var result = SkillTrustVerifier.Verify(SkillDir, fs);

        Assert.False(result.Trusted);
        Assert.Contains("No trust catalog found", result.Violations);
    }

    [Fact]
    public void Verify_ValidCatalog_AllHashesMatch_ReturnsTrusted()
    {
        var fs = new MockFileSystem();
        var scriptContent = "Write-Output 'hello'"u8.ToArray();
        var hash = SkillTrustVerifier.ComputeSha256(scriptContent);

        fs.AddFile($"{SkillDir}/scripts/run.ps1", new MockFileData(scriptContent));
        fs.AddFile($"{SkillDir}/trust.json", new MockFileData($$"""
        {
            "version": 1,
            "generatedAt": "2026-01-01T00:00:00Z",
            "entries": [
                { "path": "scripts/run.ps1", "sha256": "{{hash}}", "updatedAt": "2026-01-01T00:00:00Z" }
            ]
        }
        """));

        var result = SkillTrustVerifier.Verify(SkillDir, fs);

        Assert.True(result.Trusted);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void Verify_HashMismatch_ReturnsUntrustedWithViolation()
    {
        var fs = new MockFileSystem();
        fs.AddFile($"{SkillDir}/scripts/run.ps1", new MockFileData("modified content"));
        fs.AddFile($"{SkillDir}/trust.json", new MockFileData("""
        {
            "version": 1,
            "generatedAt": "2026-01-01T00:00:00Z",
            "entries": [
                { "path": "scripts/run.ps1", "sha256": "0000000000000000000000000000000000000000000000000000000000000000", "updatedAt": "2026-01-01T00:00:00Z" }
            ]
        }
        """));

        var result = SkillTrustVerifier.Verify(SkillDir, fs);

        Assert.False(result.Trusted);
        Assert.Single(result.Violations);
        Assert.Contains("Hash mismatch", result.Violations[0]);
    }

    [Fact]
    public void Verify_MissingFile_ReturnsUntrustedWithViolation()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(SkillDir);
        fs.AddFile($"{SkillDir}/trust.json", new MockFileData("""
        {
            "version": 1,
            "generatedAt": "2026-01-01T00:00:00Z",
            "entries": [
                { "path": "scripts/missing.ps1", "sha256": "abc123", "updatedAt": "2026-01-01T00:00:00Z" }
            ]
        }
        """));

        var result = SkillTrustVerifier.Verify(SkillDir, fs);

        Assert.False(result.Trusted);
        Assert.Contains("Missing file", result.Violations[0]);
    }

    [Fact]
    public void Verify_InvalidCatalogJson_ReturnsUntrusted()
    {
        var fs = new MockFileSystem();
        fs.AddFile($"{SkillDir}/trust.json", new MockFileData("not valid json {{{"));

        var result = SkillTrustVerifier.Verify(SkillDir, fs);

        Assert.False(result.Trusted);
        Assert.Contains("Failed to parse", result.Violations[0]);
    }

    [Fact]
    public void GenerateCatalog_CreatesEntriesForScannableFiles()
    {
        var fs = new MockFileSystem();
        fs.AddFile($"{SkillDir}/scripts/run.ps1", new MockFileData("param() Write-Output 1"));
        fs.AddFile($"{SkillDir}/scripts/helper.py", new MockFileData("print('hi')"));
        fs.AddFile($"{SkillDir}/SKILL.md", new MockFileData("# Skill")); // .md not scannable

        var catalog = SkillTrustVerifier.GenerateCatalog(SkillDir, fs);

        Assert.Equal(2, catalog.Entries.Count);
        Assert.Contains(catalog.Entries, e => e.Path == "scripts/run.ps1");
        Assert.Contains(catalog.Entries, e => e.Path == "scripts/helper.py");
        Assert.All(catalog.Entries, e => Assert.Equal(64, e.Sha256.Length)); // SHA-256 hex length
    }

    [Fact]
    public void GenerateCatalog_EmptyDir_ReturnsEmptyCatalog()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(SkillDir);

        var catalog = SkillTrustVerifier.GenerateCatalog(SkillDir, fs);

        Assert.Empty(catalog.Entries);
    }

    [Fact]
    public void WriteCatalog_CreatesJsonFile()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(SkillDir);

        var catalog = new TrustCatalog
        {
            GeneratedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            Entries = [new TrustCatalogEntry { Path = "scripts/run.ps1", Sha256 = "abc", UpdatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z") }],
        };

        SkillTrustVerifier.WriteCatalog(SkillDir, catalog, fs);

        Assert.True(fs.File.Exists($"{SkillDir}/trust.json"));
        var content = fs.File.ReadAllText($"{SkillDir}/trust.json");
        Assert.Contains("abc", content);
    }

    [Fact]
    public void ComputeSha256_ProducesCorrectHash()
    {
        // Known SHA-256 for empty byte array
        var hash = SkillTrustVerifier.ComputeSha256([]);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", hash);
    }

    [Fact]
    public void Verify_EmptyCatalogEntries_ReturnsUntrusted()
    {
        var fs = new MockFileSystem();
        fs.AddFile($"{SkillDir}/trust.json", new MockFileData("""
        {
            "version": 1,
            "generatedAt": "2026-01-01T00:00:00Z",
            "entries": []
        }
        """));

        var result = SkillTrustVerifier.Verify(SkillDir, fs);

        Assert.False(result.Trusted);
        Assert.Contains("empty or invalid", result.Violations[0]);
    }
}
