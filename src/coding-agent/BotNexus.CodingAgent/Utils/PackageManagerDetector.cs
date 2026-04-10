namespace BotNexus.CodingAgent.Utils;

public static class PackageManagerDetector
{
    public static string Detect(System.IO.Abstractions.IFileSystem fileSystem, string workingDir)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        var root = Path.GetFullPath(workingDir);

        if (fileSystem.File.Exists(Path.Combine(root, "pnpm-lock.yaml")))
        {
            return "pnpm";
        }

        if (fileSystem.File.Exists(Path.Combine(root, "yarn.lock")))
        {
            return "yarn";
        }

        if (fileSystem.File.Exists(Path.Combine(root, "package-lock.json")))
        {
            return "npm";
        }

        if (fileSystem.File.Exists(Path.Combine(root, "go.mod")))
        {
            return "go";
        }

        if (fileSystem.File.Exists(Path.Combine(root, "Cargo.toml")))
        {
            return "cargo";
        }

        if (fileSystem.File.Exists(Path.Combine(root, "pom.xml")))
        {
            return "maven";
        }

        if (fileSystem.File.Exists(Path.Combine(root, "build.gradle")) || fileSystem.File.Exists(Path.Combine(root, "build.gradle.kts")))
        {
            return "gradle";
        }

        if (fileSystem.Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly).Any()
            || fileSystem.Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories).Any())
        {
            return "dotnet";
        }

        if (fileSystem.File.Exists(Path.Combine(root, "Gemfile.lock")))
        {
            return "bundler";
        }

        if (fileSystem.File.Exists(Path.Combine(root, "requirements.txt")) || fileSystem.File.Exists(Path.Combine(root, "pyproject.toml")))
        {
            return "python";
        }

        return "unknown";
    }
}
