using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text.Json;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Cli.Services;

/// <summary>
/// Preserves the complete launch directory before a build can overwrite it. Recovery uses the
/// original path so apphost selection, dependency probing and assembly-relative content stay intact.
/// This is binary recovery only; it does not roll back source, configuration or persisted data.
/// </summary>
internal sealed class GatewayBuildSnapshot : IDisposable
{
    private readonly IFileSystem _fileSystem;
    private readonly string _outputDirectory;
    private readonly Dictionary<string, string> _files;
    private readonly string[] _directories;
    private bool _retain;

    internal string PreservedDirectory { get; }
    internal bool RetainedForManualRecovery => _retain;

    private GatewayBuildSnapshot(IFileSystem fileSystem, string outputDirectory, string preservedDirectory,
        Dictionary<string, string> files, string[] directories)
    {
        _fileSystem = fileSystem;
        _outputDirectory = outputDirectory;
        PreservedDirectory = preservedDirectory;
        _files = files;
        _directories = directories;
    }

    internal static GatewayBuildSnapshot Capture(IFileSystem fileSystem, string gatewayBinary)
    {
        var output = fileSystem.Path.GetDirectoryName(fileSystem.Path.GetFullPath(gatewayBinary))
            ?? throw new IOException("Gateway output directory is missing.");
        ValidateRuntime(fileSystem, output, gatewayBinary);
        var files = Inventory(fileSystem, output);
        var directories = Directories(fileSystem, output);
        var parent = fileSystem.Path.GetDirectoryName(output) ?? throw new IOException("Gateway output has no parent.");
        var preserved = fileSystem.Path.Combine(parent, ".botnexus-update-recovery-" + Guid.NewGuid().ToString("N"));
        var snapshot = new GatewayBuildSnapshot(fileSystem, output, preserved, files, directories);
        try
        {
            snapshot.CopyTree(output, preserved);
            snapshot.Verify(preserved);
            // Detect concurrent changes during preservation, not just a successful partial copy.
            snapshot.Verify(output);
            return snapshot;
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    internal async Task RestoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await VerifyAsync(PreservedDirectory, cancellationToken); // Never destroy candidate output for an untrusted snapshot.
            cancellationToken.ThrowIfCancellationRequested();
            if (_fileSystem.Directory.Exists(_outputDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                _fileSystem.Directory.Delete(_outputDirectory, recursive: true);
            }
            await CopyTreeAsync(PreservedDirectory, _outputDirectory, cancellationToken);
            await VerifyAsync(_outputDirectory, cancellationToken);
        }
        catch
        {
            _retain = true; // Keep the only preserved tree for manual investigation if restore fails.
            throw;
        }
    }

    private static void ValidateRuntime(IFileSystem fs, string root, string gatewayBinary)
    {
        var stem = fs.Path.GetFileNameWithoutExtension(gatewayBinary);
        foreach (var name in new[] { fs.Path.GetFileName(gatewayBinary), stem + ".runtimeconfig.json", stem + ".deps.json" })
        {
            if (!fs.File.Exists(fs.Path.Combine(root, name)))
                throw new IOException("Prior gateway runtime is incomplete: " + name);
        }
        try
        {
            using var runtimeConfig = JsonDocument.Parse(fs.File.ReadAllText(fs.Path.Combine(root, stem + ".runtimeconfig.json")));
            if (!runtimeConfig.RootElement.TryGetProperty("runtimeOptions", out var options)
                || options.ValueKind != JsonValueKind.Object
                || !HasString(options, "tfm"))
                throw new IOException("Prior gateway runtime configuration has no runtime options.");
            // Framework-dependent and self-contained outputs both describe their framework set.
            var frameworks = new List<JsonElement>();
            if (options.TryGetProperty("framework", out var framework))
                frameworks.Add(framework);
            foreach (var property in new[] { "frameworks", "includedFrameworks" })
            {
                if (!options.TryGetProperty(property, out var values)) continue;
                if (values.ValueKind != JsonValueKind.Array)
                    throw new IOException("Prior gateway runtime framework list is invalid.");
                frameworks.AddRange(values.EnumerateArray());
            }
            if (frameworks.Count == 0 || frameworks.Any(value => value.ValueKind != JsonValueKind.Object
                || !HasString(value, "name") || !HasString(value, "version")))
                throw new IOException("Prior gateway runtime frameworks are missing or invalid.");
            using var deps = JsonDocument.Parse(fs.File.ReadAllText(fs.Path.Combine(root, stem + ".deps.json")));
            if (!deps.RootElement.TryGetProperty("targets", out var targets) || !targets.EnumerateObject().Any())
                throw new IOException("Prior gateway dependency manifest has no runtime targets.");
            var runtimeAssets = 0;
            var entryPointFound = false;
            foreach (var target in targets.EnumerateObject())
            foreach (var library in target.Value.EnumerateObject())
            foreach (var group in library.Value.EnumerateObject())
            {
                if (group.Name is not ("runtime" or "native" or "resources" or "runtimeTargets"))
                    continue;
                foreach (var asset in group.Value.EnumerateObject())
                {
                    if (fs.Path.GetFileName(asset.Name) == "_._")
                        continue;
                    if (group.Name == "runtime" || group.Name == "runtimeTargets"
                        && asset.Value.TryGetProperty("assetType", out var assetType) && assetType.GetString() == "runtime")
                    {
                        runtimeAssets++;
                        entryPointFound |= fs.Path.GetFileName(asset.Name) == stem + ".dll";
                    }
                    // Build outputs flatten ordinary managed/native assets but retain RID assets
                    // and culture directories. Accept only paths within the preserved output.
                    var relative = asset.Name.Replace('/', fs.Path.DirectorySeparatorChar);
                    if (group.Name == "resources")
                    {
                        if (!asset.Value.TryGetProperty("locale", out var locale) || string.IsNullOrWhiteSpace(locale.GetString()))
                            throw new IOException("Prior gateway resource locale is missing.");
                        relative = fs.Path.Combine(locale.GetString() ?? throw new IOException("Invalid resource locale."), fs.Path.GetFileName(relative));
                    }
                    var full = fs.Path.GetFullPath(fs.Path.Combine(root, relative));
                    var inside = fs.Path.GetRelativePath(root, full);
                    if (fs.Path.IsPathRooted(inside) || inside == ".." || inside.StartsWith(".." + fs.Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        throw new IOException("Prior gateway dependency escapes its output directory.");
                    if (!fs.File.Exists(full) && (group.Name is "resources" or "runtimeTargets"
                        || !fs.File.Exists(fs.Path.Combine(root, fs.Path.GetFileName(relative)))))
                        throw new IOException("Prior gateway dependency is missing: " + asset.Name);
                }
            }
            if (runtimeAssets == 0 || !entryPointFound)
                throw new IOException("Prior gateway dependency manifest has no gateway runtime asset closure.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new IOException("Prior gateway runtime manifests are invalid.", ex);
        }
    }

    private static bool HasString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString());

    private static void RejectLink(IFileSystem fs, string path)
    {
        if ((fs.File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Gateway recovery refuses linked output: " + path);
    }

    private static string[] Directories(IFileSystem fs, string root, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RejectLink(fs, root);
        // Walk one level at a time so a directory link is rejected before traversal.
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var child in fs.Directory.GetDirectories(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectLink(fs, child);
                result.Add(fs.Path.GetRelativePath(root, child));
                pending.Push(child);
            }
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    private static Dictionary<string, string> Inventory(IFileSystem fs, string root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relative in Directories(fs, root).Prepend(""))
        foreach (var file in fs.Directory.GetFiles(fs.Path.Combine(root, relative)))
        {
            RejectLink(fs, file);
            using var stream = fs.File.OpenRead(file);
            var fingerprint = Convert.ToHexString(SHA256.HashData(stream));
            if (!OperatingSystem.IsWindows())
                fingerprint += ":" + fs.File.GetUnixFileMode(file);
            result.Add(fs.Path.GetRelativePath(root, file), fingerprint);
        }
        return result;
    }

    private void Verify(string root)
    {
        if (!_fileSystem.Directory.Exists(root))
            throw new IOException("Preserved gateway output directory is missing.");
        var actual = Inventory(_fileSystem, root);
        if (!_directories.SequenceEqual(Directories(_fileSystem, root), StringComparer.Ordinal)
            || actual.Count != _files.Count || _files.Any(entry => !actual.TryGetValue(entry.Key, out var hash) || hash != entry.Value))
            throw new IOException("Gateway recovery snapshot is incomplete or modified.");
    }

    private void CopyTree(string source, string destination)
    {
        _fileSystem.Directory.CreateDirectory(destination);
        foreach (var relative in _directories)
            _fileSystem.Directory.CreateDirectory(_fileSystem.Path.Combine(destination, relative));
        foreach (var relative in _files.Keys)
        {
            var sourceFile = _fileSystem.Path.Combine(source, relative);
            var destinationFile = _fileSystem.Path.Combine(destination, relative);
            _fileSystem.File.Copy(sourceFile, destinationFile);
            // The native apphost must remain executable after recovery on Unix.
            SecureFilePermissions.CopyUnixFileMode(_fileSystem, sourceFile, destinationFile);
        }
    }

    private async Task<Dictionary<string, string>> InventoryAsync(string root, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relative in Directories(_fileSystem, root, cancellationToken).Prepend(""))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var file in _fileSystem.Directory.GetFiles(_fileSystem.Path.Combine(root, relative)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectLink(_fileSystem, file);
                await using var stream = _fileSystem.File.OpenRead(file);
                var fingerprint = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();
                if (!OperatingSystem.IsWindows())
                    fingerprint += ":" + _fileSystem.File.GetUnixFileMode(file);
                result.Add(_fileSystem.Path.GetRelativePath(root, file), fingerprint);
            }
        }
        return result;
    }

    private async Task VerifyAsync(string root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_fileSystem.Directory.Exists(root))
            throw new IOException("Preserved gateway output directory is missing.");
        var actual = await InventoryAsync(root, cancellationToken);
        if (!_directories.SequenceEqual(Directories(_fileSystem, root, cancellationToken), StringComparer.Ordinal)
            || actual.Count != _files.Count)
            throw new IOException("Gateway recovery snapshot is incomplete or modified.");
        foreach (var entry in _files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!actual.TryGetValue(entry.Key, out var hash) || hash != entry.Value)
                throw new IOException("Gateway recovery snapshot is incomplete or modified.");
        }
    }

    private async Task CopyTreeAsync(string source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _fileSystem.Directory.CreateDirectory(destination);
        foreach (var relative in _directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _fileSystem.Directory.CreateDirectory(_fileSystem.Path.Combine(destination, relative));
        }
        foreach (var relative in _files.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceFile = _fileSystem.Path.Combine(source, relative);
            var destinationFile = _fileSystem.Path.Combine(destination, relative);
            await using var input = _fileSystem.File.OpenRead(sourceFile);
            cancellationToken.ThrowIfCancellationRequested();
            await using var output = _fileSystem.File.Open(destinationFile, FileMode.CreateNew, FileAccess.Write);
            await input.CopyToAsync(output, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            SecureFilePermissions.CopyUnixFileMode(_fileSystem, sourceFile, destinationFile);
        }
    }

    public void Dispose()
    {
        if (_retain)
            return;
        try
        {
            if (_fileSystem.Directory.Exists(PreservedDirectory))
                _fileSystem.Directory.Delete(PreservedDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup must not replace the build's exit code or prevent normal candidate startup.
        }
    }
}
