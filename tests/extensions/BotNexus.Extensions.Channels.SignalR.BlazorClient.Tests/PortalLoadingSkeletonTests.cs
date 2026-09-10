using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// The portal's cold start. Chrome paints in under a second; the body then waited on the hub with
/// nothing in it — measured on a live instance as still empty 11.3 seconds after a reload, with
/// every framework asset cached and every REST call answered inside 500ms.
///
/// <para>
/// These are structure tests, not layout tests: bUnit cannot see rendering, so they pin the
/// contracts that survive it — that a skeleton appears instead of a void, that a real error still
/// wins over it, and that assistive technology is told one useful thing rather than handed a dozen
/// empty boxes.
/// </para>
/// </summary>
public sealed class PortalLoadingSkeletonTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void It_draws_a_skeleton_while_loading()
    {
        var cut = _ctx.Render<PortalLoadingSkeleton>();

        cut.Find("[data-testid='portal-skeleton']").ShouldNotBeNull();
        cut.FindAll(".sk-block").Count.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData(PortalLoadingSkeleton.SkeletonVariant.Home, "Home")]
    [InlineData(PortalLoadingSkeleton.SkeletonVariant.Chat, "Chat")]
    [InlineData(PortalLoadingSkeleton.SkeletonVariant.Activity, "Activity")]
    public void Each_page_gets_its_own_shape(PortalLoadingSkeleton.SkeletonVariant variant, string expected)
    {
        // The point of a skeleton is that it approximates the page that is coming. One shape for
        // every page would be a spinner with extra steps.
        var cut = _ctx.Render<PortalLoadingSkeleton>(p => p.Add(x => x.Variant, variant));

        cut.Find("[data-testid='portal-skeleton']").GetAttribute("data-variant").ShouldBe(expected);
    }

    [Fact]
    public void The_chat_shape_alternates_sides_so_it_reads_as_a_conversation()
    {
        var cut = _ctx.Render<PortalLoadingSkeleton>(p =>
            p.Add(x => x.Variant, PortalLoadingSkeleton.SkeletonVariant.Chat));

        cut.FindAll(".portal-skeleton-message.is-own").Count.ShouldBeGreaterThan(0);
        cut.FindAll(".portal-skeleton-composer").Count.ShouldBe(1);
    }

    [Fact]
    public void A_load_error_replaces_the_skeleton_entirely()
    {
        // A skeleton promises content that is arriving. A failed load has none, so showing both
        // would say two contradictory things at once.
        var cut = _ctx.Render<PortalLoadingSkeleton>(p => p.Add(x => x.Error, "Gateway unreachable"));

        cut.FindAll("[data-testid='portal-skeleton']").ShouldBeEmpty();
        cut.Find("[data-testid='portal-load-error']").TextContent.ShouldContain("Gateway unreachable");
    }

    [Fact]
    public void The_shapes_are_hidden_from_assistive_technology()
    {
        // A screen reader handed a dozen empty divs learns less than one told "Loading".
        var cut = _ctx.Render<PortalLoadingSkeleton>();

        foreach (var block in cut.FindAll(".portal-skeleton > div"))
            block.GetAttribute("aria-hidden").ShouldBe("true");
    }

    [Fact]
    public void One_live_region_carries_the_status()
    {
        var cut = _ctx.Render<PortalLoadingSkeleton>(p =>
            p.Add(x => x.Variant, PortalLoadingSkeleton.SkeletonVariant.Activity));

        var status = cut.FindAll("[role='status']").ShouldHaveSingleItem();
        status.GetAttribute("aria-live").ShouldBe("polite");
        status.ClassList.ShouldContain("sr-only");
        status.TextContent.ShouldContain("activity");
    }
}
