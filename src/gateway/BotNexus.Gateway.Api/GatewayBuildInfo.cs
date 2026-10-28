using System.Reflection;

namespace BotNexus.Gateway.Api;

/// <summary>
/// Build and runtime information captured at startup.
/// </summary>
public static class GatewayBuildInfo
{
    /// <summary>When the gateway process started.</summary>
    public static DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>Timestamp of the running gateway assembly used by both Portal clients.</summary>
    public static DateTimeOffset BuildTimestamp { get; } =
        new(File.GetLastWriteTimeUtc(typeof(GatewayBuildInfo).Assembly.Location), TimeSpan.Zero);

    /// <summary>Full commit SHA embedded at build time via AssemblyMetadata.</summary>
    public static string CommitSha { get; } = ReadCommitSha(typeof(GatewayBuildInfo).Assembly);

    private static string ReadCommitSha(Assembly assembly)
    {
        var commitSha = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "CommitSha")?.Value;
        return string.IsNullOrWhiteSpace(commitSha) ? "unknown" : commitSha;
    }

    /// <summary>Short commit SHA (first 7 chars).</summary>
    public static string CommitShort => CommitSha.Length >= 7 ? CommitSha[..7] : CommitSha;

    /// <summary>Assembly version.</summary>
    public static string Version { get; } = typeof(GatewayBuildInfo).Assembly
        .GetName().Version?.ToString() ?? "0.0.0";
}
