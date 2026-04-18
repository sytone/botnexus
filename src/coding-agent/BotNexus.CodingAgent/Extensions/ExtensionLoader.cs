using System.IO.Abstractions;
using System.Reflection;
using System.Runtime.Loader;
using BotNexus.Agent.Core.Tools;

namespace BotNexus.CodingAgent.Extensions;

/// <summary>
/// Loads coding-agent extensions from assembly files.
/// </summary>
public sealed class ExtensionLoader
{
    private readonly IFileSystem _fileSystem;

    public ExtensionLoader(IFileSystem? fileSystem = null)
    {
        _fileSystem = fileSystem ?? new FileSystem();
    }

    public ExtensionLoadResult LoadExtensions(string extensionsDirectory)
    {
        if (string.IsNullOrWhiteSpace(extensionsDirectory) || !_fileSystem.Directory.Exists(extensionsDirectory))
        {
            return new ExtensionLoadResult([], []);
        }

        var extensions = new List<IExtension>();
        var tools = new List<IAgentTool>();
        foreach (var dllPath in _fileSystem.Directory.EnumerateFiles(extensionsDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(dllPath));
                var extensionTypes = assembly
                    .GetTypes()
                    .Where(type =>
                        typeof(IExtension).IsAssignableFrom(type) &&
                        type is { IsAbstract: false, IsInterface: false } &&
                        type.GetConstructor(Type.EmptyTypes) is not null)
                    .ToList();

                foreach (var extensionType in extensionTypes)
                {
                    try
                    {
                        var extension = (IExtension)Activator.CreateInstance(extensionType)!;
                        extensions.Add(extension);
                        var extensionTools = extension.GetTools();
                        tools.AddRange(extensionTools);
                        Console.WriteLine($"Loaded {extensionTools.Count} tools from extension {extension.Name}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Failed to initialize extension type '{extensionType.FullName}': {ex.Message}");
                    }
                }
            }
            catch (ReflectionTypeLoadException ex)
            {
                var details = string.Join("; ", ex.LoaderExceptions.Where(item => item is not null).Select(item => item!.Message));
                Console.Error.WriteLine($"Failed to inspect extension assembly '{dllPath}': {details}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to load extension assembly '{dllPath}': {ex.Message}");
            }
        }

        return new ExtensionLoadResult(extensions, tools);
    }
}

public sealed record ExtensionLoadResult(
    IReadOnlyList<IExtension> Extensions,
    IReadOnlyList<IAgentTool> Tools);
