namespace BotNexus.Agent.Providers.Core.Utilities;

/// <summary>
/// Optional extra Copilot request headers a caller can supply alongside the
/// always-on dynamic headers (X-Initiator, Openai-Intent, Copilot-Vision-Request).
/// </summary>
/// <remarks>
/// All properties are nullable. A null value means "do not emit this header,"
/// preserving the current wire shape for callers that do not opt in. This
/// keeps the existing parity tests green while letting newer call sites
/// surface higher-fidelity headers captured from the real Copilot CLI.
/// </remarks>
/// <param name="IntegrationId">
/// Value for <c>Copilot-Integration-Id</c>. Real Copilot CLI traffic uses
/// <c>copilot-developer-cli</c>; the VS Code chat client uses <c>vscode-chat</c>.
/// Empty/null = not sent.
/// </param>
/// <param name="ApiVersion">
/// Value for <c>X-GitHub-Api-Version</c>, e.g. <c>2026-06-01</c>. Empty/null = not sent.
/// </param>
/// <param name="EditorVersion">
/// Value for <c>Editor-Version</c>, e.g. <c>BotNexus/0.1.0</c>. Empty/null = not sent.
/// </param>
/// <param name="InteractionId">
/// Value for <c>X-Interaction-Id</c>. When null and any of the other extra
/// headers are emitted, callers should generate a per-call correlation id
/// rather than re-using a single value across requests.
/// </param>
/// <param name="IntentOverride">
/// Overrides the default <c>Openai-Intent</c> value. Captures show the
/// Copilot CLI varies this per call (e.g. <c>conversation-agent</c>,
/// <c>conversation-background</c>). When null, the default <c>conversation-edits</c>
/// is preserved for backward compatibility.
/// </param>
public sealed record CopilotHeaderOptions(
    string? IntegrationId = null,
    string? ApiVersion = null,
    string? EditorVersion = null,
    string? InteractionId = null,
    string? IntentOverride = null);
