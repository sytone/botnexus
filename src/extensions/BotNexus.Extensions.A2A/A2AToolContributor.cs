using BotNexus.Agent.Core.Tools;
using BotNexus.Gateway.A2A;
using BotNexus.Gateway.Abstractions.A2A;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Extensions.A2A;

/// <summary>Contributes one bounded delegation tool when an agent grants a registered, ready profile.</summary>
public sealed class A2AToolContributor : IAgentToolContributor
{
    private readonly IReadOnlyList<IA2AServiceProfile> profiles;
    private readonly Func<A2AClient> clientFactory;

    internal A2AToolContributor(IEnumerable<IA2AServiceProfile> profiles, Func<A2AClient> clientFactory)
    {
        this.profiles = profiles?.ToList() ?? throw new ArgumentNullException(nameof(profiles));
        this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    /// <inheritdoc />
    public Task<AgentToolContribution> ContributeAsync(
        AgentToolContributionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = ExtensionConfigBinder.Bind<A2AExtensionConfig>(context.Descriptor, A2AExtensionConfig.ExtensionId);
        if (config?.Profiles is not { Count: > 0 })
            return Task.FromResult(new AgentToolContribution([]));

        var allowed = new HashSet<string>(
            config.Profiles.Where(static id => !string.IsNullOrWhiteSpace(id)).Select(static id => id.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var available = profiles
            .Where(profile => profile.IsReady && allowed.Contains(profile.Id))
            .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (available.Count == 0)
            return Task.FromResult(new AgentToolContribution([]));

        var tool = new DelegateA2ATaskTool(available, new DelegateClientFactory(clientFactory));
        return Task.FromResult(new AgentToolContribution([tool], [tool]));
    }

    private sealed class DelegateClientFactory(Func<A2AClient> factory) : IA2AClientFactory
    {
        public IA2AClient Create() => new Client(factory());

        private sealed class Client(A2AClient client) : IA2AClient
        {
            public Task<A2ATaskResult> SendMessageAsync(A2AClientOptions options, A2AMessage message, DateTimeOffset deadline, CancellationToken cancellationToken = default)
                => client.SendMessageAsync(options, message, deadline, cancellationToken);
            public void Dispose() => client.Dispose();
        }
    }
}
