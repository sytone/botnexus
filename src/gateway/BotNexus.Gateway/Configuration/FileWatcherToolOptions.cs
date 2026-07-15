using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Configuration;

/// <summary>Bounds for the built-in file watcher tool.</summary>
public sealed class FileWatcherToolOptions
{
    /// <summary>Maximum time, in seconds, an agent may wait on a file-watch request.</summary>
    [Display(
        Name = "Max timeout (seconds)",
        Description = "Maximum time, in seconds, an agent may wait on a file-watch request. Longer requests are clamped to this ceiling.",
        GroupName = "File watcher tool",
        Order = 0)]
    [DefaultValue(1800)]
    [Range(1, int.MaxValue)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "file-watcher-tool", Order = 0)]
    public int MaxTimeoutSeconds { get; set; } = 1800; // 30 minutes

    /// <summary>Default file-watch timeout, in seconds, when a request omits one.</summary>
    [Display(
        Name = "Default timeout (seconds)",
        Description = "Default file-watch timeout, in seconds, applied when a request does not specify one.",
        GroupName = "File watcher tool",
        Order = 1)]
    [DefaultValue(300)]
    [Range(1, int.MaxValue)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "file-watcher-tool", Order = 1)]
    public int DefaultTimeoutSeconds { get; set; } = 300; // 5 minutes

    /// <summary>Debounce interval, in milliseconds, coalescing rapid filesystem events.</summary>
    [Display(
        Name = "Debounce (ms)",
        Description = "Debounce interval, in milliseconds, used to coalesce rapid filesystem events into a single wake (some editors fire multiple events per save).",
        GroupName = "File watcher tool",
        Order = 2)]
    [DefaultValue(500)]
    [Range(0, int.MaxValue)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "file-watcher-tool", Order = 2)]
    public int DebounceMilliseconds { get; set; } = 500;
}
