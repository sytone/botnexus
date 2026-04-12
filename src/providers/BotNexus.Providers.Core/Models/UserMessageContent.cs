using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Providers.Core.Models;

/// <summary>
/// Represents user message content that can be either a plain string
/// or a list of content blocks (TextContent | ImageContent).
/// Mirrors pi-mono's <c>string | (TextContent | ImageContent)[]</c> union.
/// </summary>
[JsonConverter(typeof(UserMessageContentConverter))]
/// <summary>
/// Represents user message content.
/// </summary>
public sealed class UserMessageContent
{
    /// <summary>
    /// Gets the text.
    /// </summary>
    public string? Text { get; }
    /// <summary>
    /// Gets the blocks.
    /// </summary>
    public IReadOnlyList<ContentBlock>? Blocks { get; }

    public bool IsText => Text is not null;

    public UserMessageContent(string text) => Text = text;
    public UserMessageContent(IReadOnlyList<ContentBlock> blocks) => Blocks = blocks;

    /// <summary>
    /// Performs the declared conversion or operator operation.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The operator user message content result.</returns>
    public static implicit operator UserMessageContent(string text) => new(text);
}

internal sealed class UserMessageContentConverter : JsonConverter<UserMessageContent>
{
    /// <summary>
    /// Executes read.
    /// </summary>
    /// <param name="reader">The reader.</param>
    /// <param name="typeToConvert">The type to convert.</param>
    /// <param name="options">The options.</param>
    /// <returns>The read result.</returns>
    public override UserMessageContent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new UserMessageContent(reader.GetString()!);

        var blocks = JsonSerializer.Deserialize<List<ContentBlock>>(ref reader, options)
                     ?? [];
        return new UserMessageContent(blocks);
    }

    /// <summary>
    /// Executes write.
    /// </summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The value.</param>
    /// <param name="options">The options.</param>
    public override void Write(Utf8JsonWriter writer, UserMessageContent value, JsonSerializerOptions options)
    {
        if (value.IsText)
            writer.WriteStringValue(value.Text);
        else
            JsonSerializer.Serialize(writer, value.Blocks, options);
    }
}
