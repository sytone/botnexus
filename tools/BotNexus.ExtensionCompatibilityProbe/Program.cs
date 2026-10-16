using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;

// This is a gateway-free initialization harness, not a gateway entry point. Run with dotnet exec
// and the production gateway deps/runtimeconfig, never a testhost's merged NuGet closure.
var extensionPath = Path.GetFullPath(args.Single());
var hostDirectory = AppContext.BaseDirectory;
var gateway = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(hostDirectory, "BotNexus.Gateway.dll"));
var loader = gateway.GetType("BotNexus.Gateway.Extensions.ExtensionAssemblyLoadContext", throwOnError: true)
    ?? throw new InvalidOperationException("Production extension loader missing.");
var context = Activator.CreateInstance(loader, [extensionPath, true]) as AssemblyLoadContext
    ?? throw new InvalidOperationException("Could not construct production loader.");
var network = new RejectNetworkHandler();
using var services = new ServiceCollection().AddLogging().AddSingleton<IHttpClientFactory>(new OfflineClientFactory(network)).BuildServiceProvider();
try
{
    var entry = context.LoadFromAssemblyPath(extensionPath);
    Require(AssemblyLoadContext.GetLoadContext(entry) == context, "Extension must load privately.");
    var sdkPath = Path.Combine(Path.GetDirectoryName(extensionPath) ?? throw new InvalidOperationException("Extension directory missing."), "Microsoft.Agents.Authentication.Msal.dll");
    var sdk = context.LoadFromAssemblyName(AssemblyName.GetAssemblyName(sdkPath));
    Require(AssemblyLoadContext.GetLoadContext(sdk) == context, "Agents SDK must load privately.");
    var settingsType = sdk.GetType("Microsoft.Agents.Authentication.Msal.Model.ConnectionSettings", throwOnError: true)
        ?? throw new InvalidOperationException("SDK connection settings missing.");
    var settings = Activator.CreateInstance(settingsType) ?? throw new InvalidOperationException("Settings construction failed.");
    // Deliberately fake values. No credential, token acquisition, certificate store or cloud request.
    Set(settings, "ClientId", "11111111-1111-1111-1111-111111111111");
    Set(settings, "TenantId", "22222222-2222-2222-2222-222222222222");
    Set(settings, "ClientSecret", "not-a-credential");
    Invoke(settings, "ValidateConfiguration");
    var authType = sdk.GetType("Microsoft.Agents.Authentication.Msal.MsalAuth", throwOnError: true)
        ?? throw new InvalidOperationException("SDK MSAL auth missing.");
    var auth = Activator.CreateInstance(authType, services, settings) ?? throw new InvalidOperationException("SDK auth construction failed.");
    var app = Invoke(auth, "CreateClientApplication") ?? throw new InvalidOperationException("MSAL application not created.");
    var credential = Invoke(auth, "GetTokenCredential") ?? throw new InvalidOperationException("Token credential not created.");
    Require(app.GetType().Assembly.GetName().Name == "Microsoft.Identity.Client", "SDK must initialize a real MSAL application.");
    Require(credential.GetType().BaseType?.Assembly.GetName().Name == "Azure.Core", "SDK must initialize a real Azure token credential.");
    foreach (var name in new[] { "Microsoft.Identity.Client", "Azure.Core", "System.Memory.Data", "System.ClientModel", "Microsoft.IdentityModel.Abstractions" })
    {
        var hostPath = Path.Combine(hostDirectory, name + ".dll");
        var host = AssemblyLoadContext.Default.LoadFromAssemblyPath(hostPath);
        var bound = context.LoadFromAssemblyName(AssemblyName.GetAssemblyName(hostPath));
        Require(ReferenceEquals(bound, host), name + " must bind to the host, not a private duplicate.");
        Require(bound.ManifestModule.ModuleVersionId == ReadMvid(hostPath), name + " must match production artifact bytes.");
    }
    var hostMsal = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(hostDirectory, "Microsoft.Identity.Client.dll"));
    var hostCore = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(hostDirectory, "Azure.Core.dll"));
    Require(ReferenceEquals(app.GetType().Assembly, hostMsal), "SDK-created MSAL application must use the host assembly.");
    Require(ReferenceEquals(credential.GetType().BaseType, hostCore.GetType("Azure.Core.TokenCredential", throwOnError: true)),
        "SDK-created credential must derive from the host TokenCredential type.");
    Require(network.RequestCount == 0, "Initialization must not use network.");
    Console.WriteLine("Agent365 SDK auth initialized; host identities verified; network requests: 0");
}
finally
{
    context.Unload();
}

static void Require(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}
static void Set(object target, string property, object value) =>
    (target.GetType().GetProperty(property) ?? throw new MissingMemberException(property)).SetValue(target, value);
static object? Invoke(object target, string method) =>
    (target.GetType().GetMethod(method, Type.EmptyTypes) ?? throw new MissingMethodException(method)).Invoke(target, null);
static Guid ReadMvid(string path)
{
    using var stream = File.OpenRead(path);
    using var reader = new System.Reflection.PortableExecutable.PEReader(stream);
    var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(reader);
    return metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
}

sealed class OfflineClientFactory(RejectNetworkHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
sealed class RejectNetworkHandler : HttpMessageHandler
{
    public int RequestCount { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        throw new InvalidOperationException("Network is forbidden in the initialization probe: " + request.RequestUri);
    }
}
