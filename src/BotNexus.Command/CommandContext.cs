using BotNexus.Core.Models;

namespace BotNexus.Command;

/// <summary>Context for a command invocation.</summary>
public record CommandContext(
    InboundMessage Message,
    string Command,
    string[] Args,
    CancellationToken CancellationToken);
