namespace BotNexus.Gateway.Prompts;

/// <summary>
/// Assembles system prompt content from ordered sections and contributors,
/// optionally wrapping each block in XML tags for improved model attention.
/// </summary>
public sealed class PromptPipeline
{
    private readonly List<IPromptSection> _sections = [];
    private readonly List<IPromptContributor> _contributors = [];

    /// <summary>
    /// Adds a prompt section to the pipeline.
    /// </summary>
    public PromptPipeline Add(IPromptSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        _sections.Add(section);
        return this;
    }

    /// <summary>
    /// Adds prompt contributors to the pipeline.
    /// </summary>
    public PromptPipeline AddContributors(IEnumerable<IPromptContributor> contributors)
    {
        ArgumentNullException.ThrowIfNull(contributors);
        _contributors.AddRange(contributors.Where(static contributor => contributor is not null)!);
        return this;
    }

    /// <summary>
    /// Builds the complete system prompt string from all sections and contributors.
    /// </summary>
    public string Build(PromptContext context)
    {
        return string.Join("\n", BuildLines(context));
    }

    /// <summary>
    /// Builds the ordered list of prompt lines from all sections and contributors,
    /// wrapping each block in XML tags when the section specifies an <see cref="IPromptSection.XmlTag"/>.
    /// </summary>
    public IReadOnlyList<string> BuildLines(PromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var blocks = new List<(int Order, int TieBreaker, string? XmlTag, IReadOnlyList<string> Lines)>();
        var sectionIndex = 0;
        foreach (var section in _sections.Where(section => section.ShouldInclude(context)).OrderBy(section => section.Order))
        {
            blocks.Add((section.Order, sectionIndex++, section.XmlTag, section.Build(context)));
        }

        foreach (var contributor in _contributors.Where(contributor => contributor.Target is null && contributor.ShouldInclude(context)))
        {
            var contribution = contributor.GetContribution(context);
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(contribution.SectionHeading))
            {
                lines.Add($"## {contribution.SectionHeading}");
            }

            lines.AddRange(contribution.Lines);
            blocks.Add((contribution.Order ?? contributor.Priority, sectionIndex++, null, lines));
        }

        var result = new List<string>();
        foreach (var block in blocks.OrderBy(static item => item.Order).ThenBy(static item => item.TieBreaker))
        {
            if (block.Lines.Count == 0)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(block.XmlTag))
            {
                result.Add($"<{block.XmlTag}>");
                result.AddRange(block.Lines);
                result.Add($"</{block.XmlTag}>");
            }
            else
            {
                result.AddRange(block.Lines);
            }
        }

        return result;
    }
}
