using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Export;

/// <summary>
/// The wire formats an export route accepts, and the per-format response facts (content type and
/// file extension) that go with each (issue #3278).
/// </summary>
/// <remarks>
/// Modelled as a closed set parsed at the route boundary rather than a raw string threaded into the
/// renderers. An unrecognised format is a 400 at the edge, which means no code downstream has to
/// carry a "what if the format is nonsense" branch, and the content type can never drift out of
/// agreement with the renderer that produced the bytes.
/// </remarks>
public enum ExportFormatKind
{
    /// <summary>Markdown output (<c>text/markdown</c>, <c>.md</c>).</summary>
    Markdown,

    /// <summary>Standalone HTML output (<c>text/html</c>, <c>.html</c>).</summary>
    Html
}

/// <summary>
/// Parsing and per-format metadata for <see cref="ExportFormatKind"/>.
/// </summary>
public static class ExportFormat
{
    /// <summary>
    /// Parses a route format token. Accepts <c>markdown</c>, <c>md</c>, <c>html</c> and <c>htm</c>,
    /// case-insensitively.
    /// </summary>
    /// <param name="value">The route token.</param>
    /// <param name="format">The parsed format when the token is recognised.</param>
    /// <returns><see langword="true"/> when the token is recognised.</returns>
    public static bool TryParse(string? value, out ExportFormatKind format)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "markdown":
            case "md":
                format = ExportFormatKind.Markdown;
                return true;
            case "html":
            case "htm":
                format = ExportFormatKind.Html;
                return true;
            default:
                format = default;
                return false;
        }
    }

    /// <summary>Gets the response content type for a format.</summary>
    /// <param name="format">The export format.</param>
    /// <returns>The MIME type, without a charset parameter.</returns>
    public static string ContentType(ExportFormatKind format)
        => format == ExportFormatKind.Markdown ? "text/markdown" : "text/html";

    /// <summary>Gets the download file extension for a format, without the leading dot.</summary>
    /// <param name="format">The export format.</param>
    /// <returns>The extension.</returns>
    public static string Extension(ExportFormatKind format)
        => format == ExportFormatKind.Markdown ? "md" : "html";
}

/// <summary>
/// Builds the HTTP file response for an assembled <see cref="ExportDocument"/> (issue #3278,
/// acceptance criterion 7).
/// </summary>
/// <remarks>
/// Shared by the conversation and session export routes so the content type, the UTF-8 encoding and
/// the <c>Content-Disposition</c> filename convention are decided in exactly one place. Two routes
/// each assembling their own <c>File(...)</c> call is how a content-type or filename rule silently
/// applies to one download and not the other.
/// </remarks>
public static class ExportResponse
{
    /// <summary>
    /// Renders the document in the requested format and returns it as a UTF-8 file download.
    /// </summary>
    /// <param name="document">The assembled export document.</param>
    /// <param name="format">The requested output format.</param>
    /// <param name="controller">The calling controller, used to build the <see cref="FileContentResult"/>.</param>
    /// <param name="redactSecrets">
    /// When true (the default), recognised credential shapes are redacted at render time. Every
    /// export route leaves this at the default; the parameter exists so tests can render an
    /// un-redacted document to prove the redaction is what removed the secret.
    /// </param>
    /// <returns>The file result.</returns>
    public static FileContentResult File(
        ExportDocument document,
        ExportFormatKind format,
        ControllerBase controller,
        bool redactSecrets = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(controller);

        var rendered = format == ExportFormatKind.Markdown
            ? ExportMarkdownRenderer.Render(document, redactSecrets)
            : ExportHtmlRenderer.Render(document, redactSecrets);

        // The filename slug prefers the conversation title; a session export of an orphan session has
        // none, so it falls back to the session id, and ExportFileName falls back again to a constant
        // if that yields nothing usable.
        var slugSource = !string.IsNullOrWhiteSpace(document.Title)
            ? document.Title
            : document.Sessions.Count > 0 ? document.Sessions[0].SessionId : document.ConversationId;

        var fileName = ExportFileName.Build(slugSource, document.GeneratedAt, ExportFormat.Extension(format));
        var bytes = System.Text.Encoding.UTF8.GetBytes(rendered);

        return controller.File(bytes, ExportFormat.ContentType(format), fileName);
    }
}
