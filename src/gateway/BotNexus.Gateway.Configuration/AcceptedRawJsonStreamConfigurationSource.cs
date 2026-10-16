using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// A JSON stream source that retains the exact accepted document alongside the framework key
/// projection, allowing raw-shape normalization without guessing objects or arrays from scalars.
/// </summary>
public sealed class AcceptedRawJsonStreamConfigurationSource : JsonStreamConfigurationSource
{
    /// <inheritdoc />
    public override IConfigurationProvider Build(IConfigurationBuilder builder)
        => new AcceptedRawJsonStreamConfigurationProvider(this);
}

internal sealed class AcceptedRawJsonStreamConfigurationProvider(AcceptedRawJsonStreamConfigurationSource source)
    : JsonStreamConfigurationProvider(source), IAcceptedRawConfigDocumentProvider
{
    private ConfigDocument? _acceptedRawDocument;

    public override void Load(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        var bytes = copy.ToArray();
        _acceptedRawDocument = ConfigDocument.Parse(Encoding.UTF8.GetString(bytes));
        using var bindingStream = ExactModelCapacityConfiguration.CreateBindingStream(_acceptedRawDocument);
        base.Load(bindingStream);
    }

    ConfigDocument? IAcceptedRawConfigDocumentProvider.GetAcceptedRawDocument()
        => _acceptedRawDocument?.DeepClone();
}

/// <summary>Registration helpers for exact-document JSON streams.</summary>
public static class AcceptedRawJsonStreamConfigurationExtensions
{
    /// <summary>Adds a JSON stream while retaining its exact parsed document for post-configuration.</summary>
    public static IConfigurationBuilder AddAcceptedRawJsonStream(
        this IConfigurationBuilder builder,
        Stream stream)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(stream);
        builder.Add(new AcceptedRawJsonStreamConfigurationSource { Stream = stream });
        return builder;
    }
}
