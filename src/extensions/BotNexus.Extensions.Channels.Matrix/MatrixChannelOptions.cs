using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.Channels.Matrix;

/// <summary>
/// Per-agent Matrix account configuration. Each entry represents one Matrix user on the
/// homeserver, owned by exactly one BotNexus agent.
/// </summary>
/// <remarks>
/// The issue (#1201) frames Matrix identity as per-agent rather than per-gateway: an agent is a
/// first-class Matrix user that a human can DM from any client. Modelling the account as a
/// dictionary entry keyed by agent name - rather than a single set of gateway-wide credentials -
/// is what makes that expressible, and it mirrors the multi-bot shape the Telegram adapter uses.
/// </remarks>
public sealed class MatrixAccountConfig
{
    /// <summary>
    /// Fully-qualified Matrix user ID for this account, e.g. <c>@farnsworth:example.com</c>.
    /// </summary>
    [Display(
        Name = "User ID",
        Description = "Fully-qualified Matrix user ID for this agent's account, e.g. @farnsworth:example.com.",
        GroupName = "Matrix",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "matrix", Order = 0)]
    public string? UserId { get; set; }

    /// <summary>
    /// Matrix access token authenticating this account against the homeserver.
    /// </summary>
    [Display(
        Name = "Access token",
        Description = "Matrix access token for this agent's account. Sensitive: stored and shown masked.",
        GroupName = "Matrix",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Secret, Group = "matrix", Order = 1, Secret = true)]
    public string? AccessToken { get; set; }

    /// <summary>
    /// BotNexus agent ID inbound messages on this account route to. When null, the account key
    /// in <see cref="MatrixChannelOptions.Agents"/> is used.
    /// </summary>
    [Display(
        Name = "Agent ID",
        Description = "BotNexus agent ID inbound messages on this account route to. Defaults to the configuration key.",
        GroupName = "Matrix",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "matrix", Order = 2)]
    public string? AgentId { get; set; }

    /// <summary>
    /// Optional per-account homeserver override. When null the shared
    /// <see cref="MatrixChannelOptions.Homeserver"/> is used.
    /// </summary>
    [Display(
        Name = "Homeserver override",
        Description = "Optional per-account homeserver base URL. Defaults to the shared homeserver.",
        GroupName = "Matrix",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "matrix", Order = 3)]
    public string? Homeserver { get; set; }

    /// <summary>
    /// Whether this account automatically accepts room invites observed on <c>/sync</c>.
    /// </summary>
    [Display(
        Name = "Auto-join on invite",
        Description = "Whether the account automatically accepts room invites observed on /sync.",
        GroupName = "Matrix",
        Order = 4)]
    [DefaultValue(true)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "matrix", Order = 4)]
    public bool AutoJoin { get; set; } = true;

    /// <summary>
    /// Allow-list of Matrix room IDs this account will process messages from. Empty permits all
    /// joined rooms.
    /// </summary>
    public ICollection<string> AllowedRoomIds { get; } = [];

    /// <summary>
    /// Allow-list of Matrix user IDs whose messages this account will process. Empty permits all
    /// senders (other than the account's own echo, which is always suppressed).
    /// </summary>
    public ICollection<string> AllowedUserIds { get; } = [];
}

/// <summary>
/// Configuration options for the Matrix channel adapter. Bind under the
/// <c>channels:matrix</c> configuration key, or configure via the
/// <c>AddBotNexusMatrixChannel</c> delegate overload.
/// </summary>
public sealed class MatrixChannelOptions
{
    /// <summary>Default <c>/sync</c> long-poll timeout in milliseconds.</summary>
    public const int DefaultSyncTimeoutMs = 30_000;

    /// <summary>Default minimum interval between streaming edit updates, in milliseconds.</summary>
    public const int DefaultStreamingBufferMs = 750;

    /// <summary>
    /// Base URL of the Matrix homeserver shared by every configured account, e.g.
    /// <c>https://matrix.example.com</c>.
    /// </summary>
    [Display(
        Name = "Homeserver",
        Description = "Base URL of the Matrix homeserver, e.g. https://matrix.example.com.",
        GroupName = "Matrix",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "matrix", Order = 0)]
    public string? Homeserver { get; set; }

    /// <summary>
    /// Per-agent Matrix accounts keyed by agent name. Each entry gets its own <c>/sync</c> loop.
    /// </summary>
    public IDictionary<string, MatrixAccountConfig> Agents { get; } =
        new Dictionary<string, MatrixAccountConfig>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Long-poll timeout in milliseconds passed to <c>/sync</c>. Values at or below zero fall
    /// back to <see cref="DefaultSyncTimeoutMs"/>: a misconfiguration must not be read as
    /// "poll with no wait", which would busy-spin against the homeserver.
    /// </summary>
    [Display(
        Name = "Sync timeout (ms)",
        Description = "Long-poll timeout in milliseconds passed to the Matrix /sync endpoint.",
        GroupName = "Matrix",
        Order = 1)]
    [DefaultValue(DefaultSyncTimeoutMs)]
    [Range(0, 600_000)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "matrix", Order = 1)]
    public int SyncTimeoutMs { get; set; } = DefaultSyncTimeoutMs;

    /// <summary>
    /// Minimum interval in milliseconds between streaming edit (<c>m.replace</c>) updates for a
    /// single in-flight response. Bounds the edit rate so a fast token stream cannot rate-limit
    /// the account.
    /// </summary>
    [Display(
        Name = "Streaming buffer (ms)",
        Description = "Minimum interval between streaming edit updates for a single response.",
        GroupName = "Matrix",
        Order = 2)]
    [DefaultValue(DefaultStreamingBufferMs)]
    [Range(0, 60_000)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "matrix", Order = 2)]
    public int StreamingBufferMs { get; set; } = DefaultStreamingBufferMs;

    /// <summary>
    /// Resolves the effective <c>/sync</c> timeout, substituting the default for a non-positive
    /// configured value.
    /// </summary>
    public int ResolveSyncTimeoutMs() => SyncTimeoutMs > 0 ? SyncTimeoutMs : DefaultSyncTimeoutMs;

    /// <summary>
    /// Resolves the effective streaming buffer interval. Zero is a legitimate configured value
    /// (edit on every delta), so only a negative value falls back to the default.
    /// </summary>
    public int ResolveStreamingBufferMs() =>
        StreamingBufferMs >= 0 ? StreamingBufferMs : DefaultStreamingBufferMs;
}
