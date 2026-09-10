using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Pins the contributed left-nav endpoint.
/// </summary>
/// <remarks>
/// Every entry here originates in a third-party extension manifest and is rendered by the portal
/// into an anchor, so the load-bearing tests are the rejections. A path that is not site-relative
/// must never reach the portal, and one bad entry must not take the rest of the sidebar with it.
/// </remarks>
public sealed class NavContributionsControllerTests
{
    private static NavContributionsController ControllerOver(params LoadedExtension[] loaded)
    {
        var loader = Substitute.For<IExtensionLoader>();
        loader.GetLoaded().Returns(loaded);
        return new NavContributionsController(loader);
    }

    private static LoadedExtension Extension(string id, params ExtensionNavEntry[] nav) => new()
    {
        ExtensionId = id,
        Name = id,
        Version = "1.0.0",
        DirectoryPath = "/tmp/" + id,
        EntryAssemblyPath = "/tmp/" + id + "/x.dll",
        LoadedAtUtc = DateTimeOffset.UnixEpoch,
        Nav = nav,
    };

    private static ExtensionNavEntry Entry(
        string id = "agent-builder",
        string label = "Agent Builder",
        string path = "/agent-builder",
        string? icon = "tools",
        int order = 65,
        bool external = true) =>
        new() { Id = id, Label = label, Path = path, Icon = icon, Order = order, External = external };

    private static IReadOnlyList<NavContributionResponse> Result(NavContributionsController controller) =>
        Assert.IsType<IReadOnlyList<NavContributionResponse>>(
            Assert.IsType<OkObjectResult>(controller.Contributions().Result).Value,
            exactMatch: false);

    [Fact]
    public void Returns_a_declared_entry()
    {
        var result = Result(ControllerOver(Extension("botnexus-agent-builder", Entry())));

        var entry = Assert.Single(result);
        Assert.Equal("agent-builder", entry.Id);
        Assert.Equal("Agent Builder", entry.Label);
        Assert.Equal("/agent-builder", entry.Path);
        Assert.Equal("tools", entry.Icon);
        Assert.Equal(65, entry.Order);
        Assert.True(entry.External);
        Assert.Equal("botnexus-agent-builder", entry.ExtensionId);
    }

    [Fact]
    public void Returns_nothing_when_no_extension_contributes_nav()
    {
        Assert.Empty(Result(ControllerOver(Extension("botnexus-plugins-api"))));
    }

    // The portal renders Path into an href. These are the shapes that must never get there.
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("//evil.example/phish")]
    [InlineData("https://evil.example/phish")]
    [InlineData("agent-builder")]
    [InlineData("")]
    [InlineData("   ")]
    public void Drops_an_entry_whose_path_is_not_site_relative(string path)
    {
        Assert.Empty(Result(ControllerOver(Extension("x", Entry(path: path)))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("../escape")]
    [InlineData("UPPER_SNAKE")]
    public void Drops_an_entry_whose_id_is_not_a_safe_key(string id)
    {
        Assert.Empty(Result(ControllerOver(Extension("x", Entry(id: id)))));
    }

    [Fact]
    public void Drops_an_entry_with_no_label()
    {
        Assert.Empty(Result(ControllerOver(Extension("x", Entry(label: "  ")))));
    }

    // An icon is a NAME, never markup. An unusable one is nulled so the portal picks a default,
    // rather than dropping an otherwise valid entry.
    [Fact]
    public void Clears_an_unusable_icon_but_keeps_the_entry()
    {
        var result = Result(ControllerOver(Extension("x", Entry(icon: "<svg onload=alert(1)>"))));

        var entry = Assert.Single(result);
        Assert.Null(entry.Icon);
    }

    [Fact]
    public void Truncates_a_label_that_would_break_the_sidebar()
    {
        var result = Result(ControllerOver(Extension("x", Entry(label: new string('a', 200)))));

        Assert.Equal(40, Assert.Single(result).Label.Length);
    }

    // One malformed entry must not cost the others.
    [Fact]
    public void A_bad_entry_does_not_suppress_a_good_one()
    {
        var result = Result(ControllerOver(Extension(
            "x",
            Entry(id: "bad", path: "javascript:alert(1)"),
            Entry(id: "good", path: "/good"))));

        Assert.Equal("good", Assert.Single(result).Id);
    }

    [Fact]
    public void Orders_by_declared_order_then_id()
    {
        var result = Result(ControllerOver(
            Extension("a", Entry(id: "later", path: "/later", order: 90)),
            Extension("b", Entry(id: "earlier", path: "/earlier", order: 15))));

        Assert.Equal(["earlier", "later"], result.Select(e => e.Id));
    }

    // Two extensions claiming one nav key is a conflict the portal cannot resolve; rendering both
    // would duplicate the row and navigate to whichever won a race.
    [Fact]
    public void First_declaration_of_a_duplicated_id_wins()
    {
        var result = Result(ControllerOver(
            Extension("first", Entry(id: "dup", path: "/first")),
            Extension("second", Entry(id: "dup", path: "/second"))));

        var entry = Assert.Single(result);
        Assert.Equal("/first", entry.Path);
        Assert.Equal("first", entry.ExtensionId);
    }
}
