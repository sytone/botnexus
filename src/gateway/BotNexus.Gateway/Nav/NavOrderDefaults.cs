namespace BotNexus.Gateway.Nav;

/// <summary>
/// Canonical default order numbers for the built-in portal left-nav sections (#2236, slice 5 of
/// #2231). These defaults give every built-in nav item a stable, sensible position so the whole
/// nav can be rendered sorted by a single ordering model. Users may override any item's order;
/// overrides are persisted server-side so they roam with the user (see <see cref="INavOrderStore"/>).
/// </summary>
/// <remarks>
/// The gap of 10 between adjacent defaults leaves room for a user to slot an item between two
/// built-ins without renumbering. By design <c>tools</c> (20) sits above <c>chat</c> (30) so the
/// Tools section renders above Chat out of the box, per the product decision on #2231.
/// <c>home</c> (5) owns the root route and is the intended entry point, so it defaults above
/// Activity (#2535). Because the effective list is built by layering stored overrides onto this
/// dictionary, a stored order predating this key still surfaces Home at its default position.
/// </remarks>
public static class NavOrderDefaults
{
    /// <summary>Stable key for the Home (root landing) nav item (#2535).</summary>
    public const string Home = "home";

    /// <summary>Stable key for the Activity dashboard nav item.</summary>
    public const string Activity = "activity";

    /// <summary>Stable key for the Tools nav item.</summary>
    public const string Tools = "tools";

    /// <summary>Stable key for the Chat nav item (and its nested agent/conversation region).</summary>
    public const string Chat = "chat";

    /// <summary>Stable key for the Configuration nav item.</summary>
    public const string Configuration = "configuration";

    /// <summary>Stable key for the Skills nav item.</summary>
    public const string Skills = "skills";

    /// <summary>Stable key for the Agents nav item.</summary>
    public const string Agents = "agents";

    /// <summary>Stable key for the Cron Jobs nav item.</summary>
    public const string Cron = "cron";

    /// <summary>Stable key for the Plugins nav item (#3346).</summary>
    public const string Plugins = "plugins";

    /// <summary>
    /// Default order number for each built-in nav key. Lower numbers render higher in the sidebar.
    /// Tools (20) intentionally precedes Chat (30).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> Defaults = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        [Home] = 5,
        [Activity] = 10,
        [Tools] = 20,
        [Chat] = 30,
        [Configuration] = 40,
        [Skills] = 50,
        [Agents] = 60,
        [Cron] = 70,
        [Plugins] = 80,
    };
}
