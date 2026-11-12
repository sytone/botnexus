using BotNexus.Extensions.Plugins;
using BotNexus.Extensions.Plugins.Lifecycle;

namespace BotNexus.Extensions.Mcp.Plugins;

/// <summary>Validated native-only standalone identity. Installed registration uses its configured store instead.</summary>
public sealed class NativePluginMcpAdmission
{
    private NativePluginMcpAdmission(string name, string directory, NativePluginMcpAuthority authority)
    {
        Name = name;
        Directory = directory;
        Authority = authority;
    }

    internal NativePluginMcpAuthority Authority { get; }

    /// <summary>Identity validated by the strict native manifest schema.</summary>
    public string Name { get; }

    /// <summary>Canonical directory bound to this admission.</summary>
    public string Directory { get; }

    /// <summary>Validates an explicit standalone native package, rejecting foreign/installed envelopes and filesystem aliases.</summary>
    /// <param name="directory">Standalone package directory.</param>
    /// <param name="authority">Explicit configured standalone boundary, disjoint from installed ownership.</param>
    /// <returns>Native identity evidence bound to that authority, revalidated by registration.</returns>
    public static NativePluginMcpAdmission Admit(string directory, NativePluginMcpAuthority authority)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        ArgumentNullException.ThrowIfNull(authority);
        authority.Validate(root);
        var metadata = Path.Combine(root, PluginManifestParser.PluginMetadataDirectoryName);
        var manifest = Path.Combine(metadata, PluginManifestParser.ManifestFileName);
        if ((File.GetAttributes(metadata) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(manifest) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Native metadata must not be linked.");
        var parsed = new PluginManifestParser().ParsePluginDirectory(root);
        if (!parsed.IsValid || parsed.Value is not { } native)
            throw new InvalidDataException("Standalone native manifest was not admitted.");
        return new NativePluginMcpAdmission(native.Name, root, authority);
    }
}
