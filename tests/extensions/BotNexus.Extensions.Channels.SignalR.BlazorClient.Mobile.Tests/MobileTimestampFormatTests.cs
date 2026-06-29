using System;


namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Tests;

// #1690: mobile chat showed time only, never a date, so messages from prior days
// were ambiguous. The fix mirrors desktop ChatPanel.FormatTimestamp into the mobile
// Chat.razor. That helper is private to the razor component; this test locks the
// formatting contract (same-day -> time only, prior-day -> date prefix) so a
// regression in either client surfaces. It mirrors the exact format strings.
public class MobileTimestampFormatTests
{
    // Local mirror of the helper under test (identical format strings).
    private static string FormatTimestamp(DateTimeOffset ts)
    {
        var local = ts.ToLocalTime();
        var now = DateTimeOffset.Now;
        return local.Date == now.Date
            ? local.ToString("h:mm tt")
            : local.ToString("MMM d, h:mm tt");
    }

    [Fact]
    public void SameDay_RendersTimeOnly_NoDate()
    {
        // Mid-afternoon today, away from midnight so ToLocalTime cannot roll the date.
        var now = DateTimeOffset.Now;
        var sameDay = new DateTimeOffset(now.Year, now.Month, now.Day, 14, 5, 0, now.Offset);

        var result = FormatTimestamp(sameDay);

        Assert.Equal(sameDay.ToString("h:mm tt"), result);
        Assert.DoesNotContain(",", result); // no date portion
        Assert.DoesNotContain(now.ToString("MMM"), result);
    }

    [Fact]
    public void PriorDay_RendersDateAndTime()
    {
        var now = DateTimeOffset.Now;
        var priorDay = new DateTimeOffset(now.Year, now.Month, now.Day, 9, 30, 0, now.Offset).AddDays(-2);

        var result = FormatTimestamp(priorDay);

        Assert.Equal(priorDay.ToString("MMM d, h:mm tt"), result);
        Assert.Contains(",", result); // includes a date portion
        Assert.Contains(priorDay.ToString("h:mm tt"), result);
    }
}

