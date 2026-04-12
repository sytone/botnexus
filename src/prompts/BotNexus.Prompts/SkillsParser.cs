namespace BotNexus.Prompts;

public static class SkillsParser
{
    public static SkillDocument Parse(string raw)
    {
        var content = raw.Trim();
        if (!content.StartsWith("---", StringComparison.Ordinal))
        {
            return new SkillDocument("skill", null, content);
        }

        var lines = content.Split(["\r\n", "\n"], StringSplitOptions.None);
        var closingIndex = Array.FindIndex(lines, 1, static line => line.Trim().Equals("---", StringComparison.Ordinal));
        if (closingIndex < 0)
        {
            return new SkillDocument("skill", null, content);
        }

        string? name = null;
        string? description = null;
        for (var i = 1; i < closingIndex; i++)
        {
            var line = lines[i];
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('\'', '"');
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                name = value;
            }
            else if (key.Equals("description", StringComparison.OrdinalIgnoreCase))
            {
                description = value;
            }
        }

        var body = string.Join(Environment.NewLine, lines.Skip(closingIndex + 1)).Trim();
        return new SkillDocument(name ?? "skill", description, body);
    }
}