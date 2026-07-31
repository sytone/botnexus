using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Unit tests for <see cref="ConversationLabel"/> (#2528): opaque-identifier detection, bounded
/// truncation, and the derived label that replaces a raw routing token used as a title.
/// </summary>
public sealed class ConversationLabelTests
{
    private const string ServiceBusId =
        "servicebus:a:1lexPcP4_GMPlgVVbjGrdGzyqu_vhKl8pYMbpdTsQtXOvY1lWpznwGCftUS0BRbXu4Bu3TbCzOO5xGw7E4sRVj9w1J1";

    // ── IsOpaqueIdentifier ─────────────────────────────────────────────────

    [Fact]
    public void Long_routing_token_is_opaque()
    {
        Assert.True(ConversationLabel.IsOpaqueIdentifier(ServiceBusId));
    }

    [Theory]
    [InlineData("Deployment review")]
    [InlineData("fix the activity table")]
    [InlineData("build-fix")]
    [InlineData("cron:nightly")]
    public void Human_titles_are_not_opaque(string title)
    {
        Assert.False(ConversationLabel.IsOpaqueIdentifier(title));
    }

    [Fact]
    public void Empty_title_is_not_opaque()
    {
        Assert.False(ConversationLabel.IsOpaqueIdentifier(""));
        Assert.False(ConversationLabel.IsOpaqueIdentifier(null));
    }

    [Fact]
    public void Prose_containing_a_long_token_is_not_opaque()
    {
        Assert.False(ConversationLabel.IsOpaqueIdentifier("session " + ServiceBusId));
    }

    // ── Truncate ───────────────────────────────────────────────────────────

    [Fact]
    public void Truncate_bounds_long_values_with_an_ellipsis()
    {
        var result = ConversationLabel.Truncate(new string('x', 500));

        Assert.Equal(ConversationLabel.MaxTitleLength, result.Length);
        Assert.EndsWith("\u2026", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Truncate_leaves_short_values_unchanged()
    {
        Assert.Equal("Deployment review", ConversationLabel.Truncate("Deployment review"));
    }

    [Fact]
    public void Truncate_collapses_embedded_newlines()
    {
        Assert.Equal("a b", ConversationLabel.Truncate("a\nb"));
    }

    [Fact]
    public void Truncate_honours_an_explicit_max_length()
    {
        var result = ConversationLabel.Truncate("abcdefghij", 5);

        Assert.Equal(5, result.Length);
        Assert.Equal("abcd\u2026", result);
    }

    // ── DerivedLabel ───────────────────────────────────────────────────────

    [Fact]
    public void Derived_label_uses_scheme_agent_and_short_id()
    {
        var label = ConversationLabel.DerivedLabel(ServiceBusId, "farnsworth");

        Assert.StartsWith("servicebus", label, StringComparison.Ordinal);
        Assert.Contains("farnsworth", label, StringComparison.Ordinal);
        Assert.Equal(ServiceBusId[^8..], label[^8..]);
    }

    [Fact]
    public void Derived_label_without_scheme_still_names_the_agent()
    {
        var label = ConversationLabel.DerivedLabel("abcdef123456", "alpha");

        Assert.Contains("alpha", label, StringComparison.Ordinal);
        Assert.DoesNotContain(":", label, StringComparison.Ordinal);
    }

    [Fact]
    public void Derived_label_falls_back_when_nothing_is_known()
    {
        Assert.Equal("(untitled)", ConversationLabel.DerivedLabel(null, null));
    }

    // ── DisplayTitle ───────────────────────────────────────────────────────

    [Fact]
    public void Display_title_replaces_a_raw_routing_id_with_a_derived_label()
    {
        var title = ConversationLabel.DisplayTitle(ServiceBusId, ServiceBusId, "farnsworth");

        Assert.NotEqual(ServiceBusId, title);
        Assert.Contains("farnsworth", title, StringComparison.Ordinal);
        Assert.StartsWith("servicebus", title, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_title_keeps_a_human_title()
    {
        Assert.Equal(
            "Deployment review",
            ConversationLabel.DisplayTitle("Deployment review", ServiceBusId, "farnsworth"));
    }

    [Fact]
    public void Display_title_is_always_bounded()
    {
        var longProse = string.Join(" ", Enumerable.Repeat("word", 200));

        var title = ConversationLabel.DisplayTitle(longProse, "c1", "alpha");

        Assert.True(title.Length <= ConversationLabel.MaxTitleLength);
    }

    [Fact]
    public void Display_title_derives_when_the_title_is_blank()
    {
        var title = ConversationLabel.DisplayTitle("   ", ServiceBusId, "farnsworth");

        Assert.Contains("farnsworth", title, StringComparison.Ordinal);
    }
}
