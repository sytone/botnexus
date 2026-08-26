using System.Text;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Reads platform configuration from the SQLite store through the framework's own configuration
/// pipeline (#3485 D1), so every consumer sees store values via <c>IOptions</c> with no bespoke
/// read path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this replaces a seam rather than joining one.</b> The previous cutover
/// (<c>IConfigDocumentSource</c> / <c>ConfigStoreStartupLoader</c>) redirected exactly one read - the
/// startup load - while the 33 <c>IOptionsMonitor&lt;PlatformConfig&gt;</c> call sites continued
/// reading the file. A provider has no such split: registering it puts the store in the same pipeline
/// every consumer already resolves through, and precedence becomes registration order rather than an
/// <c>if (authoritative)</c> branch.
/// </para>
/// <para>
/// <b>Keys come from the framework parser, not from the store's dotted paths.</b> The store flattens
/// to <c>a.b.c</c> and deliberately does not descend into arrays (an array's identity is its whole
/// serialised value - see <see cref="Shadow.ConfigDocumentFlattener"/>). .NET configuration expects
/// <c>a:b:c</c> and <em>does</em> index arrays as <c>a:b:0</c>. Translating dots to colons by hand
/// would therefore produce a key space subtly different from <c>AddJsonFile</c> over the same
/// document - arrays would arrive as one opaque JSON string instead of indexed children, and binding
/// a <c>List&lt;T&gt;</c> would silently yield an empty list. So the entries are rehydrated to a
/// document and handed to the framework's own JSON parser: the key semantics are then identical to
/// the JSON provider by construction rather than by inspection.
/// </para>
/// <para>
/// <b>Fail-safe reload, matching #2358.</b> A store that is missing, unreadable, or corrupt retains
/// the last-known-good <see cref="ConfigurationProvider.Data"/> instead of clearing it and throwing.
/// The framework's <c>FileConfigurationProvider</c> clears <c>Data</c> and rethrows on a reload
/// failure - on a background thread, which terminates the process - and that hazard is identical
/// here.
/// </para>
/// </remarks>
public sealed class SqliteConfigurationProvider : ConfigurationProvider
{
    private readonly IConfigStore _store;
    private readonly Action<string, Exception?>? _onLoadFailure;

    /// <summary>Creates a provider over <paramref name="store"/>.</summary>
    /// <param name="store">The configuration store to read.</param>
    /// <param name="onLoadFailure">
    /// Invoked with a human-readable reason when a load is rejected and the previous data retained.
    /// </param>
    public SqliteConfigurationProvider(IConfigStore store, Action<string, Exception?>? onLoadFailure = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _onLoadFailure = onLoadFailure;
    }

    /// <summary>
    /// Rejects writes made through <see cref="IConfiguration"/>, naming the sanctioned write seam.
    /// </summary>
    /// <remarks>
    /// <c>ConfigurationRoot.SetConfiguration</c> loops over <em>every</em> registered provider rather
    /// than the winning one, so a single <c>config["a:b"] = "x"</c> would commit through the JSON
    /// provider and this one at once - and through env-var and command-line providers, which accept
    /// the write into their dictionary and silently discard it. Reads are last-wins; writes fan out.
    /// A persisting <c>Set</c> would therefore turn one assignment into N durable side effects with
    /// no error. The signature cannot express the alternative either: it is synchronous with no
    /// cancellation and no result, so a concurrency conflict has nowhere to surface, and
    /// <c>Set(key, null)</c> stores a null rather than removing a key, so delete is inexpressible.
    /// Throwing converts a silent multi-store commit into a loud failure at the call site.
    /// </remarks>
    public override void Set(string key, string? value)
        => throw new NotSupportedException(
            $"Configuration is not writable through IConfiguration (attempted to set '{key}'). " +
            "Writes go through the configuration write seam, which is async, expresses delete, and " +
            "targets a chosen store. Assigning here would fan the write out to every registered " +
            "provider.");

    /// <inheritdoc />
    public override void Load()
    {
        // Synchronous by contract. Safe to block: Load runs either during host construction (nothing
        // else executing) or from a change-token callback on a background thread with no
        // synchronization context to deadlock against.
        var previous = Data;

        IReadOnlyDictionary<string, ConfigEntry> entries;
        try
        {
            entries = _store.ReadEntriesAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Retain last-known-good rather than clearing and rethrowing (#2358). On the reload path
            // a throw here reaches a background thread and takes the process down.
            _onLoadFailure?.Invoke("Configuration store could not be read; retaining previous values.", ex);
            Data = previous;
            return;
        }

        try
        {
            var document = ConfigDocumentRehydrator.Rehydrate(entries);
            Data = Parse(document);
        }
        catch (Exception ex)
        {
            _onLoadFailure?.Invoke(
                "Configuration store contents could not be materialised; retaining previous values.", ex);
            Data = previous;
        }
    }

    /// <summary>
    /// Produces provider data with key semantics identical to <c>AddJsonFile</c> over the same
    /// document, by running the framework's own <see cref="JsonConfigurationProvider"/> over the
    /// rehydrated document rather than reimplementing the dotted-to-colon and array-indexing rules.
    /// </summary>
    /// <remarks>
    /// The framework's parser type is <c>internal</c>, so the supported way to reach it is to drive a
    /// real <see cref="JsonConfigurationProvider"/> over a stream. That is a deliberate choice over a
    /// hand-written flattener: any divergence in array indexing, escaping, or case handling between
    /// this provider and <c>AddJsonFile</c> would surface as a binding difference that only appears
    /// for one storage backend, which is precisely the class of defect this work exists to remove.
    /// </remarks>
    private static IDictionary<string, string?> Parse(JsonObject document)
    {
        var bytes = Encoding.UTF8.GetBytes(document.ToJsonString());
        var source = new JsonStreamConfigurationSource { Stream = new MemoryStream(bytes) };
        var provider = (JsonStreamConfigurationProvider)source.Build(new ConfigurationBuilder());
        provider.Load();

        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Collect(provider, parentPath: null, data);
        return data;
    }

    /// <summary>
    /// Walks a provider's key space via <see cref="IConfigurationProvider.GetChildKeys"/>, which is
    /// the only public way to enumerate what a provider loaded.
    /// </summary>
    private static void Collect(IConfigurationProvider provider, string? parentPath, Dictionary<string, string?> sink)
    {
        foreach (var child in provider.GetChildKeys([], parentPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = parentPath is null ? child : ConfigurationPath.Combine(parentPath, child);

            if (provider.TryGet(path, out var value))
            {
                sink[path] = value;
            }

            Collect(provider, path, sink);
        }
    }

    /// <summary>
    /// Signals that the store changed, so <c>IOptionsMonitor</c> consumers re-bind. This is the whole
    /// hot-reload mechanism: the write seam calls it after a commit, and the framework's existing
    /// change-token plumbing does the rest.
    /// </summary>
    public void NotifyChanged()
    {
        Load();
        OnReload();
    }
}

