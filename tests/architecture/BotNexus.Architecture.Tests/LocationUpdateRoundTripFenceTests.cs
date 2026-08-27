using System.Reflection;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using Shouldly;

namespace BotNexus.Architecture.Tests;

/// <summary>
/// Fences the location update round trip (#3616): every <see cref="LocationConfig"/> property is
/// either modelled by the request DTO or explicitly preserved when an update is applied.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a fence and not a code review.</b> The defect this guards was created by adding
/// properties to <see cref="LocationConfig"/> over time while <c>UpsertLocationRequest</c> stayed
/// at four fields. Nobody deleted anything; the gap opened by accretion, and each individual
/// addition looked harmless.
/// </para>
/// <para>
/// It is also invisible at runtime. The update returns 200, the emitted change set genuinely names
/// only the edited keys, and the configuration file is valid afterwards - it simply has less in it.
/// There is no crash and no log line to notice, so the only thing that catches the seventh property
/// is a build-time assertion.
/// </para>
/// <para>
/// <b>This is the location half of the same rule #3560 asks for on agent descriptors.</b> Both are
/// instances of one class: a typed DTO projected over stored configuration silently deletes
/// everything it does not model (#3547).
/// </para>
/// </remarks>
public sealed class LocationUpdateRoundTripFenceTests : ArchitectureTest
{
    /// <summary>
    /// Properties the update path is allowed to overwrite because the request carries them.
    /// </summary>
    private static readonly HashSet<string> ModelledByRequest = new(StringComparer.Ordinal)
    {
        nameof(LocationConfig.Type),
        nameof(LocationConfig.Description),

        // The type-discriminated value. The request carries a single Value, which is routed to
        // exactly one of these by Type - and the other two are deliberately cleared so a type
        // change cannot strand the previous type's value.
        nameof(LocationConfig.Path),
        nameof(LocationConfig.Endpoint),
        nameof(LocationConfig.ConnectionString),
    };

    /// <summary>
    /// Every writable <see cref="LocationConfig"/> property is either modelled by the request or
    /// named in the preservation copy.
    /// </summary>
    /// <remarks>
    /// Asserts against the SOURCE of <c>CloneForUpdate</c> rather than exercising the controller,
    /// because the failure being prevented is an omission: a property nobody thought about is
    /// exactly the one no behavioural test would cover.
    /// </remarks>
    [Fact]
    public void EveryLocationConfigProperty_IsModelledOrExplicitlyPreserved()
    {
        var controllerSource = FindControllerSource();
        File.Exists(controllerSource).ShouldBeTrue(
            $"LocationsController.cs must be locatable for this fence to mean anything (looked at {controllerSource})");

        var text = File.ReadAllText(controllerSource);
        var cloneBody = ExtractCloneForUpdateBody(text);
        cloneBody.ShouldNotBeNullOrWhiteSpace(
            "CloneForUpdate is the preservation seam; if it has been renamed or removed, this fence " +
            "must be updated deliberately rather than silently passing.");

        var properties = typeof(LocationConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .ToArray();

        // Anti-vacuity: a reflection lookup that silently returns nothing would make this fence
        // green forever.
        properties.Length.ShouldBeGreaterThanOrEqualTo(
            6, "LocationConfig should expose at least its six known writable properties");

        var unhandled = properties
            .Where(p => !ModelledByRequest.Contains(p.Name))
            .Where(p => !cloneBody!.Contains($"{p.Name} = existing.{p.Name}", StringComparison.Ordinal))
            .Select(p => p.Name)
            .ToArray();

        unhandled.ShouldBeEmpty(
            "Every LocationConfig property must either be modelled by UpsertLocationRequest (and so " +
            "legitimately overwritten) or copied in CloneForUpdate, or PUT /api/locations will " +
            "silently delete it (#3616). Unhandled: " + string.Join(", ", unhandled));
    }

    /// <summary>
    /// The allowlist itself must stay honest: a property named as modelled must actually exist.
    /// </summary>
    /// <remarks>
    /// Without this, the fence could be satisfied by adding a stale name to the allowlist rather
    /// than by handling a real property.
    /// </remarks>
    [Fact]
    public void ModelledAllowlist_NamesOnlyRealProperties()
    {
        var actual = typeof(LocationConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        var stale = ModelledByRequest.Where(name => !actual.Contains(name)).ToArray();

        stale.ShouldBeEmpty(
            "The modelled-by-request allowlist names properties that no longer exist on " +
            "LocationConfig: " + string.Join(", ", stale));
    }

    private string FindControllerSource()
        => Path.Combine(
            Repository.SourceRoot,
            "gateway", "BotNexus.Gateway.Api", "Controllers", "LocationsController.cs");

    /// <summary>
    /// Returns the body of <c>CloneForUpdate</c>, or null when it cannot be found.
    /// </summary>
    private static string? ExtractCloneForUpdateBody(string text)
    {
        var start = text.IndexOf("CloneForUpdate(LocationConfig? existing)", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var end = text.IndexOf("};", start, StringComparison.Ordinal);
        return end < 0 ? null : text[start..end];
    }
}
