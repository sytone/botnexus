using BotNexus.Gateway.Contracts.Updates;

namespace BotNexus.Gateway.Tests.Updates;

public sealed class ReleaseTargetResolverTests
{
    [Fact]
    public void Resolve_Stable_UsesSemanticOrderingAndExcludesPrereleasesAndMalformedTags()
    {
        var tags = new[]
        {
            new ReleaseTagReference("v0.9.0", "commit-090"),
            new ReleaseTagReference("v0.10.0-rc.1", "commit-rc"),
            new ReleaseTagReference("not-a-release", "commit-malformed"),
            new ReleaseTagReference("v0.10.0", "commit-0100"),
        };

        var result = ReleaseTargetResolver.Resolve(ReleaseTargetRequest.Stable, tags);

        result.Kind.ShouldBe(ReleaseTargetKind.Stable);
        result.Version.ShouldBe("0.10.0");
        result.TagName.ShouldBe("v0.10.0");
        result.CommitSha.ShouldBe("commit-0100");
    }

    [Fact]
    public void Resolve_Exact_AllowsExplicitPrerelease()
    {
        var tags = new[]
        {
            new ReleaseTagReference("v1.0.0", "commit-stable"),
            new ReleaseTagReference("v1.1.0-beta.2", "commit-beta"),
        };

        var result = ReleaseTargetResolver.Resolve(
            ReleaseTargetRequest.Exact("1.1.0-beta.2"),
            tags);

        result.Kind.ShouldBe(ReleaseTargetKind.Exact);
        result.Version.ShouldBe("1.1.0-beta.2");
        result.TagName.ShouldBe("v1.1.0-beta.2");
        result.CommitSha.ShouldBe("commit-beta");
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.02.0")]
    [InlineData("v1.0")]
    public void Resolve_Exact_RejectsMalformedVersion(string version)
    {
        Action action = () => ReleaseTargetResolver.Resolve(
            ReleaseTargetRequest.Exact(version),
            Array.Empty<ReleaseTagReference>());

        Should.Throw<ReleaseTargetResolutionException>(action)
            .Message.ShouldContain("valid semantic version");
    }

    [Fact]
    public void Resolve_Exact_RejectsMissingTag()
    {
        Action action = () => ReleaseTargetResolver.Resolve(
            ReleaseTargetRequest.Exact("2.0.0"),
            [new ReleaseTagReference("v1.0.0", "commit-100")]);

        Should.Throw<ReleaseTargetResolutionException>(action)
            .Message.ShouldContain("v2.0.0");
    }

    [Fact]
    public void Resolve_Stable_RejectsCatalogWithoutResolvableStableRelease()
    {
        Action action = () => ReleaseTargetResolver.Resolve(
            ReleaseTargetRequest.Stable,
            [
                new ReleaseTagReference("v2.0.0-beta.1", "commit-beta"),
                new ReleaseTagReference("v1.0.0", ""),
                new ReleaseTagReference("release-3", "commit-malformed"),
            ]);

        Should.Throw<ReleaseTargetResolutionException>(action)
            .Message.ShouldContain("stable release");
    }

    [Fact]
    public void Resolve_Latest_UsesConfiguredDevelopmentTip()
    {
        var tip = new DevelopmentTipReference("main", "commit-main");

        var result = ReleaseTargetResolver.Resolve(
            ReleaseTargetRequest.Latest,
            Array.Empty<ReleaseTagReference>(),
            tip);

        result.Kind.ShouldBe(ReleaseTargetKind.Latest);
        result.Version.ShouldBeNull();
        result.TagName.ShouldBeNull();
        result.SourceName.ShouldBe("main");
        result.CommitSha.ShouldBe("commit-main");
    }

    [Fact]
    public void ResolvedTarget_RetainsImmutableCommitIdentityWhenInputCatalogChanges()
    {
        var tags = new List<ReleaseTagReference>
        {
            new("v1.0.0", "commit-original"),
        };
        var result = ReleaseTargetResolver.Resolve(ReleaseTargetRequest.Stable, tags);

        tags[0] = new ReleaseTagReference("v1.0.0", "commit-moved");

        result.CommitSha.ShouldBe("commit-original");
    }

    [Fact]
    public void ReleaseUpdateStatus_RepresentsInstalledAndTargetVersionAndCommit()
    {
        var installed = new ReleaseIdentity("0.9.0", "commit-installed");
        var target = ReleaseTargetResolver.Resolve(
            ReleaseTargetRequest.Stable,
            [new ReleaseTagReference("v0.10.0", "commit-target")]);

        var status = new ReleaseUpdateStatus(installed, target);

        status.Installed.Version.ShouldBe("0.9.0");
        status.Installed.CommitSha.ShouldBe("commit-installed");
        status.Target.Version.ShouldBe("0.10.0");
        status.Target.CommitSha.ShouldBe("commit-target");
    }
}
