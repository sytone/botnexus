// BotNexus Blazor Client — Markdown rendering via marked + DOMPurify
window.BotNexus = window.BotNexus || {};
var codeCopyFeedbackDurationMs = 2000;

/**
 * HTML-escapes a string so it can never be interpreted as markup.
 * Deliberately hand-written: this is the fallback used precisely when
 * third-party libraries are missing, so it must not depend on any of them.
 */
window.BotNexus.escapeHtml = function (value) {
    if (value === null || value === undefined) {
        return "";
    }

    return String(value)
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;")
        .replace(/'/g, "&#39;");
};

/**
 * Renders a markdown string to sanitized HTML.
 * Fails CLOSED: if either `marked` or `DOMPurify` is unavailable the original
 * text is returned HTML-escaped (readable, but inert) and a console warning
 * names the missing dependency. Unsanitized HTML is never returned.
 */
window.BotNexus.renderMarkdown = function (markdown) {
    var markedAvailable = typeof marked !== 'undefined';
    var purifyAvailable = typeof DOMPurify !== 'undefined';

    if (!markedAvailable || !purifyAvailable) {
        var missing = [];
        if (!markedAvailable) { missing.push("marked"); }
        if (!purifyAvailable) { missing.push("DOMPurify"); }

        if (typeof console !== 'undefined' && console && typeof console.warn === 'function') {
            console.warn(
                "[BotNexus] Markdown rendering degraded: missing " + missing.join(", ") +
                ". Falling back to HTML-escaped plain text.");
        }

        return window.BotNexus.escapeHtml(markdown);
    }

    var renderer = new marked.Renderer();
    var linkRenderer = renderer.link.bind(renderer);
    renderer.link = function (token) {
        var html = linkRenderer(token);
        return html.replace(/^<a /, '<a target="_blank" rel="noopener noreferrer" ');
    };

    var parsed = marked.parse(markdown, { breaks: true, gfm: true, renderer: renderer });
    return DOMPurify.sanitize(parsed, { ADD_ATTR: ["target", "rel"] });
};

window.BotNexus.copyToClipboard = function (text) {
    if (navigator.clipboard && typeof navigator.clipboard.writeText === "function") {
        return navigator.clipboard.writeText(text).then(function () { return true; }, function () { return false; });
    }

    return Promise.resolve(false);
};

window.BotNexus.attachCodeCopyButtons = function (containerEl) {
    if (!containerEl) {
        return;
    }

    containerEl.querySelectorAll(".msg-content pre > code").forEach(function (codeEl) {
        var preEl = codeEl.parentElement;
        if (!preEl || preEl.querySelector(".code-copy-btn")) {
            return;
        }

        var buttonEl = document.createElement("button");
        buttonEl.type = "button";
        buttonEl.className = "code-copy-btn";
        buttonEl.textContent = "📋";
        buttonEl.title = "Copy code";
        buttonEl.setAttribute("aria-label", "Copy code");

        buttonEl.addEventListener("click", function () {
            // Trim to match the issue requirement of copying code text without leading/trailing whitespace.
            window.BotNexus.copyToClipboard((codeEl.textContent || "").trim()).then(function (copied) {
                if (!copied) {
                    return;
                }

                buttonEl.classList.add("copied");
                buttonEl.textContent = "✓";
                buttonEl.title = "Copied!";
                buttonEl.setAttribute("aria-label", "Copied!");

                window.setTimeout(function () {
                    buttonEl.classList.remove("copied");
                    buttonEl.textContent = "📋";
                    buttonEl.title = "Copy code";
                    buttonEl.setAttribute("aria-label", "Copy code");
                }, codeCopyFeedbackDurationMs);
            });
        });

        preEl.appendChild(buttonEl);
    });
};
