namespace BotNexus.Prompts;

public sealed class PromptPipeline
{
    private readonly List<IPromptSection> _sections = [];
    private readonly List<IPromptContributor> _contributors = [];

    public PromptPipeline Add(IPromptSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        _sections.Add(section);
        return this;
    }

    public PromptPipeline AddContributors(IEnumerable<IPromptContributor> contributors)
    {
        ArgumentNullException.ThrowIfNull(contributors);
        _contributors.AddRange(contributors.Where(static contributor => contributor is not null)!);
        return this;
    }

    public string Build(PromptContext context)
    {
        return string.Join("\n", BuildLines(context));
    }

    public IReadOnlyList<string> BuildLines(PromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var blocks = new List<(int Order, int TieBreaker, IReadOnlyList<string> Lines)>();
        var sectionIndex = 0;
        foreach (var section in _sections.Where(section => section.ShouldInclude(context)).OrderBy(section => section.Order))
        {
            blocks.Add((section.Order, sectionIndex++, section.Build(context)));
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
            blocks.Add((contribution.Order ?? contributor.Priority, sectionIndex++, lines));
        }

        return blocks
            .OrderBy(static item => item.Order)
            .ThenBy(static item => item.TieBreaker)
            .SelectMany(static item => item.Lines)
            .ToList();
    }
}
