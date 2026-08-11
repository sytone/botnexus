using System;
using System.Threading.Tasks;

namespace BotNexus.E2E.PortalDesktop.Tests;

/// <summary>
/// Real-browser geometry coverage for the Activity table's Conversation column (#2788).
///
/// <para>Clause 6 of #2788 asks for a test that observes RENDERED geometry rather than DOM
/// presence, because the title string was always in the DOM - it was the resolved column
/// width that collapsed to approximately zero. Only a real layout pass can measure that,
/// so it is measured here with <c>getBoundingClientRect</c> against a live portal.</para>
///
/// <para><b>Skip contract.</b> Consistent with the rest of this suite, the test skips when
/// <c>E2E_PORTAL_DESKTOP_URL</c> is unset - there is no portal to drive - and never
/// silently passes. This project is quarantined out of the <c>core</c> validation gate
/// (the NotExecuted defect), so the same invariant is additionally enforced without a
/// browser by <c>ActivityTableColumnGeometryTests</c> in the BlazorClient test project,
/// which does run on every gate.</para>
/// </summary>
public sealed class ActivityTableLayoutTests
{
    /// <summary>
    /// The Conversation column must render at a readable width and be wider than the
    /// fixed-width Status column, with the title laid out on a single line.
    /// </summary>
    [SkippableFact]
    public async Task ActivityTable_ConversationColumn_IsWiderThanStatusColumn()
    {
        var baseUrl = PortalPlaywright.PortalBaseUrl;
        Skip.If(
            string.IsNullOrWhiteSpace(baseUrl),
            "E2E_PORTAL_DESKTOP_URL not set; no running desktop portal to drive.");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await PortalPlaywright.LaunchChromiumAsync(playwright);
        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
        });

        await page.GotoAsync($"{baseUrl!.TrimEnd('/')}/activity");

        await page.WaitForSelectorAsync(
            "[data-testid='activity-row']",
            new() { Timeout = 20000, State = WaitForSelectorState.Visible });

        var titleCell = page.Locator("[data-testid='activity-row'] td.activity-cell-title").First;
        var statusCell = page.Locator("[data-testid='activity-row'] td:nth-child(3)").First;

        var titleBox = await titleCell.BoundingBoxAsync()
            ?? throw new InvalidOperationException("Conversation cell has no layout box.");
        var statusBox = await statusCell.BoundingBoxAsync()
            ?? throw new InvalidOperationException("Status cell has no layout box.");

        Assert.True(
            titleBox.Width > 0,
            $"#2788: the Conversation column measured {titleBox.Width}px - it has collapsed.");
        Assert.True(
            titleBox.Width > statusBox.Width,
            $"#2788: the Conversation column measured {titleBox.Width}px, not wider than the Status " +
            $"column at {statusBox.Width}px.");

        // #2528: the row stays exactly one line tall - the title ellipsises, it does not wrap.
        var titleSpan = page.Locator("[data-testid='activity-row'] .activity-conversation-title").First;
        var spanBox = await titleSpan.BoundingBoxAsync()
            ?? throw new InvalidOperationException("Conversation title span has no layout box.");
        var lineHeight = await titleSpan.EvaluateAsync<double>(
            "el => parseFloat(getComputedStyle(el).lineHeight) || parseFloat(getComputedStyle(el).fontSize) * 1.5");

        Assert.True(
            spanBox.Height <= lineHeight * 1.6,
            $"#2788/#2528: the title rendered {spanBox.Height}px tall against a {lineHeight}px line box - it wrapped.");
    }
}
