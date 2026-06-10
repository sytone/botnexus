namespace BotNexus.Gateway.Isolation;

/// <summary>
/// Result of executing a command inside a Docker sandbox.
/// </summary>
/// <param name="ExitCode">Process exit code. 0 = success. -1 = timeout/killed.</param>
/// <param name="Stdout">Standard output captured from the command.</param>
/// <param name="Stderr">Standard error captured from the command.</param>
public sealed record SandboxExecResult(int ExitCode, string Stdout, string Stderr)
{
    /// <summary>Whether the command completed successfully (exit code 0).</summary>
    public bool Success => ExitCode == 0;
}
