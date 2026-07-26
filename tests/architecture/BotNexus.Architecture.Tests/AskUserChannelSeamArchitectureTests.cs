using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Services;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Architecture fence pinning the channel-agnostic <c>ask_user</c> gateway seam (#2322).
///
/// <para><b>Rule 1 - no client reference for prompt logic.</b> Channel extension projects
/// (<c>src/extensions/BotNexus.Extensions.Channels.*</c>) MUST NOT reference the SignalR
/// Blazor client for prompt reconciliation. Prompt normalisation lives in the domain
/// (<see cref="AskUserPromptNormalizer"/>). The SignalR extension family is exempt: it owns
/// the Blazor client and bundles it as its own web asset.</para>
///
/// <para><b>Rule 2 - single resolution path.</b> Only the resolver implementation may call
/// <c>IAskUserResponseRegistry.TryComplete</c>. A channel that reaches past
/// <see cref="IAskUserPromptResolver"/> straight into the registry reintroduces exactly the
/// per-channel validation and error semantics this issue removed.</para>
///
/// <para><b>Rule 3 - the resolution contract stays complete.</b> The shared submission must
/// keep carrying free-form text, structured selections, and explicit cancel. Dropping any of
/// the three would silently re-narrow the seam to the free-text-only shape the inbound
/// interceptor used to have.</para>
///
/// <para><b>Rule 4 - the capability signal exists.</b> <see cref="IChannelAdapter"/> must
/// expose the interactive-prompt capability the gateway uses to choose between structured
/// rendering and the text-degraded fallback.</para>
/// </summary>
public sealed class AskUserChannelSeamArchitectureTests
{
    private static string RepoRoot => FindRepoRoot();

    /// <summary>
    /// Extension projects allowed to reference the Blazor client. The SignalR channel family
    /// owns and publishes that client - it is not a consumer reaching across a seam.
    /// </summary>
    private static readonly string[] BlazorClientReferenceExemptions =
    [
        "BotNexus.Extensions.Channels.SignalR",
        "BotNexus.Extensions.Channels.SignalR.BlazorClient",
        "BotNexus.Extensions.Channels.SignalR.BlazorClient.Core",
        "BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile"
    ];

    [Fact]
    public void Channel_extension_projects_do_not_reference_the_blazor_client()
    {
        var offenders = new List<string>();

        foreach (var csproj in EnumerateChannelExtensionProjects())
        {
            var projectName = Path.GetFileNameWithoutExtension(csproj);
            if (BlazorClientReferenceExemptions.Contains(projectName, StringComparer.OrdinalIgnoreCase))
                continue;

            var content = File.ReadAllText(csproj);
            if (content.Contains("BlazorClient", StringComparison.OrdinalIgnoreCase))
                offenders.Add(Path.GetRelativePath(RepoRoot, csproj));
        }

        offenders.ShouldBeEmpty(
            "Channel extension projects must not reference the SignalR Blazor client. ask_user prompt " +
            "reconciliation lives in BotNexus.Domain (AskUserPromptNormalizer) precisely so Telegram, " +
            "Discord, Slack, and TUI can consume it without a client assembly reference (#2322). " +
            "Offending project(s): " + string.Join(", ", offenders));
    }

    [Fact]
    public void Only_the_resolver_completes_pending_ask_user_requests()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildArtifact(file))
                continue;

            var fileName = Path.GetFileName(file);

            // The registry declares and implements TryComplete; the resolver is its one caller.
            if (fileName is "IAskUserResponseRegistry.cs" or "AskUserResponseRegistry.cs" or "AskUserPromptResolver.cs")
                continue;

            var content = File.ReadAllText(file);

            // Scoped to the ask-user registry receiver: Channel<T>.Writer.TryComplete() is an
            // unrelated BCL call that appears throughout the streaming and queueing code.
            if (Regex.IsMatch(content, @"\b\w*(?i:askuser\w*registry|registry)\w*\s*\.\s*TryComplete\s*\("))
                offenders.Add(Path.GetRelativePath(RepoRoot, file));
        }

        offenders.ShouldBeEmpty(
            "IAskUserResponseRegistry.TryComplete must only be called by AskUserPromptResolver. Channels " +
            "resolve ask_user prompts through IAskUserPromptResolver so validation, normalisation, and " +
            "error semantics live in exactly one place (#2322). Offending file(s): " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void Channel_extensions_do_not_touch_the_ask_user_registry_directly()
    {
        var offenders = new List<string>();
        var extensionsRoot = Path.Combine(RepoRoot, "src", "extensions");

        if (Directory.Exists(extensionsRoot))
        {
            foreach (var file in Directory.EnumerateFiles(extensionsRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildArtifact(file))
                    continue;

                if (File.ReadAllText(file).Contains("IAskUserResponseRegistry", StringComparison.Ordinal))
                    offenders.Add(Path.GetRelativePath(RepoRoot, file));
            }
        }

        offenders.ShouldBeEmpty(
            "Channel extensions must not reference IAskUserResponseRegistry. The registry is an internal " +
            "detail of the gateway's resolution path; channels go through IAskUserPromptResolver so a new " +
            "channel cannot invent its own entry point, validation, or error semantics (#2322). " +
            "Offending file(s): " + string.Join(", ", offenders));
    }

    [Fact]
    public void Submission_contract_carries_all_three_response_kinds()
    {
        var submission = typeof(AskUserSubmission);

        submission.GetProperty(nameof(AskUserSubmission.FreeFormText)).ShouldNotBeNull(
            "AskUserSubmission must carry free-form text.");
        submission.GetProperty(nameof(AskUserSubmission.SelectedValues)).ShouldNotBeNull(
            "AskUserSubmission must carry structured selections - dropping this re-narrows the seam to " +
            "the free-text-only shape PendingAskUserInterceptor used to have (#2322).");
        submission.GetProperty(nameof(AskUserSubmission.Cancelled)).ShouldNotBeNull(
            "AskUserSubmission must carry explicit cancellation.");
    }

    [Fact]
    public void ChannelAdapter_exposes_the_interactive_prompt_capability()
    {
        typeof(IChannelAdapter)
            .GetProperty(nameof(IChannelAdapter.SupportsInteractivePrompts))
            .ShouldNotBeNull(
                "IChannelAdapter must expose SupportsInteractivePrompts so the gateway can choose between " +
                "structured prompt rendering and the text-degraded fallback (#2322).");
    }

    [Fact]
    public void Resolver_contract_is_the_channel_facing_entry_point()
    {
        typeof(IAskUserPromptResolver)
            .GetMethod(nameof(IAskUserPromptResolver.ResolveAsync))
            .ShouldNotBeNull("IAskUserPromptResolver.ResolveAsync is the single ask_user resolution entry point (#2322).");
    }

    private static IEnumerable<string> EnumerateChannelExtensionProjects()
    {
        var extensionsRoot = Path.Combine(RepoRoot, "src", "extensions");
        if (!Directory.Exists(extensionsRoot))
            return [];

        return Directory
            .EnumerateFiles(extensionsRoot, "BotNexus.Extensions.Channels.*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsBuildArtifact(path));
    }

    private static bool IsBuildArtifact(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, "BotNexus.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}
