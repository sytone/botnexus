using System.Reflection;
using System.Runtime.Loader;

// No GatewayHost/Program entry point is invoked. Constructing clients does not start a
// processor, send a message, or request a token. All endpoints/keys below are dummy inputs.
try
{
    if (args.Length != 3)
        throw new ArgumentException("Expected host assembly, extension assembly, and authentication mode.");
    var hostPath = Path.GetFullPath(args[0]);
    var extensionPath = Path.GetFullPath(args[1]);
    var hostDirectory = Path.GetDirectoryName(hostPath) ?? throw new InvalidOperationException("No host directory.");
    Require(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) == Path.TrimEndingDirectorySeparator(hostDirectory),
        $"Probe base directory {AppContext.BaseDirectory} is not the actual host shipping directory {hostDirectory}.");
    Require(!File.Exists(Path.Combine(hostDirectory, "Azure.Messaging.ServiceBus.dll")), "ServiceBus must be extension-private.");
    Require(File.Exists(Path.Combine(Path.GetDirectoryName(extensionPath) ?? "", "Azure.Core.dll")), "Missing extension-private Azure.Core copy.");

    var gateway = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(hostDirectory, "BotNexus.Gateway.dll"));
    var loaderType = gateway.GetType("BotNexus.Gateway.Extensions.ExtensionAssemblyLoadContext", throwOnError: true)
        ?? throw new InvalidOperationException("Missing production extension loader.");
    var context = Activator.CreateInstance(loaderType, [extensionPath, true]) as AssemblyLoadContext
        ?? throw new InvalidOperationException("Cannot construct production extension loader.");
    try
    {
        var extension = context.LoadFromAssemblyPath(extensionPath);
        Require(AssemblyLoadContext.GetLoadContext(extension) == context, "Entry assembly must execute in the extension context.");
        var adapterType = extension.GetType("BotNexus.Extensions.Channels.ServiceBus.ServiceBusChannelAdapter", throwOnError: true)
            ?? throw new InvalidOperationException("Missing adapter.");
        var optionsType = extension.GetType("BotNexus.Extensions.Channels.ServiceBus.ServiceBusChannelOptions", throwOnError: true)
            ?? throw new InvalidOperationException("Missing options.");
        var options = Activator.CreateInstance(optionsType) ?? throw new InvalidOperationException("Cannot construct options.");
        var (property, value) = args[2] switch
        {
            "connection-string" => ("ConnectionString", "Endpoint=sb://bn4778.invalid/;SharedAccessKeyName=probe;SharedAccessKey=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="),
            "managed-identity" => ("FullyQualifiedNamespace", "bn4778.invalid"),
            _ => throw new ArgumentException("Unknown authentication mode.")
        };
        var optionProperty = optionsType.GetProperty(property) ?? throw new InvalidOperationException("Missing auth option.");
        optionProperty.SetValue(options, value);

        // Resolve DI contracts from the HOST, not from a compiled reference or test helper.
        var logging = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("Microsoft.Extensions.Logging.Abstractions"));
        var loggerType = (logging.GetType("Microsoft.Extensions.Logging.Abstractions.NullLogger`1", true)
            ?? throw new InvalidOperationException("Missing NullLogger.")).MakeGenericType(adapterType);
        var logger = loggerType.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) ?? throw new InvalidOperationException("Missing NullLogger.Instance.");
        var optionsAssembly = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("Microsoft.Extensions.Options"));
        var wrapperType = (optionsAssembly.GetType("Microsoft.Extensions.Options.OptionsWrapper`1", true)
            ?? throw new InvalidOperationException("Missing OptionsWrapper.")).MakeGenericType(optionsType);
        var wrapper = Activator.CreateInstance(wrapperType, [options]) ?? throw new InvalidOperationException("Cannot wrap options.");
        var adapter = Activator.CreateInstance(adapterType, [logger, wrapper, null, null])
            ?? throw new InvalidOperationException("Cannot construct adapter without an injected factory.");
        var createFactory = adapterType.GetMethod("CreateDefaultFactory", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing production factory method.");
        var factory = createFactory.Invoke(adapter, null) as IAsyncDisposable
            ?? throw new InvalidOperationException("Production factory is not asynchronously disposable.");
        await using (factory)
        {
            Require(factory.GetType().FullName == "BotNexus.Extensions.Channels.ServiceBus.DefaultServiceBusAdapterClientFactory",
                "Expected the actual production factory, not a substitute.");
            var serviceBus = context.Assemblies.Single(assembly => assembly.GetName().Name == "Azure.Messaging.ServiceBus");
            Require(AssemblyLoadContext.GetLoadContext(serviceBus) == context, "ServiceBus must remain extension-private.");
            var coreReference = serviceBus.GetReferencedAssemblies().Single(assembly => assembly.Name == "Azure.Core");
            var core = context.LoadFromAssemblyName(coreReference);
            Require(AssemblyLoadContext.GetLoadContext(core) == AssemblyLoadContext.Default, "Azure.Core must unify with the host.");
            Require(Path.GetFullPath(core.Location) == Path.Combine(hostDirectory, "Azure.Core.dll"), "Azure.Core came from outside the host artifact.");
            Require(core.ManifestModule.ModuleVersionId == ReadMvid(Path.Combine(hostDirectory, "Azure.Core.dll")), "Host Azure.Core content differs.");
            foreach (var assembly in AssemblyLoadContext.Default.Assemblies.Where(assembly => !assembly.IsDynamic))
                Require(!assembly.GetName().Name.AsSpan().StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
                    && assembly.GetName().Name != "Shouldly", "Testhost dependencies leaked into the isolated probe.");
        }
        Console.WriteLine($"FACTORY_OK {args[2]}; host={hostDirectory}; loader={loaderType.FullName}; no processor/send/token call");
    }
    finally
    {
        context.Unload();
    }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static Guid ReadMvid(string path)
{
    using var stream = File.OpenRead(path);
    using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
    var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
    return metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
}
