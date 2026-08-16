namespace BotNexus.SourceGenerators;

using System;

/// <summary>
/// MSBuild-supplied configuration for the feature-flag generator, carried through the Roslyn
/// incremental pipeline.
/// <para>
/// <b>Why <see cref="IEquatable{T}"/> matters:</b> Roslyn compares successive option values to
/// decide whether cached generator output is still valid. A property added here but omitted from
/// <see cref="Equals(GeneratorOptions)"/> or <see cref="GetHashCode"/> makes the generator serve
/// stale code when only that property changes - the failure is silent and looks like the build
/// ignoring an edit.
/// </para>
/// <para>
/// Property names must stay in lockstep with the <c>build_property.*</c> keys read in
/// <c>FeatureFlagSourceGenerator.ExtractOptions</c> and the <c>CompilerVisibleProperty</c> items
/// declared by the consuming project. All three are one setting spelled three times.
/// </para>
/// </summary>
public sealed class GeneratorOptions : IEquatable<GeneratorOptions>
{
    /// <summary>
    /// Gets or sets the namespace of the generated inventory. Defaults to the gateway configuration
    /// namespace so the generated <c>FeatureFlags</c> is a drop-in for the hand-written type it
    /// replaced and no consumer had to change its <c>using</c> directives.
    /// <para>MSBuild property: <c>FeatureFlagSourceGenerator_Namespace</c>.</para>
    /// </summary>
    public string Namespace { get; set; } = "BotNexus.Gateway.Configuration";

    /// <summary>
    /// Gets or sets the name of the generated static inventory class.
    /// <para>MSBuild property: <c>FeatureFlagSourceGenerator_ClassName</c>. Default <c>FeatureFlags</c>.</para>
    /// </summary>
    public string ClassName { get; set; } = "FeatureFlags";

    /// <summary>
    /// Gets or sets the days after <c>dateAdded</c> at which a flag earns a staleness warning.
    /// <c>0</c> disables it.
    /// <para>MSBuild property: <c>FeatureFlagSourceGenerator_AgeWarning</c>. Default 90.</para>
    /// </summary>
    public int AgeWarningDays { get; set; } = 90;

    /// <inheritdoc />
    public bool Equals(GeneratorOptions other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Namespace == other.Namespace
            && ClassName == other.ClassName
            && AgeWarningDays == other.AgeWarningDays;
    }

    /// <inheritdoc />
    public override bool Equals(object obj) => Equals(obj as GeneratorOptions);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        unchecked
        {
            var hash = Namespace.GetHashCode();
            hash = (hash * 397) ^ ClassName.GetHashCode();
            hash = (hash * 397) ^ AgeWarningDays;
            return hash;
        }
    }
}
