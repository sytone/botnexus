using System.Text.Json.Nodes;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Configuration.Inheritance;

/// <summary>
/// One named layer of a layered configuration domain, lowest precedence first.
/// </summary>
/// <param name="Name">
/// Operator-facing layer name, e.g. <c>agents.defaults</c> or <c>agents.farnsworth</c>. Reported
/// verbatim in provenance and validation messages, so it must name something an operator can
/// actually go and edit - not an internal type name.
/// </param>
/// <param name="Document">
/// The layer's raw configuration document. Raw rather than bound, because binding to a POCO
/// collapses "absent" and "explicit null" into an identical null field, destroying the very
/// distinction the engine exists to honour (#2766).
/// </param>
public sealed record ConfigLayer(string Name, JsonObject? Document);

/// <summary>
/// Where one effective property's value came from.
/// </summary>
/// <param name="Path">Canonical dotted path of the property, e.g. <c>heartbeat.quietHours.start</c>.</param>
/// <param name="LayerName">
/// The <see cref="ConfigLayer.Name"/> that supplied the winning value, or <see langword="null"/> when
/// no layer set it.
/// </param>
/// <param name="Policy">The policy that decided the outcome.</param>
/// <param name="State">
/// The winning value's state. <see cref="Shadow.ConfigValueState.ExplicitNull"/> is a real outcome and
/// distinct from "nothing supplied it" - the former suppresses an inherited value, the latter means
/// no layer mentioned the property at all.
/// </param>
public sealed record ConfigProvenance(
    string Path,
    string? LayerName,
    ConfigInheritancePolicy Policy,
    Shadow.ConfigValueState State);

/// <summary>
/// A validation failure that names both the effective path and the layer responsible (#2425 AC9).
/// </summary>
/// <remarks>
/// Reporting only the effective path is what makes layered-config errors expensive to diagnose: an
/// operator reads "heartbeat.intervalMinutes is invalid", inspects their agent block, finds nothing,
/// and has no indication the offending value came from the shared defaults layer. Carrying the layer
/// name turns that hunt into a single edit.
/// </remarks>
/// <param name="Path">Canonical dotted path of the offending property.</param>
/// <param name="LayerName">The layer that supplied the offending value.</param>
/// <param name="Message">Human-readable description of what is wrong.</param>
public sealed record ConfigLayerValidationError(string Path, string LayerName, string Message)
{
    /// <summary>Renders the error with both coordinates an operator needs to act on it.</summary>
    public override string ToString() => $"{Path} (from layer '{LayerName}'): {Message}";
}

/// <summary>
/// The result of overlaying a stack of configuration layers.
/// </summary>
/// <param name="Document">
/// The merged document. Contains only what the layers actually supplied; the engine never
/// materialises a default that no layer set, because doing so would freeze a value that should have
/// kept tracking its parent layer (#2429).
/// </param>
/// <param name="Provenance">Per-path record of which layer supplied each effective value.</param>
public sealed record ConfigOverlayResult(
    JsonObject Document,
    IReadOnlyDictionary<string, ConfigProvenance> Provenance)
{
    /// <summary>
    /// Returns the provenance for <paramref name="path"/>, or <see langword="null"/> when no layer
    /// supplied that property (AC8).
    /// </summary>
    public ConfigProvenance? GetProvenance(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Provenance.TryGetValue(path, out var found) ? found : null;
    }
}
