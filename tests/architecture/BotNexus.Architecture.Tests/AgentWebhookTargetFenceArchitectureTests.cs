using System.Xml.Linq;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// #3523 acceptance criterion 7, as an executable fence rather than a review instruction.
///
/// <para>
/// The agent webhook provisioner lives in core (<c>BotNexus.Gateway.Webhooks</c>) and knows only
/// the product-agnostic <c>IAgentWebhookTargetNotifier</c> seam declared in
/// <c>BotNexus.Gateway.Contracts</c>. The knowledge that a downstream product called TaskNexus
/// exists lives entirely in <c>src/extensions/BotNexus.Extensions.TaskNexus</c>.
/// </para>
///
/// <para>
/// <b>Why a test and not a convention.</b> The direction of this dependency is invisible at the
/// point where it would be violated: adding a <c>ProjectReference</c> from the webhooks project to
/// the extension compiles cleanly, passes every behavioural test, and produces a working gateway.
/// The only symptom is that core has silently taken an outbound dependency on one downstream
/// consumer, which is exactly what <c>src/gateway/AGENTS.md</c> prohibits and exactly the kind of
/// edge a reviewer skims past. The build cannot catch it, so this does.
/// </para>
///
/// <para>
/// This is deliberately narrower than a blanket gateway-to-extensions ban - that rule is stated in
/// <c>src/gateway/AGENTS.md</c> and pinned separately. What is pinned here is the specific pair the
/// #3523 design turns on, plus the symmetric fact that the contract the extension implements is
/// genuinely in core and product-agnostic.
/// </para>
/// </summary>
public sealed class AgentWebhookTargetFenceArchitectureTests : ArchitectureTest
{
    private const string TaskNexusAssembly = "BotNexus.Extensions.TaskNexus";
    private const string TaskNexusName = "TaskNexus";

    [Fact]
    public void GatewayWebhooks_HasNoReferenceToTheTaskNexusExtension()
    {
        var projectPath = Repository.Path(
            "src", "gateway", "BotNexus.Gateway.Webhooks", "BotNexus.Gateway.Webhooks.csproj");
        File.Exists(projectPath).ShouldBeTrue($"Expected the webhooks project at {projectPath}.");

        var references = ReferenceNames(projectPath);

        references.ShouldNotContain(
            TaskNexusAssembly,
            "BotNexus.Gateway.Webhooks must not reference the TaskNexus extension. The provisioner " +
            "talks to IAgentWebhookTargetNotifier (BotNexus.Gateway.Contracts); the TaskNexus " +
            "delivery implementation is discovered at runtime by the extension loader. See #3523 AC7.");
    }

    [Fact]
    public void NoGatewayOrDomainProject_ReferencesTheTaskNexusExtension()
    {
        var fencedAreas = new[] { "gateway", "domain", "agent", "persistence" }
            .Select(area => Path.Combine(Repository.SourceRoot, area))
            .Where(Directory.Exists);

        var violations = fencedAreas
            .SelectMany(area => Directory.EnumerateFiles(area, "*.csproj", SearchOption.AllDirectories))
            .Where(project => !IsBuildOutput(project))
            .Where(project => ReferenceNames(project).Contains(TaskNexusAssembly))
            .Select(project => Path.GetRelativePath(Repository.Root, project))
            .ToArray();

        violations.ShouldBeEmpty(
            "Core must not take an outbound dependency on one downstream product. Move the " +
            "TaskNexus-specific behaviour behind IAgentWebhookTargetNotifier instead. Violations: " +
            string.Join(", ", violations));
    }

    [Fact]
    public void TaskNexusExtension_LivesUnderSrcExtensionsAndImplementsTheCoreContract()
    {
        var extensionProject = Repository.Path(
            "src", "extensions", TaskNexusAssembly, TaskNexusAssembly + ".csproj");

        File.Exists(extensionProject).ShouldBeTrue(
            $"The TaskNexus delivery target must live under src/extensions/. Expected {extensionProject}.");

        // The extension depends on core, never the reverse. Contracts is where the seam lives.
        ReferenceNames(extensionProject).ShouldContain(
            "BotNexus.Gateway.Contracts",
            "The TaskNexus extension implements IAgentWebhookTargetNotifier, which is declared in " +
            "BotNexus.Gateway.Contracts.");
    }

    [Fact]
    public void AgentWebhookTargetContract_IsDeclaredInCoreAndNamesNoDownstreamProduct()
    {
        var contractPath = Repository.Path(
            "src", "gateway", "BotNexus.Gateway.Contracts", "Webhooks", "IAgentWebhookTargetNotifier.cs");
        File.Exists(contractPath).ShouldBeTrue($"Expected the delivery contract at {contractPath}.");

        File.ReadAllText(contractPath)
            .Contains(TaskNexusName, StringComparison.OrdinalIgnoreCase)
            .ShouldBeFalse(
                "The delivery contract is product-agnostic by design; naming a downstream product " +
                "here reintroduces in documentation the coupling the assembly boundary removes.");
    }

    [Fact]
    public void AgentWebhookProvisioner_NamesNoDownstreamProductInItsIdempotencyKey()
    {
        var provisionerPath = Repository.Path(
            "src", "gateway", "BotNexus.Gateway.Webhooks", "AgentWebhookProvisioner.cs");
        File.Exists(provisionerPath).ShouldBeTrue($"Expected the provisioner at {provisionerPath}.");

        // The label is persisted, so a product name baked into it would outlive any later
        // refactor - a stored key is far harder to walk back than a class name.
        File.ReadAllText(provisionerPath)
            .Contains("LabelPrefix = \"agent-webhook:\"", StringComparison.Ordinal)
            .ShouldBeTrue(
                "The provisioner's registration label must be product-agnostic and agent-id keyed.");
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    /// <summary>
    /// Bare assembly/project names for every ProjectReference and PackageReference in a csproj.
    /// </summary>
    private static IReadOnlyCollection<string> ReferenceNames(string projectPath)
    {
        var document = XDocument.Load(projectPath);

        return document.Descendants()
            .Where(element =>
                element.Name.LocalName is "ProjectReference" or "PackageReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', '/')))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
