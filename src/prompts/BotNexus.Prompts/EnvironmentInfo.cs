using System.Runtime.InteropServices;

namespace BotNexus.Prompts;

public static class EnvironmentInfo
{
    public static IReadOnlyList<string> BuildSection(string workingDirectory, string? gitBranch, string? gitStatus, string packageManager)
    {
        return
        [
            $"- OS: {RuntimeInformation.OSDescription}",
            $"- Working directory: {workingDirectory.Replace('\\', '/')}",
            $"- Git branch: {gitBranch ?? \"N/A\"}",
            $"- Git status: {gitStatus ?? \"N/A\"}",
            $"- Package manager: {packageManager}"
        ];
    }
}