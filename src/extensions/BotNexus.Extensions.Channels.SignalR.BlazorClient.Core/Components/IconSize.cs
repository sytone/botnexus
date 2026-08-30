namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;

/// <summary>
/// The three legal icon sizes. An enum rather than a pixel count because the size a glyph
/// is drawn at is a design decision with three answers, not a free number: before this,
/// <c>Icon.Size</c> was an <see cref="int"/> and call sites had picked 16, 20 and 24 while
/// the token layer declared 16 and 18 — so the tokens described a scale nothing used.
/// </summary>
/// <remarks>
/// The pixel values live in <c>tokens.css</c> as <c>--icon-sm|md|lg</c>. This enum maps to
/// the matching <c>.bn-icon-sm|md|lg</c> class, so the stylesheet stays the single source of
/// truth and the same scale sizes both an inline SVG and a text glyph.
/// </remarks>
public enum IconSize
{
    /// <summary>16px. Secondary glyphs inside a dense row — a pin, a row action.</summary>
    Small,

    /// <summary>20px. The default: a nav row marker, a primary action glyph.</summary>
    Medium,

    /// <summary>24px. A page heading, where the icon is an object rather than a marker.</summary>
    Large,
}
