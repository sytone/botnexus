using System.Text.Json;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// The single sanctioned way to bind an entry out of <see cref="AgentDescriptor.ExtensionConfig"/>
/// into a typed configuration POCO.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AgentDescriptor.ExtensionConfig"/> holds <em>raw, unbound</em> JSON exactly as it was
/// written to the configuration store, which is camelCase. Extension config POCOs use PascalCase
/// properties. <see cref="JsonSerializer"/>'s default options are case-<em>sensitive</em>, so
/// deserialising a raw element with no options binds <em>nothing</em>: every property silently
/// falls back to its C# default and the operator receives no error (#3492).
/// </para>
/// <para>
/// That failure is invisible wherever the configured value happens to equal the C# default, which
/// is why it survived for months - only <c>allowSharedSkillManagement</c>, defaulting to
/// <see langword="false"/>, was ever noticed. The rest of the configuration stack already binds
/// case-insensitively; the options were being dropped precisely at the extension boundary.
/// </para>
/// <para>
/// Binding lives here, next to the property it reads, rather than in each extension, because eight
/// separate copies of a private <c>ResolveExtensionConfig&lt;T&gt;</c> drifted into six different
/// behaviours. A single seam means a ninth extension cannot reintroduce the defect, and
/// <c>ExtensionConfigBindingArchitectureTests</c> fails the build if one tries.
/// </para>
/// </remarks>
public static class ExtensionConfigBinder
{
    /// <summary>
    /// Options matching the rest of the configuration stack: camelCase in the file binds to
    /// PascalCase on the POCO.
    /// </summary>
    /// <remarks>
    /// <see cref="JsonSerializerDefaults.Web"/> supplies
    /// <c>PropertyNameCaseInsensitive = true</c> plus camelCase naming, which is the same contract
    /// the configuration loader and the platform writer use. Stating it as a shared static also
    /// means the fence has a single symbol to assert against.
    /// </remarks>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Binds <paramref name="extensionId"/>'s configuration from <paramref name="descriptor"/>, or
    /// returns <see langword="null"/> when the extension has no configured entry.
    /// </summary>
    /// <typeparam name="T">The extension's configuration type.</typeparam>
    /// <param name="descriptor">The agent whose configuration is being read. May be null.</param>
    /// <param name="extensionId">The extension ID key, for example <c>botnexus-skills</c>.</param>
    /// <returns>
    /// The bound configuration, or <see langword="null"/> when the key is absent, the value is JSON
    /// null, or the entry is malformed. Callers substitute their own defaults, which keeps the
    /// "absent" and "unparseable" paths identical to the behaviour each extension had before.
    /// </returns>
    public static T? Bind<T>(AgentDescriptor? descriptor, string extensionId)
        where T : class
    {
        if (descriptor?.ExtensionConfig is null)
        {
            return null;
        }

        if (!descriptor.ExtensionConfig.TryGetValue(extensionId, out var element))
        {
            return null;
        }

        return Bind<T>(element);
    }

    /// <summary>
    /// Binds an already-retrieved <see cref="JsonElement"/> from the extension config bag.
    /// </summary>
    /// <typeparam name="T">The extension's configuration type.</typeparam>
    /// <param name="element">The raw configuration element.</param>
    /// <returns>The bound configuration, or <see langword="null"/> when null or malformed.</returns>
    /// <remarks>
    /// Malformed configuration returns <see langword="null"/> rather than throwing: a bad entry for
    /// one extension must not prevent an agent from starting. The caller decides whether to fall
    /// back to defaults or to disable itself.
    /// </remarks>
    public static T? Bind<T>(JsonElement element)
        where T : class
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(element.GetRawText(), Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
