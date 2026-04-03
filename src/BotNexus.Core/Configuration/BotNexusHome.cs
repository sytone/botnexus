namespace BotNexus.Core.Configuration;

public static class BotNexusHome
{
    private const string HomeDirectoryName = ".botnexus";
    private const string HomeOverrideEnvVar = "BOTNEXUS_HOME";

    private const string DefaultConfigJson = """
{
  "BotNexus": {
    "ExtensionsPath": "~/.botnexus/extensions",
    "Providers": {},
    "Channels": {
      "Instances": {}
    },
    "Tools": {
      "Extensions": {},
      "McpServers": {}
    }
  }
}
""";

    public static string ResolveHomePath()
    {
        var homeOverride = Environment.GetEnvironmentVariable(HomeOverrideEnvVar);
        if (!string.IsNullOrWhiteSpace(homeOverride))
            return ResolveAbsolutePath(homeOverride);

        return ResolveAbsolutePath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            HomeDirectoryName));
    }

    public static string AgentsPath => Path.Combine(ResolveHomePath(), "agents");

    public static string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        var normalized = path.Trim();
        if (normalized.StartsWith("~/.botnexus", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("~\\.botnexus", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = normalized[11..].TrimStart('\\', '/');
            var home = ResolveHomePath();
            return string.IsNullOrEmpty(suffix)
                ? home
                : ResolveAbsolutePath(Path.Combine(home, suffix));
        }

        return ResolveAbsolutePath(normalized);
    }

    public static string Initialize()
    {
        var homePath = ResolveHomePath();
        Directory.CreateDirectory(homePath);
        Directory.CreateDirectory(Path.Combine(homePath, "extensions"));
        Directory.CreateDirectory(Path.Combine(homePath, "extensions", "providers"));
        Directory.CreateDirectory(Path.Combine(homePath, "extensions", "channels"));
        Directory.CreateDirectory(Path.Combine(homePath, "extensions", "tools"));
        Directory.CreateDirectory(Path.Combine(homePath, "tokens"));
        Directory.CreateDirectory(Path.Combine(homePath, "sessions"));
        Directory.CreateDirectory(Path.Combine(homePath, "logs"));
        Directory.CreateDirectory(Path.Combine(homePath, "skills"));
        Directory.CreateDirectory(AgentsPath);

        var configPath = Path.Combine(homePath, "config.json");
        if (!File.Exists(configPath))
            File.WriteAllText(configPath, DefaultConfigJson);

        return homePath;
    }

    public static string GetAgentWorkspacePath(string agentName)
        => Path.Combine(AgentsPath, agentName);

    public static void InitializeAgentWorkspace(string agentName)
    {
        var agentWorkspacePath = GetAgentWorkspacePath(agentName);
        Directory.CreateDirectory(agentWorkspacePath);
        Directory.CreateDirectory(Path.Combine(agentWorkspacePath, "memory"));
        Directory.CreateDirectory(Path.Combine(agentWorkspacePath, "memory", "daily"));
        Directory.CreateDirectory(Path.Combine(agentWorkspacePath, "skills"));
    }

    private static string ResolveAbsolutePath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        if (expanded.StartsWith("~", StringComparison.Ordinal))
        {
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            expanded = string.Concat(userHome, expanded[1..]);
        }

        return Path.IsPathRooted(expanded)
            ? Path.GetFullPath(expanded)
            : Path.GetFullPath(expanded, Directory.GetCurrentDirectory());
    }
}
