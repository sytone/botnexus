namespace BotNexus.SourceGenerators;

using System.Collections.Generic;

/// <summary>
/// The shape emitted for one <c>[HubEventInventory]</c>-annotated interface (#3318): the target
/// namespace, the container class name, and the ordered event names read off the interface.
/// </summary>
public sealed class HubEventInventoryModel
{
    /// <summary>Namespace the inventory class is emitted into (the interface's own namespace).</summary>
    public string Namespace { get; set; }

    /// <summary>Name of the generated static container, e.g. <c>HubEvents</c>.</summary>
    public string ClassName { get; set; }

    /// <summary>Name of the interface the inventory was derived from, used in the doc comment.</summary>
    public string SourceInterfaceName { get; set; }

    /// <summary>Event names in interface declaration order.</summary>
    public IReadOnlyList<string> EventNames { get; set; } = new List<string>();
}
