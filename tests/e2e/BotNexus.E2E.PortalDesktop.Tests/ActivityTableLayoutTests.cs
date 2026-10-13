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
    /// fixed-width Status column, with the complete long title visible and wrapping.
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

        // #4882 supersedes the one-line ellipsis contract. Exercise a deterministic long title
        // in the real layout without changing server data; restore it after measurement.
        var titleSpan = page.Locator("[data-testid='activity-row'] .activity-conversation-title").First;
        var originalTitle = await titleSpan.TextContentAsync();
        const string longTitle = "Review the complete rendering regression and preserve every identifying word " +
            "across scheduled automation and conversation activity abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvwxyz " +
            "including the final readable suffix rather than hiding it behind an ellipsis.";
        try
        {
            await titleSpan.EvaluateAsync("(el, title) => el.textContent = title", longTitle);
            var fullTextVisible = await titleSpan.EvaluateAsync<bool>("""
                el => {
                    const cell = el.closest('td');
                    const wrapper = el.parentElement;
                    const style = getComputedStyle(el);
                    const cellStyle = getComputedStyle(cell);
                    const box = el.getBoundingClientRect();
                    const cellBox = cell.getBoundingClientRect();
                    const headerBox = cell.closest('table').querySelector('th').getBoundingClientRect();
                    const range = document.createRange();
                    range.selectNodeContents(el);
                    const textBox = range.getBoundingClientRect();
                    const lineHeight = parseFloat(style.lineHeight) || parseFloat(style.fontSize) * 1.5;
                    const usableWidth = cellBox.width - parseFloat(cellStyle.paddingLeft) - parseFloat(cellStyle.paddingRight);
                    return cellStyle.display === 'table-cell'
                        && wrapper.classList.contains('activity-title-content')
                        && getComputedStyle(wrapper).display === 'flex'
                        && style.whiteSpace === 'normal' && style.overflowWrap === 'anywhere'
                        && style.overflow === 'visible' && style.textOverflow !== 'ellipsis'
                        && Math.abs(cellBox.width - headerBox.width) <= 1
                        && Math.abs(box.width - usableWidth) <= 1
                        && box.height > lineHeight * 1.6
                        && el.scrollWidth <= el.clientWidth + 1
                        && el.scrollHeight <= el.clientHeight + 1
                        && textBox.left >= box.left - 1 && textBox.right <= box.right + 1
                        && textBox.bottom <= box.bottom + 1;
                }
                """);
            Assert.Equal(longTitle, await titleSpan.TextContentAsync());
            Assert.True(fullTextVisible, "#4882: the complete title must wrap within the full allocated table cell without clipping.");
        }
        finally
        {
            await titleSpan.EvaluateAsync("(el, title) => el.textContent = title", originalTitle);
        }
    }
}
