using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using Moq;

namespace BotNexus.Extensions.Qmd.Tests;

/// <summary>
/// Guards the QMD default-off contract (#2116 / PR #2274) now that the extension actually loads
/// (#2365). Before #2365 the manifest lacked <c>entryAssembly</c>/<c>extensionTypes</c>, so the
/// gateway skipped the extension entirely and the fail-closed path never executed in production.
/// Fixing the manifest makes the contributor run for every agent, so default-off must hold on the
/// shipped manifest as it exists on disk.
/// </summary>
public sealed class QmdManifestDefaultOffTests
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The shipped manifest must be loadable: it declares an entry assembly and a tool type.</summary>
    [Fact]
    public void ShippedManifest_DeclaresEntryAssemblyAndToolType()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ManifestPath));
        var root = document.RootElement;

        Assert.Equal("botnexus-qmd", root.GetProperty("id").GetString());
        Assert.Equal("BotNexus.Extensions.Qmd.dll", root.GetProperty("entryAssembly").GetString());

        var types = root.GetProperty("extensionTypes").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("tool", types);
    }

    /// <summary>
    /// The manifest must not claim to control enablement. <c>ExtensionManifest</c> has no
    /// <c>optional</c> or <c>enabledByDefault</c> member, so those keys were inert; and
    /// <c>enabled:false</c> would suppress loading altogether rather than express per-agent opt-in.
    /// Default-off is owned by <see cref="QmdToolContributor"/>, not the manifest.
    /// </summary>
    [Fact]
    public void ShippedManifest_DoesNotEncodeEnablementPolicy()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ManifestPath));
        var keys = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();

        Assert.DoesNotContain("optional", keys);
        Assert.DoesNotContain("enabledByDefault", keys);
        Assert.DoesNotContain("enabled", keys);
    }

    /// <summary>
    /// The real behavioural guard: with the extension loaded and no <c>botnexus-qmd</c> agent
    /// config, the contributor must contribute zero tools and allocate no backend resources.
    /// </summary>
    [Fact]
    public async Task LoadedExtension_WithoutAgentOptIn_ContributesNoTools()
    {
        var descriptor = new AgentDescriptor
        {
            AgentId = AgentId.From("no-optin-agent"),
            DisplayName = "No Opt-In Agent",
            ModelId = "test-model",
            ApiProvider = "test-provider",
            ExtensionConfig = new Dictionary<string, JsonElement>()
        };

        var context = new AgentToolContributionContext(
            descriptor,
            new AgentExecutionContext { SessionId = SessionId.From("sess-no-optin") },
            "/tmp/workspace",
            Mock.Of<BotNexus.Gateway.Abstractions.Security.IPathValidator>(),
            null,
            (_, _) => Task.FromResult<string?>(null));

        var contribution = await new QmdToolContributor().ContributeAsync(context);

        Assert.Empty(contribution.Tools);
        Assert.True(contribution.ResourcesToDispose is null || contribution.ResourcesToDispose.Count == 0);
    }

    private static string ManifestPath => Path.Combine(
        RepoRoot, "src", "extensions", "BotNexus.Extensions.Qmd", "botnexus-extension.json");

    private static string RepoRoot
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
            {
                current = current.Parent;
            }

            Assert.NotNull(current);
            return current!.FullName;
        }
    }
}
