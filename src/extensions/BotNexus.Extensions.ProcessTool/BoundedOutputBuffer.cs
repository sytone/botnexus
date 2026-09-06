using BotNexus.Agent.Core.Tools;

namespace BotNexus.Extensions.ProcessTool;

/// <summary>Compatibility name for the shared core buffer; all retention accounting lives in one owner.</summary>
internal sealed class BoundedOutputBuffer(int maxOutputBytes = OutputRetentionPolicy.MaxOutputBytes)
    : BackgroundOutputBuffer(maxOutputBytes);
