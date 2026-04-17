// BotNexus Blazor Client — Markdown rendering via marked + DOMPurify
window.BotNexus = window.BotNexus || {};

/**
 * Renders a markdown string to sanitized HTML.
 * Falls back to returning the raw markdown if libraries are not loaded.
 */
window.BotNexus.renderMarkdown = function (markdown) {
    if (typeof marked !== 'undefined') {
        var html = marked.parse(markdown, { breaks: true, gfm: true });
        return typeof DOMPurify !== 'undefined' ? DOMPurify.sanitize(html) : html;
    }
    return markdown;
};
