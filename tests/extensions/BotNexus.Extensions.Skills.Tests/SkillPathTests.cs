using System.IO.Abstractions.TestingHelpers;
using System.Reflection;
using BotNexus.Extensions.Skills.Security;

namespace BotNexus.Skills.Tests;

/// <summary>
/// Tests for <see cref="SkillPath"/> (#2927) — the strong type that makes "this path was proven to
/// be inside the skills root" a compile-time fact rather than a convention.
/// </summary>
public sealed class SkillPathTests
{
    private const string SkillRoot = "/workspace/skills/my-skill";

    // --- AC1: cannot be constructed without validation; no implicit conversion from string ---

    [Fact]
    public void Type_ExposesNoPublicConstructorAndNoConversionFromString()
    {
        var type = typeof(SkillPath);

        type.GetConstructors()
            .Where(c => c.GetParameters().Length > 0)
            .ShouldBeEmpty();

        type.GetMethods()
            .Where(m => m.Name is "op_Implicit" or "op_Explicit")
            .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(string)))
            .ShouldBeEmpty();
    }

    [Fact]
    public void FromResolved_IsNotPubliclyReachable()
    {
        // The single privileged constructor is reserved for SkillPathValidator. If it were public,
        // any caller could assert containment without proving it — the exact hole this type closes.
        var method = typeof(SkillPath).GetMethod(
            nameof(SkillPath.FromResolved),
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        method.ShouldNotBeNull();
        method.IsPublic.ShouldBeFalse();
        method.IsAssembly.ShouldBeTrue();
    }

    [Fact]
    public void Default_HasNoValue_AndValueThrows()
    {
        var uninitialised = default(SkillPath);

        uninitialised.HasValue.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => uninitialised.Value);
    }

    [Fact]
    public void CreateRoot_NormalisesAConfiguredRoot()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(SkillRoot);

        var root = SkillPath.CreateRoot(SkillRoot, fs);

        root.HasValue.ShouldBeTrue();
        root.Value.ShouldContain("my-skill");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryCreateRoot_RejectsEmptyInput(string? value)
    {
        SkillPath.TryCreateRoot(value, new MockFileSystem(), out var root).ShouldBeFalse();
        root.HasValue.ShouldBeFalse();
    }

    [Fact]
    public void CreateRoot_ThrowsForEmptyInput()
        => Should.Throw<ArgumentException>(() => SkillPath.CreateRoot("  ", new MockFileSystem()));

    // --- AC4: an escaping path cannot produce a SkillPath instance ---

    [Fact]
    public void EscapingPath_CannotProduceASkillPathInstance()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(SkillRoot);
        fs.AddDirectory("/etc/secrets");

        var root = SkillPath.CreateRoot(SkillRoot, fs);

        // Plain traversal out of the root.
        SkillPathValidator.TryValidate(
            "/workspace/skills/other-skill/evil.ps1", root, fs, out var traversed, out _).ShouldBeFalse();
        traversed.HasValue.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => traversed.Value);

        // Relative traversal that normalises out of the root.
        SkillPathValidator.TryValidate(
            $"{SkillRoot}/../../../etc/secrets/passwords.txt", root, fs, out var dotDot, out _).ShouldBeFalse();
        dotDot.HasValue.ShouldBeFalse();
    }

    [Fact]
    public void SymlinkEscapingTheRoot_CannotProduceASkillPathInstance()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(SkillRoot);
        fs.AddDirectory("/etc/secrets");
        fs.Directory.CreateSymbolicLink($"{SkillRoot}/scripts", "/etc/secrets");

        var root = SkillPath.CreateRoot(SkillRoot, fs);

        SkillPathValidator.TryValidate(
            $"{SkillRoot}/scripts/passwords.txt", root, fs, out var escaped, out var error).ShouldBeFalse();

        escaped.HasValue.ShouldBeFalse();
        error.ShouldNotBeNull();
        Should.Throw<InvalidOperationException>(() => escaped.Value);
    }

    [Fact]
    public void ContainedPath_DoesProduceASkillPathInstance()
    {
        // Non-vacuity guard for the tests above: the validator is not simply refusing everything.
        var fs = new MockFileSystem();
        fs.AddDirectory($"{SkillRoot}/scripts");

        var root = SkillPath.CreateRoot(SkillRoot, fs);

        SkillPathValidator.TryValidate(
            $"{SkillRoot}/scripts/run.ps1", root, fs, out var contained, out var error).ShouldBeTrue();

        contained.HasValue.ShouldBeTrue();
        contained.Value.ShouldContain("run.ps1");
        error.ShouldBeNull();
    }

    [Fact]
    public void ToString_RendersThePath_BecauseAPathIsNotASecret()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(SkillRoot);

        SkillPath.CreateRoot(SkillRoot, fs).ToString().ShouldContain("my-skill");
        default(SkillPath).ToString().ShouldBe("SkillPath(none)");
    }
}
