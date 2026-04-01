using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Agent;

/// <summary>
/// Loads skill definitions from files in the agent's skills directory.
/// Skills are simple text files describing tools; returned as ToolDefinitions with no parameters.
/// </summary>
public sealed class SkillsLoader : ISkillsLoader
{
    private readonly string _basePath;
    private readonly ILogger<SkillsLoader> _logger;

    public SkillsLoader(string basePath, ILogger<SkillsLoader> logger)
    {
        _basePath = basePath;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ToolDefinition>> LoadSkillsAsync(string agentName, CancellationToken cancellationToken = default)
    {
        var skillsDir = Path.Combine(_basePath, agentName, "skills");
        if (!Directory.Exists(skillsDir))
        {
            _logger.LogDebug("No skills directory for agent {AgentName}", agentName);
            return [];
        }

        var skills = new List<ToolDefinition>();
        foreach (var file in Directory.GetFiles(skillsDir, "*.txt"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var description = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            skills.Add(new ToolDefinition(name, description.Trim(), new Dictionary<string, ToolParameterSchema>()));
            _logger.LogDebug("Loaded skill {SkillName} for agent {AgentName}", name, agentName);
        }

        return skills;
    }
}
