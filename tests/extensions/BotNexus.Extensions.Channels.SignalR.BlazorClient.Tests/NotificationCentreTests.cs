using System.Net;
using AngleSharp.Dom;
using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Tests the notification centre in the banner.
/// </summary>
/// <remarks>
/// The load-bearing test is the FIRST one. This component sits in the banner, which renders on
/// every page, so a version that threw on a missing service took the whole layout down with it -
/// 163 tests failed against a baseline of 16 on the first draft, and deploying it would have left
/// the portal rendering nothing at all rather than merely lacking a bell. Everything else here is
/// ordinary behaviour; that one is the regression guard.
/// </remarks>
public sealed class NotificationCentreTests : IDisposable
{
    private readonly BunitContext _ctx = new();
    private readonly StubHandler _handler = new();

    public NotificationCentreTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddSingleton(new GatewayHubConnection());

        // Registered exactly as production does. Left unplanned, the JS calls return null under
        // Loose interop, which is what an absent script looks like - so the default here is a
        // browser that cannot show desktop notifications at all.
        _ctx.Services.AddSingleton(sp => new DesktopNotifier(sp.GetRequiredService<IJSRuntime>()));
    }

    public void Dispose() => _ctx.Dispose();

    /// <summary>Registers the API client. Omitted deliberately by the degradation test.</summary>
    private void WithApi() =>
        _ctx.Services.AddSingleton(new NotificationsApiClient(
            new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") }));

    private IRenderedComponent<NotificationCentre> Render() => _ctx.Render<NotificationCentre>();

    private static string Item(string id, string title, bool unread = true, string? link = null) =>
        "{" + $"\"id\":\"{id}\",\"kind\":\"AgentRunFailed\",\"severity\":\"Error\","
        + $"\"title\":\"{title}\",\"createdAtUtc\":\"2026-08-28T10:00:00Z\","
        + $"\"readAtUtc\":{(unread ? "null" : "\"2026-08-28T10:05:00Z\"")}"
        + (link is null ? "" : $",\"link\":\"{link}\"") + "}";

    // THE regression guard: the banner is on every page.
    [Fact]
    public void Renders_nothing_when_the_notifications_client_is_unavailable()
    {
        var cut = Render();

        Assert.Empty(cut.FindAll("[data-testid='notification-centre']"));
        Assert.Empty(cut.FindAll("[data-testid='notification-bell']"));
    }

    [Fact]
    public void Renders_the_bell_when_the_client_is_available()
    {
        WithApi();
        _handler.UnreadCount = 0;

        var cut = Render();

        Assert.Single(cut.FindAll("[data-testid='notification-bell']"));
    }

    // The badge is the whole point of the bell, and it must be right before the panel is ever
    // opened - it covers what happened while this browser was closed or on another device.
    [Fact]
    public void Shows_the_unread_count_without_opening_the_panel()
    {
        WithApi();
        _handler.UnreadCount = 3;

        var cut = Render();

        cut.WaitForState(() => cut.FindAll("[data-testid='notification-badge']").Count > 0);
        Assert.Equal("3", cut.Find("[data-testid='notification-badge']").TextContent.Trim());
    }

    // A badge that renders "0" would nag about nothing.
    [Fact]
    public void Shows_no_badge_when_nothing_is_unread()
    {
        WithApi();
        _handler.UnreadCount = 0;

        var cut = Render();

        Assert.Empty(cut.FindAll("[data-testid='notification-badge']"));
    }

    [Fact]
    public void Caps_a_large_badge_so_it_stays_readable()
    {
        WithApi();
        _handler.UnreadCount = 250;

        var cut = Render();

        cut.WaitForState(() => cut.FindAll("[data-testid='notification-badge']").Count > 0);
        Assert.Equal("99+", cut.Find("[data-testid='notification-badge']").TextContent.Trim());
    }

    [Fact]
    public void Opening_lists_the_notifications()
    {
        WithApi();
        _handler.ListJson = "[" + Item("a", "Agent run failed") + "," + Item("b", "Job failed") + "]";

        var cut = Render();
        cut.Find("[data-testid='notification-bell']").Click();

        cut.WaitForState(() => cut.FindAll("[data-testid='notification-item']").Count == 2);
        Assert.Contains("Agent run failed", cut.Markup);
    }

    // An empty feed is the ordinary state of a healthy gateway, so it must read as reassurance
    // rather than as an error.
    [Fact]
    public void Opening_an_empty_feed_explains_what_would_appear()
    {
        WithApi();
        _handler.ListJson = "[]";

        var cut = Render();
        cut.Find("[data-testid='notification-bell']").Click();

        cut.WaitForState(() => cut.FindAll("[data-testid='notification-empty']").Count > 0);
        Assert.Contains("Nothing to report", cut.Find("[data-testid='notification-empty']").TextContent);
    }

    [Fact]
    public void Unread_items_are_marked_as_such_for_styling()
    {
        WithApi();
        _handler.ListJson = "[" + Item("a", "Unread one") + "," + Item("b", "Read one", unread: false) + "]";

        var cut = Render();
        cut.Find("[data-testid='notification-bell']").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid='notification-item']").Count == 2);

        var items = cut.FindAll("[data-testid='notification-item']");
        Assert.Equal("true", items[0].GetAttribute("data-unread"));
        Assert.Equal("false", items[1].GetAttribute("data-unread"));
    }

    [Fact]
    public void Dismissing_removes_the_item_from_the_list()
    {
        WithApi();
        _handler.ListJson = "[" + Item("a", "Doomed") + "]";

        var cut = Render();
        cut.Find("[data-testid='notification-bell']").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid='notification-item']").Count == 1);

        cut.Find("[data-testid='notification-dismiss']").Click();

        cut.WaitForState(() => cut.FindAll("[data-testid='notification-item']").Count == 0);
        Assert.Contains("DELETE /api/notifications/a", _handler.Calls);
    }

    [Fact]
    public void Mark_all_read_is_offered_only_when_something_is_unread()
    {
        WithApi();
        _handler.UnreadCount = 0;
        _handler.ListJson = "[" + Item("a", "Read one", unread: false) + "]";

        var cut = Render();
        cut.Find("[data-testid='notification-bell']").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid='notification-item']").Count == 1);

        Assert.Empty(cut.FindAll("[data-testid='notification-mark-all']"));
    }

    [Fact]
    public void Mark_all_read_calls_the_gateway()
    {
        WithApi();
        _handler.UnreadCount = 2;
        _handler.ListJson = "[" + Item("a", "One") + "," + Item("b", "Two") + "]";

        var cut = Render();
        cut.Find("[data-testid='notification-bell']").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid='notification-mark-all']").Count > 0);

        cut.Find("[data-testid='notification-mark-all']").Click();

        cut.WaitForState(() => _handler.Calls.Contains("POST /api/notifications/read-all"));
    }

    // Opening an item IS the act of having seen it; a separate gesture is one nobody would make.
    [Fact]
    public void Opening_an_unread_item_marks_it_read()
    {
        WithApi();
        _handler.ListJson = "[" + Item("a", "Unread one") + "]";

        var cut = Render();
        cut.Find("[data-testid='notification-bell']").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid='notification-item']").Count == 1);

        cut.Find(".notification-item-main").Click();

        cut.WaitForState(() => _handler.Calls.Contains("POST /api/notifications/a/read"));
    }

    // Closing must not leave a stale panel behind.
    [Fact]
    public void The_panel_closes_again()
    {
        WithApi();
        _handler.ListJson = "[]";

        var cut = Render();
        cut.Find("[data-testid='notification-bell']").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid='notification-panel']").Count > 0);

        cut.Find("[data-testid='notification-close']").Click();

        Assert.Empty(cut.FindAll("[data-testid='notification-panel']"));
    }

    // The bug this guards: the panel was a child of the bell, and .banner-header clips its own
    // overflow, so the list was sliced off at the bottom edge of the top bar and appeared to sit
    // behind the page. It has to hang from the fixed overlay, outside the clipped bar, instead.
    [Fact]
    public void The_panel_hangs_outside_the_clipped_banner()
    {
        WithApi();
        _handler.ListJson = "[]";

        var cut = Render();
        cut.Find("[data-testid='notification-bell']").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid='notification-panel']").Count > 0);

        var bell = cut.Find("[data-testid='notification-centre']");
        Assert.Null(bell.QuerySelector("[data-testid='notification-panel']"));
        Assert.NotNull(cut.Find(".notification-overlay [data-testid='notification-panel']"));
    }

    // The overlay covers the viewport so a click anywhere dismisses the panel; the panel itself
    // must not close when clicked, or the list would vanish on the way to an item.
    [Fact]
    public void Clicking_away_closes_the_panel_but_clicking_it_does_not()
    {
        WithApi();
        _handler.ListJson = "[" + Item("a", "Agent run failed", unread: false) + "]";

        var cut = Render();
        cut.Find("[data-testid='notification-bell']").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid='notification-item']").Count == 1);

        // Reading an item is a click INSIDE the panel: it must not reach the overlay behind it.
        cut.Find("[data-testid='notification-item'] .notification-item-main").Click();
        Assert.Single(cut.FindAll("[data-testid='notification-panel']"));

        cut.Find(".notification-overlay").Click();
        Assert.Empty(cut.FindAll("[data-testid='notification-panel']"));
    }

    [Fact]
    public void Escape_closes_the_panel()
    {
        WithApi();
        _handler.ListJson = "[]";

        var cut = Render();
        cut.Find("[data-testid='notification-bell']").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid='notification-panel']").Count > 0);

        cut.Find(".notification-overlay").KeyDown(Key.Escape);

        Assert.Empty(cut.FindAll("[data-testid='notification-panel']"));
    }

    // The handler above is on the overlay, and a keydown handler only fires while its element
    // holds focus. Opening the panel leaves focus on the BELL, so without this the Escape handler
    // is unreachable and does nothing - which is exactly how it shipped the first time.
    [Fact]
    public void Opening_moves_focus_to_the_panel_so_escape_can_reach_it()
    {
        WithApi();
        _handler.ListJson = "[]";

        var cut = Render();
        var before = _ctx.JSInterop.Invocations.Count(i => i.Identifier.Contains("focus"));

        cut.Find("[data-testid='notification-bell']").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid='notification-panel']").Count > 0);

        cut.WaitForState(() =>
            _ctx.JSInterop.Invocations.Count(i => i.Identifier.Contains("focus")) > before);
    }

    // ── Desktop notifications ───────────────────────────────────────────────────────────────
    //
    // These stay honest about a browser API that is absent on some clients, permission-gated on
    // all of them, and a dead end once denied. The JS itself is not under test here; what is
    // under test is that the portal asks the browser the right question at the right moment and
    // never raises a toast the user did not opt into.

    /// <summary>Plans the status call, which is what the footer renders from.</summary>
    private void WithDesktop(
        bool supported,
        string permission,
        bool enabled,
        bool secure = true,
        string? browser = "chrome",
        string? origin = "https://gateway.example",
        bool pushSupported = false,
        bool pushSubscribed = false)
    {
        _ctx.JSInterop.Setup<DesktopNotificationStatus>("botnexusDesktopNotifications.statusWithPush")
            .SetResult(new DesktopNotificationStatus
            {
                Supported = supported,
                Permission = permission,
                Enabled = enabled,
                Secure = secure,
                Browser = browser,
                Origin = origin,
                PushSupported = pushSupported,
                PushSubscribed = pushSubscribed,
            });
    }

    private IElement OpenPanel(IRenderedComponent<NotificationCentre> cut)
    {
        cut.Find("[data-testid='notification-bell']").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid='notification-panel']").Count > 0);
        return cut.Find("[data-testid='notification-panel']");
    }

    // A browser with no Notification API gets no broken switch: the whole control is absent.
    [Fact]
    public void Desktop_alerts_are_not_offered_when_the_browser_cannot_show_them()
    {
        WithApi();
        _handler.ListJson = "[]";

        var cut = Render();
        OpenPanel(cut);

        Assert.Empty(cut.FindAll("[data-testid='desktop-alerts-toggle']"));
        Assert.Empty(cut.FindAll("[data-testid='desktop-alerts-blocked']"));
    }

    [Fact]
    public void Offers_to_enable_desktop_alerts_before_permission_is_asked_for()
    {
        WithApi();
        WithDesktop(supported: true, permission: "default", enabled: false);
        _handler.ListJson = "[]";

        var cut = Render();
        OpenPanel(cut);

        Assert.Equal(
            "Enable desktop alerts",
            cut.Find("[data-testid='desktop-alerts-toggle']").TextContent.Trim());
    }

    // A denial can never be re-prompted from script, so offering a button would be a lie. The
    // user has to be told where the switch actually is.
    [Fact]
    public void Says_so_when_the_browser_has_blocked_desktop_alerts()
    {
        WithApi();
        WithDesktop(supported: true, permission: "denied", enabled: false, secure: true);
        _handler.ListJson = "[]";

        var cut = Render();
        OpenPanel(cut);

        Assert.Empty(cut.FindAll("[data-testid='desktop-alerts-toggle']"));
        Assert.Contains("blocked", cut.Find("[data-testid='desktop-alerts-blocked']").TextContent);
    }

    [Fact]
    public void Reports_desktop_alerts_as_on_once_permitted_and_opted_in()
    {
        WithApi();
        WithDesktop(supported: true, permission: "granted", enabled: true);
        _handler.ListJson = "[]";

        var cut = Render();
        OpenPanel(cut);

        var toggle = cut.Find("[data-testid='desktop-alerts-toggle']");
        Assert.Equal("Desktop alerts on", toggle.TextContent.Trim());
        Assert.Equal("true", toggle.GetAttribute("aria-pressed"));
    }

    // The click IS the user gesture the browser requires; a prompt raised any other way is
    // ignored by Chrome and refused by Safari.
    [Fact]
    public void Enabling_prompts_the_browser_for_permission()
    {
        WithApi();
        WithDesktop(supported: true, permission: "default", enabled: false);
        var request = _ctx.JSInterop.Setup<DesktopNotificationStatus>("botnexusDesktopNotifications.request");
        request.SetResult(new DesktopNotificationStatus
        {
            Supported = true, Permission = "granted", Enabled = true, Secure = true,
        });
        _handler.ListJson = "[]";

        var cut = Render();
        OpenPanel(cut);
        cut.Find("[data-testid='desktop-alerts-toggle']").Click();

        Assert.Single(request.Invocations);
        cut.WaitForState(() =>
            cut.Find("[data-testid='desktop-alerts-toggle']").TextContent.Trim() == "Desktop alerts on");
    }

    // Turning them off is not a permission question, so it must not go anywhere near the prompt.
    [Fact]
    public void Turning_desktop_alerts_off_does_not_touch_the_permission()
    {
        WithApi();
        WithDesktop(supported: true, permission: "granted", enabled: true);
        var request = _ctx.JSInterop.Setup<DesktopNotificationStatus>("botnexusDesktopNotifications.request");
        var setEnabled = _ctx.JSInterop.Setup<DesktopNotificationStatus>("botnexusDesktopNotifications.setEnabled", _ => true);
        setEnabled.SetResult(new DesktopNotificationStatus
        {
            Supported = true, Permission = "granted", Enabled = false, Secure = true,
        });
        _handler.ListJson = "[]";

        var cut = Render();
        OpenPanel(cut);
        cut.Find("[data-testid='desktop-alerts-toggle']").Click();

        Assert.Empty(request.Invocations);
        Assert.Single(setEnabled.Invocations);
        Assert.Equal(false, setEnabled.Invocations.Single().Arguments[0]);
    }

    // The toast is raised from the PUSH, which is the one moment the portal knows something
    // happened now. Whether it is actually shown is the JS side's call - it suppresses one when
    // the portal is already in front of the user - but the portal has to hand it over.
    [Fact]
    public void A_push_hands_the_browser_a_toast_when_desktop_alerts_are_on()
    {
        WithApi();
        WithDesktop(supported: true, permission: "granted", enabled: true);
        var show = _ctx.JSInterop.Setup<string>("botnexusDesktopNotifications.show", _ => true);
        show.SetResult("shown");

        var cut = Render();
        cut.WaitForState(() => show is not null);

        _ctx.Services.GetRequiredService<GatewayHubConnection>().RaiseNotificationForTest(new NotificationRaisedPayload
        {
            Id = "n1",
            Title = "Agent run failed",
            Body = "assistant stopped",
            Link = "/conversations/c1",
        });

        cut.WaitForState(() => show.Invocations.Count == 1);

        var args = show.Invocations.Single().Arguments;
        Assert.Equal("n1", args[0]);
        Assert.Equal("Agent run failed", args[1]);
        Assert.Equal("assistant stopped", args[2]);
        Assert.Equal("/conversations/c1", args[3]);
    }

    // THE consent guard: a push must not reach the OS for someone who never opted in.
    [Fact]
    public void A_push_raises_no_toast_when_desktop_alerts_are_off()
    {
        WithApi();
        WithDesktop(supported: true, permission: "granted", enabled: false);
        var show = _ctx.JSInterop.Setup<string>("botnexusDesktopNotifications.show", _ => true);
        show.SetResult("inactive");

        var cut = Render();
        cut.WaitForState(() => cut.FindAll("[data-testid='notification-bell']").Count == 1);

        _ctx.Services.GetRequiredService<GatewayHubConnection>().RaiseNotificationForTest(new NotificationRaisedPayload
        {
            Id = "n1",
            Title = "Agent run failed",
        });

        cut.WaitForState(() => cut.Find("[data-testid='notification-badge']").TextContent.Trim() == "1");
        Assert.Empty(show.Invocations);
    }

    // ── Guidance ────────────────────────────────────────────────────────────────────────────

    // The case that prompted all of this. A browser denies notifications outright on a plain-http
    // origin that is not localhost, and NO browser setting overrides it - so reporting it as a
    // site setting sends the reader somewhere that cannot help. It has to be told apart.
    [Fact]
    public void An_insecure_origin_is_diagnosed_as_such_not_as_a_browser_setting()
    {
        WithApi();
        WithDesktop(
            supported: true, permission: "denied", enabled: false,
            secure: false, origin: "http://192.168.0.10:5005");
        _handler.ListJson = "[]";

        var cut = Render();
        OpenPanel(cut);

        var help = cut.Find("[data-testid='desktop-alerts-insecure']").TextContent;
        Assert.Contains("secure connection", help);
        Assert.Contains("http://192.168.0.10:5005", help);

        // The wrong diagnosis must NOT also be on screen.
        Assert.Empty(cut.FindAll("[data-testid='desktop-alerts-blocked']"));
        Assert.Empty(cut.FindAll("[data-testid='desktop-alerts-toggle']"));
    }

    // The textbook `ssh -L 5005:localhost:5005` form forwards to the gateway's own loopback, and a
    // gateway that binds only its LAN address has nothing listening there - so the suggested
    // command has to target the host's own address. Confirmed against a live gateway: the
    // localhost form refused the connection, this one served the portal.
    [Fact]
    public void The_tunnel_command_targets_the_host_not_its_loopback()
    {
        WithApi();
        WithDesktop(
            supported: true, permission: "denied", enabled: false,
            secure: false, origin: "http://192.168.0.10:5005");
        _handler.ListJson = "[]";

        var cut = Render();
        OpenPanel(cut);

        var help = cut.Find("[data-testid='desktop-alerts-insecure']").ParentElement!.TextContent;
        Assert.Contains("ssh -L 5005:192.168.0.10:5005 192.168.0.10", help);
        Assert.Contains("http://localhost:5005", help);
        Assert.DoesNotContain("5005:localhost:5005", help);
    }

    [Theory]
    [InlineData("chrome", "Site settings")]
    [InlineData("edge", "Permissions for this site")]
    [InlineData("firefox", "padlock")]
    [InlineData("safari", "Safari")]
    public void Unblocking_steps_name_the_menu_the_reader_actually_has(string browser, string expected)
    {
        WithApi();
        WithDesktop(supported: true, permission: "denied", enabled: false, browser: browser);
        _handler.ListJson = "[]";

        var cut = Render();
        OpenPanel(cut);

        Assert.Contains(
            expected,
            cut.Find("[data-testid='desktop-alerts-unblock-steps']").TextContent);
    }

    // An unrecognised browser still gets something actionable rather than nothing.
    [Fact]
    public void An_unknown_browser_still_gets_generic_steps()
    {
        WithApi();
        WithDesktop(supported: true, permission: "denied", enabled: false, browser: "other");
        _handler.ListJson = "[]";

        var cut = Render();
        OpenPanel(cut);

        Assert.Contains(
            "site settings",
            cut.Find("[data-testid='desktop-alerts-unblock-steps']").TextContent);
    }

    // THE thing the user asked for: help is for people who need it. Someone who already has
    // desktop alerts working should not be told how to turn them on.
    [Fact]
    public void No_guidance_is_shown_once_desktop_alerts_are_working()
    {
        WithApi();
        WithDesktop(supported: true, permission: "granted", enabled: true);
        _handler.ListJson = "[]";

        var cut = Render();
        OpenPanel(cut);

        Assert.Empty(cut.FindAll("[data-testid='desktop-alerts-insecure']"));
        Assert.Empty(cut.FindAll("[data-testid='desktop-alerts-blocked']"));
        Assert.Empty(cut.FindAll("[data-testid='desktop-alerts-pitch']"));
        Assert.Equal("Desktop alerts on", cut.Find("[data-testid='desktop-alerts-toggle']").TextContent.Trim());
    }

    // ── Web push ────────────────────────────────────────────────────────────────────────────

    // The distinction that decides whether someone can close the tab. Two transports, one toggle,
    // so the panel has to say which one is actually running.
    [Fact]
    public void Says_when_alerts_only_work_while_the_portal_is_open()
    {
        WithApi();
        WithDesktop(
            supported: true, permission: "granted", enabled: true,
            pushSupported: false, pushSubscribed: false);
        _handler.ListJson = "[]";

        var cut = Render();
        OpenPanel(cut);

        Assert.Equal(
            "only while the portal is open",
            cut.Find("[data-testid='desktop-alerts-transport']").TextContent.Trim());
    }

    [Fact]
    public void Says_when_alerts_survive_the_portal_being_closed()
    {
        WithApi();
        WithDesktop(
            supported: true, permission: "granted", enabled: true,
            pushSupported: true, pushSubscribed: true);
        _handler.ListJson = "[]";

        var cut = Render();
        OpenPanel(cut);

        Assert.Equal(
            "including when the portal is closed",
            cut.Find("[data-testid='desktop-alerts-transport']").TextContent.Trim());
    }

    // Push is strictly better than the in-page alert, so it is taken whenever the browser allows
    // it rather than hidden behind a second switch the reader would have to find.
    [Fact]
    public void Enabling_subscribes_to_push_when_the_browser_supports_it()
    {
        WithApi();
        WithDesktop(
            supported: true, permission: "default", enabled: false, pushSupported: true);
        var request = _ctx.JSInterop.Setup<DesktopNotificationStatus>("botnexusDesktopNotifications.request");
        request.SetResult(new DesktopNotificationStatus
        {
            Supported = true, Permission = "granted", Enabled = true, Secure = true, PushSupported = true,
        });
        var enablePush = _ctx.JSInterop.Setup<bool>("botnexusDesktopNotifications.enablePush");
        enablePush.SetResult(true);
        _handler.ListJson = "[]";

        var cut = Render();
        OpenPanel(cut);
        cut.Find("[data-testid='desktop-alerts-toggle']").Click();

        cut.WaitForState(() => enablePush.Invocations.Count == 1);
    }

    // A browser without a push manager must not be asked to subscribe - the call would fail and
    // the in-page alert it CAN do would look like it had failed too.
    [Fact]
    public void Enabling_does_not_reach_for_push_when_the_browser_lacks_it()
    {
        WithApi();
        WithDesktop(
            supported: true, permission: "default", enabled: false, pushSupported: false);
        var request = _ctx.JSInterop.Setup<DesktopNotificationStatus>("botnexusDesktopNotifications.request");
        request.SetResult(new DesktopNotificationStatus
        {
            Supported = true, Permission = "granted", Enabled = true, Secure = true, PushSupported = false,
        });
        var enablePush = _ctx.JSInterop.Setup<bool>("botnexusDesktopNotifications.enablePush");
        enablePush.SetResult(true);
        _handler.ListJson = "[]";

        var cut = Render();
        OpenPanel(cut);
        cut.Find("[data-testid='desktop-alerts-toggle']").Click();

        cut.WaitForState(() => request.Invocations.Count == 1);
        Assert.Empty(enablePush.Invocations);
    }

    // Turning alerts off has to drop the subscription too, or the gateway would keep pushing to a
    // device whose owner just said they did not want to hear from it.
    [Fact]
    public void Turning_alerts_off_also_unsubscribes_from_push()
    {
        WithApi();
        WithDesktop(
            supported: true, permission: "granted", enabled: true,
            pushSupported: true, pushSubscribed: true);
        var disablePush = _ctx.JSInterop.Setup<bool>("botnexusDesktopNotifications.disablePush");
        disablePush.SetResult(true);
        var setEnabled = _ctx.JSInterop.Setup<DesktopNotificationStatus>(
            "botnexusDesktopNotifications.setEnabled", _ => true);
        setEnabled.SetResult(new DesktopNotificationStatus
        {
            Supported = true, Permission = "granted", Enabled = false, Secure = true,
        });
        _handler.ListJson = "[]";

        var cut = Render();
        OpenPanel(cut);
        cut.Find("[data-testid='desktop-alerts-toggle']").Click();

        cut.WaitForState(() => disablePush.Invocations.Count == 1);
    }

    // ── Test notification ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Sending_a_test_asks_the_gateway_to_raise_a_real_notification()
    {
        WithApi();
        _handler.ListJson = "[]";

        var cut = Render();
        OpenPanel(cut);
        cut.Find("[data-testid='notification-send-test']").Click();

        cut.WaitForState(() => _handler.Calls.Contains("POST /api/notifications/test"));
    }

    // Offered even where a toast is impossible, because it still proves the store, the push and
    // the badge - the parts a browser cannot break.
    [Fact]
    public void The_test_is_offered_even_when_desktop_alerts_cannot_work()
    {
        WithApi();
        WithDesktop(supported: true, permission: "denied", enabled: false, secure: false);
        _handler.ListJson = "[]";

        var cut = Render();
        OpenPanel(cut);

        Assert.Single(cut.FindAll("[data-testid='notification-send-test']"));
    }

    [Fact]
    public void A_refused_test_says_so_rather_than_failing_silently()
    {
        WithApi();
        _handler.ListJson = "[]";
        _handler.FailTest = true;

        var cut = Render();
        OpenPanel(cut);
        cut.Find("[data-testid='notification-send-test']").Click();

        cut.WaitForState(() => cut.FindAll("[data-testid='notification-test-error']").Count == 1);
    }

    /// <summary>Answers the notification endpoints and records what was called.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public string ListJson { get; set; } = "[]";

        public int UnreadCount { get; set; }

        /// <summary>Makes the test-notification endpoint refuse, so the failure path is reachable.</summary>
        public bool FailTest { get; set; }

        public List<string> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            Calls.Add($"{request.Method.Method} {path}");

            if (path.EndsWith("/unread-count", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("{\"count\":" + UnreadCount + "}"));
            }

            if (path.EndsWith("/test", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(
                    FailTest ? HttpStatusCode.InternalServerError : HttpStatusCode.Accepted));
            }

            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(Json(ListJson));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
    }
}
