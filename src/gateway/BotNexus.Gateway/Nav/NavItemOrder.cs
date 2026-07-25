namespace BotNexus.Gateway.Nav;

/// <summary>
/// A single nav item's effective sidebar order: its stable <see cref="Key"/> and the
/// <see cref="Order"/> number it renders at (ascending). Returned by the nav-order API so the
/// portal can render the whole left nav sorted by one model (#2236, slice 5 of #2231).
/// </summary>
/// <param name="Key">Stable nav key (e.g. <c>tools</c>, <c>chat</c>). Case-insensitive.</param>
/// <param name="Order">Effective order number; lower renders higher in the sidebar.</param>
public sealed record NavItemOrder(string Key, int Order);
