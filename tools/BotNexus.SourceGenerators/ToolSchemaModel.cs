namespace BotNexus.SourceGenerators;

using System.Collections.Generic;

/// <summary>
/// One declared tool parameter, as read off a <c>[ToolParameter]</c> attribute (#3320).
/// </summary>
/// <remarks>
/// This is the generator-side model. It is deliberately a plain value type with structural equality
/// so the incremental pipeline can cache on it: a model that compared by reference would re-run
/// emission on every keystroke.
/// </remarks>
public sealed class ToolParameterModel : System.IEquatable<ToolParameterModel>
{
    /// <summary>The key as it appears in the JSON schema and in the caller's argument dictionary.</summary>
    public string Name { get; set; }

    /// <summary>The JSON Schema type keyword (<c>string</c>, <c>integer</c>, <c>boolean</c>, <c>number</c>, <c>array</c>, <c>object</c>).</summary>
    public string JsonType { get; set; }

    /// <summary>The description sent to the model. Empty descriptions are omitted from the schema.</summary>
    public string Description { get; set; }

    /// <summary>Whether the parameter appears in the schema's <c>required</c> array.</summary>
    public bool Required { get; set; }

    /// <summary>
    /// When set, this parameter is an alias: the prepare stage copies it into the named target key
    /// instead of its own. Declaration order decides precedence, so the canonical key must be
    /// declared before its aliases.
    /// </summary>
    public string AliasOf { get; set; }

    /// <summary>
    /// When true the parameter is accepted by the prepare stage but NOT advertised in the schema.
    /// This models the tolerated-but-undocumented aliases the survey found (<c>include</c>,
    /// <c>max_results</c>): removing them would break callers, advertising them would grow the
    /// model-visible surface.
    /// </summary>
    public bool HiddenFromSchema { get; set; }

    /// <summary>The prepared-dictionary key this parameter writes to.</summary>
    public string TargetKey => string.IsNullOrEmpty(AliasOf) ? Name : AliasOf;

    /// <inheritdoc />
    public bool Equals(ToolParameterModel other)
    {
        if (other is null)
        {
            return false;
        }

        return Name == other.Name
            && JsonType == other.JsonType
            && Description == other.Description
            && Required == other.Required
            && AliasOf == other.AliasOf
            && HiddenFromSchema == other.HiddenFromSchema;
    }

    /// <inheritdoc />
    public override bool Equals(object obj) => Equals(obj as ToolParameterModel);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            hash = (hash * 31) + (Name?.GetHashCode() ?? 0);
            hash = (hash * 31) + (JsonType?.GetHashCode() ?? 0);
            hash = (hash * 31) + (Description?.GetHashCode() ?? 0);
            hash = (hash * 31) + Required.GetHashCode();
            hash = (hash * 31) + (AliasOf?.GetHashCode() ?? 0);
            hash = (hash * 31) + HiddenFromSchema.GetHashCode();
            return hash;
        }
    }
}

/// <summary>
/// The full parameter declaration for one tool: the annotated container plus its parameters (#3320).
/// </summary>
public sealed class ToolSchemaModel : System.IEquatable<ToolSchemaModel>
{
    /// <summary>Namespace of the annotated partial container.</summary>
    public string Namespace { get; set; }

    /// <summary>Name of the annotated partial container.</summary>
    public string ContainerName { get; set; }

    /// <summary>Declaration-ordered parameters.</summary>
    public IReadOnlyList<ToolParameterModel> Parameters { get; set; } = new List<ToolParameterModel>();

    /// <inheritdoc />
    public bool Equals(ToolSchemaModel other)
    {
        if (other is null || Namespace != other.Namespace || ContainerName != other.ContainerName)
        {
            return false;
        }

        if (Parameters.Count != other.Parameters.Count)
        {
            return false;
        }

        for (var index = 0; index < Parameters.Count; index++)
        {
            if (!Parameters[index].Equals(other.Parameters[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object obj) => Equals(obj as ToolSchemaModel);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        unchecked
        {
            var hash = (Namespace?.GetHashCode() ?? 0) * 31 + (ContainerName?.GetHashCode() ?? 0);
            foreach (var parameter in Parameters)
            {
                hash = (hash * 31) + parameter.GetHashCode();
            }

            return hash;
        }
    }
}
