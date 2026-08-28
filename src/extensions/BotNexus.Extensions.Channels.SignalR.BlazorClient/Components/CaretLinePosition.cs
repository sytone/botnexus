namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;

/// <summary>
/// #2918: the result of the caret-edge probe used to gate bare-arrow prompt-history recall.
///
/// This is the C# projection of the object returned by <c>chatScroll.caretLinePosition</c> in
/// <c>wwwroot/js/chat.js</c>. The property names match the JS object's camelCase keys, which the
/// Web JSON defaults Blazor interop uses match case-insensitively.
///
/// It is public because it is an interop CONTRACT, not an implementation detail: it is the seam
/// tests substitute in order to exercise the bare-arrow edge gating without a real browser caret.
/// The JS side measures the edge exactly once per keystroke and latches it, so the value that
/// arrives here is the same value that decided whether the native caret movement was suppressed.
/// </summary>
/// <param name="OnFirstLine">True when the caret sits on the first logical line of the textarea.</param>
/// <param name="OnLastLine">True when the caret sits on the last logical line of the textarea.</param>
public sealed record CaretLinePosition(bool OnFirstLine, bool OnLastLine);
