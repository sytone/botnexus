using System.Text.Json;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.Skills;

/// <summary>
/// The single JSON binding seam for everything the Skills extension reads out of an agent's
/// <see cref="AgentDescriptor.ExtensionConfig"/> (and out of its own request bodies).
/// </summary>
/// <remarks>
/// <para>
/// #3495: three separate call sites - <see cref="SkillsToolContributor"/>,
/// <see cref="SkillManagerToolContributor"/> and <see cref="SkillPromptHookHandler"/> - each
/// hand-rolled <c>JsonSerializer.Deserialize&lt;T&gt;(element.GetRawText())</c> with no options.
/// <c>System.Text.Json</c> is case-sensitive by default, so the documented camelCase key
/// <c>allowSharedSkillManagement</c> never bound to <see cref="SkillsConfig.AllowSharedSkillManagement"/>
/// and the property silently kept its <c>false</c> default. The descriptor API reported the flag
/// as <c>true</c> while the write gate read <c>false</c>, so every <c>skill_manage scope=shared</c>
/// write was refused with a message asserting a value the operator could see was different.
/// </para>
/// <para>
/// The fix is deliberately a shared instance rather than three identical literals: identical
/// literals are what let the defect exist in triplicate in the first place, and nothing would stop
/// a fourth reader from omitting the options again. <c>SkillsExtensionJsonFenceArchitectureTests</c>
/// fails the build if a Skills-extension deserialization is added that does not pass options.
/// </para>
/// <para>
/// <see cref="JsonSerializerOptions"/> is thread-safe once used, so one static instance is both
/// correct and the documented way to avoid re-creating (and re-caching) metadata per call.
/// </para>
/// </remarks>
public static class SkillsExtensionJson
{
    /// <summary>
    /// The extension id under which per-agent skills configuration is stored in
    /// <see cref="AgentDescriptor.ExtensionConfig"/>.
    /// </summary>
    public const string ExtensionId = "botnexus-skills";

    /// <summary>
    /// Human-readable name of the configuration source the Skills extension binds from. Used in
    /// operator-facing gate messages so a refusal names WHERE it read, not just what it read
    /// (#3495 acceptance criterion 4).
    /// </summary>
    public const string ConfigSourceDescription = $"agent descriptor extensionConfig[\"{ExtensionId}\"]";

    /// <summary>
    /// The one options instance every Skills-extension deserialization must use.
    /// Case-insensitive so camelCase config keys bind to PascalCase properties.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// <see cref="Options"/> plus indented output, for the on-disk artefacts the extension WRITES
    /// as well as reads (currently the skill trust catalog). Declared here rather than beside its
    /// consumer so the extension has exactly one place that decides JSON policy - the architecture
    /// fence for #3495 enforces that.
    /// </summary>
    public static readonly JsonSerializerOptions IndentedOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>
    /// Binds <paramref name="element"/> to <typeparamref name="T"/> using <see cref="Options"/>,
    /// returning <c>null</c> rather than throwing when the operator's config is malformed - a bad
    /// config block must not take the agent's whole tool contribution down with it.
    /// </summary>
    public static T? Bind<T>(JsonElement element) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(element.GetRawText(), Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads <paramref name="extensionId"/>'s config block off <paramref name="descriptor"/>,
    /// or <c>null</c> when the agent has no such block.
    /// </summary>
    public static T? ResolveExtensionConfig<T>(AgentDescriptor descriptor, string extensionId)
        where T : class
        => descriptor.ExtensionConfig.TryGetValue(extensionId, out var element)
            ? Bind<T>(element)
            : null;

    /// <summary>
    /// Reads this extension's own <see cref="SkillsConfig"/> off an agent descriptor.
    /// </summary>
    public static SkillsConfig? ResolveSkillsConfig(AgentDescriptor descriptor)
        => ResolveExtensionConfig<SkillsConfig>(descriptor, ExtensionId);
}
