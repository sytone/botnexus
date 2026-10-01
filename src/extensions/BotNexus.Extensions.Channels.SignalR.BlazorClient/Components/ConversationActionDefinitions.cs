namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;

/// <summary>Describes one command rendered by the shared conversation action menu.</summary>
public sealed record ConversationActionDefinition(string Id, string Label, Func<Task> Invoke, bool IsDestructive = false);

/// <summary>Describes the typed projection decisions that control which conversation commands are valid.</summary>
public sealed record ConversationActionContext(
    bool Exists,
    bool IsDefault,
    bool IsReadOnly,
    bool IsVirtual,
    bool IsPinned,
    IReadOnlyList<ConversationActionSection> Sections);

/// <summary>Identifies a user section available as a move destination.</summary>
public sealed record ConversationActionSection(string Id, string Name, bool IsCurrent = false);

/// <summary>Collects action callbacks so sidebar and chat entry points share one definition seam.</summary>
public sealed record ConversationActionCallbacks(
    Func<Task> Rename,
    Func<bool, Task> SetPinned,
    Func<Task> Archive,
    Func<string?, Task> MoveToSection)
{
    /// <summary>Provides inert callbacks for projection-only tests and consumers.</summary>
    public static ConversationActionCallbacks NoOp { get; } = new(
        () => Task.CompletedTask,
        _ => Task.CompletedTask,
        () => Task.CompletedTask,
        _ => Task.CompletedTask);
}

/// <summary>Builds the canonical ordered action set from the existing conversation projection.</summary>
public static class ConversationActionDefinitions
{
    /// <summary>Returns only commands valid for the supplied conversation kind and mutability.</summary>
    public static IReadOnlyList<ConversationActionDefinition> Create(
        ConversationActionContext context,
        ConversationActionCallbacks callbacks)
    {
        if (!context.Exists || context.IsReadOnly)
            return [];

        var actions = new List<ConversationActionDefinition>();
        if (!context.IsReadOnly)
            actions.Add(new("rename", "Rename", callbacks.Rename));

        if (!context.IsDefault && !context.IsReadOnly)
        {
            actions.Add(new("pin", context.IsPinned ? "Unpin" : "Pin", () => callbacks.SetPinned(!context.IsPinned)));
            actions.Add(new("move-none", "Move to: None", () => callbacks.MoveToSection(null)));
            actions.AddRange(context.Sections.Select(section => new ConversationActionDefinition(
                $"move-{section.Id}",
                $"Move to: {section.Name}{(section.IsCurrent ? " (current)" : string.Empty)}",
                () => callbacks.MoveToSection(section.Id))));
        }

        if (!context.IsDefault)
            actions.Add(new("archive", context.IsVirtual ? "Close" : "Archive", callbacks.Archive, IsDestructive: true));

        return actions;
    }
}
