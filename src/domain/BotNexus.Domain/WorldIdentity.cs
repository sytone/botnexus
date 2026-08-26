using System.ComponentModel.DataAnnotations;
using BotNexus.Gateway.Abstractions.Models;
namespace BotNexus.Domain;

/// <summary>
/// Represents world identity.
/// </summary>
public sealed record WorldIdentity
{
    /// <summary>
    /// Gets or sets the id.
    /// </summary>
    [Display(
        Name = "ID",
        Description = "Stable identifier for this world. Used to scope agents, conversations and cross-world federation; do not change it after first start.",
        GroupName = "World identity",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "world-identity", Order = 0)]
    public required string Id { get; init; }
    /// <summary>
    /// Gets or sets the name.
    /// </summary>
    [Display(
        Name = "Name",
        Description = "Human-readable display name for this world, shown in the portal and in cross-world peer listings.",
        GroupName = "World identity",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "world-identity", Order = 1)]
    public required string Name { get; init; }
    /// <summary>
    /// Gets or sets the description.
    /// </summary>
    [Display(
        Name = "Description",
        Description = "Short description of what this world is for. Presentation only; has no runtime effect.",
        GroupName = "World identity",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "world-identity", Order = 2)]
    public string? Description { get; init; }
    /// <summary>
    /// Gets or sets the emoji.
    /// </summary>
    [Display(
        Name = "Emoji",
        Description = "Emoji used to visually identify this world in the portal and in peer listings.",
        GroupName = "World identity",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "world-identity", Order = 3)]
    public string? Emoji { get; init; }
}
