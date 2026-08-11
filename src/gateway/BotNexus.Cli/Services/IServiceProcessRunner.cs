using System.Diagnostics;

namespace BotNexus.Cli.Services;

/// <summary>
/// Outcome of a helper process invocation made by an <see cref="IOsServiceManager"/>.
/// </summary>
/// <param name="ExitCode">Process exit code. Non-zero means the operation failed.</param>
/// <param name="Output">Combined stdout (or stderr when stdout was empty).</param>
internal readonly record struct ProcessRunResult(int ExitCode, string Output);

/// <summary>
/// Seam over the external process invocations performed during service install/uninstall
/// (<c>sc.exe</c>, <c>reg.exe</c>, <c>systemctl</c>).
/// </summary>
/// <remarks>
/// This exists so the install paths can be unit tested without touching the real service
/// control manager or registry: the environment merge in
/// <see cref="WindowsServiceManager"/> depends on what <c>reg query</c> reports, and a failed
/// <c>reg add</c> must fail the install, so both need to be observable and fakeable.
/// </remarks>
internal interface IServiceProcessRunner
{
    /// <summary>
    /// Runs a process passing each argument as a discrete token, letting the runtime perform
    /// the platform-correct quoting. Preferred for any argument carrying user-supplied data
    /// (paths, environment values) because it cannot be broken by a quote character.
    /// </summary>
    Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a process with a pre-composed argument line. Required for <c>sc.exe</c>, whose
    /// <c>name= value</c> option syntax is not expressible through discrete argument tokens.
    /// </summary>
    Task<ProcessRunResult> RunRawAsync(string fileName, string argumentLine, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IServiceProcessRunner"/> backed by <see cref="Process"/>.
/// </summary>
internal sealed class SystemServiceProcessRunner : IServiceProcessRunner
{
    public static readonly SystemServiceProcessRunner Instance = new();

    public Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var psi = CreateStartInfo(fileName);
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        return ExecuteAsync(psi, cancellationToken);
    }

    public Task<ProcessRunResult> RunRawAsync(string fileName, string argumentLine, CancellationToken cancellationToken)
    {
        var psi = CreateStartInfo(fileName);
        psi.Arguments = argumentLine;
        return ExecuteAsync(psi, cancellationToken);
    }

    private static ProcessStartInfo CreateStartInfo(string fileName) => new()
    {
        FileName = fileName,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    private static async Task<ProcessRunResult> ExecuteAsync(ProcessStartInfo psi, CancellationToken cancellationToken)
    {
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {psi.FileName}");

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new ProcessRunResult(process.ExitCode, string.IsNullOrWhiteSpace(output) ? error : output);
    }
}
