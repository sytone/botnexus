// BotNexus Blazor Client — Markdown rendering via marked + DOMPurify
window.BotNexus = window.BotNexus || {};
var codeCopyFeedbackDurationMs = 2000;

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
