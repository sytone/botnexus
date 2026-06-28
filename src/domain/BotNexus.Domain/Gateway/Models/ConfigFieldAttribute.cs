namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// UI widget hint for a configuration field. Consumed by later config-parity layers
/// (the schema endpoint and SchemaForm renderer, future PBIs of #1579) to choose how to
/// render an editor for the annotated property. This is UI-only metadata and has no effect
/// on how configuration is parsed or validated at runtime.
/// </summary>
public enum ConfigFieldWidget
{
    /// <summary>Boolean on/off control (rendered as a switch/checkbox).</summary>
    Toggle = 0,

    /// <summary>Single-line free text input.</summary>
    Text = 1,

    /// <summary>Numeric input (integer or decimal).</summary>
    Number = 2,

    /// <summary>Single-choice picker over a known set of options.</summary>
    Select = 3,

    /// <summary>Sensitive value (API key, token); rendered masked and not echoed back.</summary>
    Secret = 4,
}

/// <summary>
/// Declarative, UI-only metadata for a configuration property. Pairs with the standard
/// <see cref="System.ComponentModel.DataAnnotations.DisplayAttribute"/> and validation
/// attributes on the same member to describe how a config field should be presented in a
/// generated settings editor.
/// </summary>
/// <remarks>
/// <para>
/// This is the foundation type for config parity (#1579, PBI 1/6 #1609). It carries presentation
/// metadata only: a <see cref="Widget"/> hint, an optional <see cref="Group"/> for visual
/// grouping, an <see cref="Order"/> for layout, and a <see cref="Secret"/> flag for sensitive
/// values. Nothing here changes configuration loading, binding, or validation behaviour; downstream
/// layers reflect over it to build a UI. Enforcement of validation attributes is a separate,
/// later PBI (#1613).
/// </para>
/// <para>
/// Applied to properties (the config tree is a graph of POCOs with init/get-set properties).
/// Not inherited and single-use per member.
/// </para>
/// </remarks>
[System.AttributeUsage(System.AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ConfigFieldAttribute : System.Attribute
{
    /// <summary>
    /// The UI widget used to render an editor for this field. Defaults to
    /// <see cref="ConfigFieldWidget.Text"/>.
    /// </summary>
    public ConfigFieldWidget Widget { get; set; } = ConfigFieldWidget.Text;

    /// <summary>
    /// Optional logical group key for arranging related fields together in the UI. When null or
    /// empty, the field is ungrouped (or grouped by its <c>[Display(GroupName=...)]</c> if a
    /// renderer prefers that). This is independent of validation.
    /// </summary>
    public string? Group { get; set; }

    /// <summary>
    /// Optional relative ordering hint within a group or form. Lower values render first.
    /// Defaults to 0. Ties fall back to declaration/discovery order.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// When true, the field holds a sensitive value (for example an API key or token) and should
    /// be masked in the UI and never echoed back in plaintext. Defaults to false. Equivalent in
    /// intent to setting <see cref="Widget"/> to <see cref="ConfigFieldWidget.Secret"/>; both may
    /// be set for clarity.
    /// </summary>
    public bool Secret { get; set; }
}
