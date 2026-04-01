using BotNexus.Core.Models;
using FluentAssertions;

namespace BotNexus.Tests.Integration.Tests;

/// <summary>
/// SC-OBS-003: Correlation IDs flow through pipeline.
/// Validates that each message gets a correlation ID (auto-generated or propagated)
/// and that the ID persists through the full message lifecycle.
/// </summary>
public sealed class CorrelationIdE2eTests
{
    [Fact]
    public void EnsureCorrelationId_GeneratesNewId_WhenMissing()
    {
        var message = new InboundMessage(
            Channel: "test",
            SenderId: "user-1",
            ChatId: "chat-1",
            Content: "hello",
            Timestamp: DateTimeOffset.UtcNow,
            Media: [],
            Metadata: new Dictionary<string, object>());

        var enriched = message.EnsureCorrelationId(out var correlationId);

        correlationId.Should().NotBeNullOrWhiteSpace();
        correlationId.Should().HaveLength(32, "GUID without dashes is 32 chars");
        enriched.GetCorrelationId().Should().Be(correlationId);
    }

    [Fact]
    public void EnsureCorrelationId_PreservesExistingId_WhenPresent()
    {
        var existingId = "my-custom-correlation-id-42";
        var message = new InboundMessage(
            Channel: "test",
            SenderId: "user-1",
            ChatId: "chat-1",
            Content: "hello",
            Timestamp: DateTimeOffset.UtcNow,
            Media: [],
            Metadata: new Dictionary<string, object>
            {
                [InboundMessageCorrelationExtensions.CorrelationIdMetadataKey] = existingId
            });

        var enriched = message.EnsureCorrelationId(out var correlationId);

        correlationId.Should().Be(existingId);
        enriched.GetCorrelationId().Should().Be(existingId);
    }

    [Fact]
    public void EnsureCorrelationId_CalledMultipleTimes_ReturnsConsistentId()
    {
        var message = new InboundMessage(
            Channel: "test",
            SenderId: "user-1",
            ChatId: "chat-1",
            Content: "hello",
            Timestamp: DateTimeOffset.UtcNow,
            Media: [],
            Metadata: new Dictionary<string, object>());

        var enriched1 = message.EnsureCorrelationId(out var id1);
        var enriched2 = enriched1.EnsureCorrelationId(out var id2);

        id1.Should().Be(id2, "calling EnsureCorrelationId twice should return the same ID");
    }

    [Fact]
    public void GetCorrelationId_ReturnsNull_WhenNoCorrelationId()
    {
        var message = new InboundMessage(
            Channel: "test",
            SenderId: "user-1",
            ChatId: "chat-1",
            Content: "hello",
            Timestamp: DateTimeOffset.UtcNow,
            Media: [],
            Metadata: new Dictionary<string, object>());

        message.GetCorrelationId().Should().BeNull();
    }

    [Fact]
    public void GetCorrelationId_HandlesNonStringValue()
    {
        var message = new InboundMessage(
            Channel: "test",
            SenderId: "user-1",
            ChatId: "chat-1",
            Content: "hello",
            Timestamp: DateTimeOffset.UtcNow,
            Media: [],
            Metadata: new Dictionary<string, object>
            {
                [InboundMessageCorrelationExtensions.CorrelationIdMetadataKey] = 42
            });

        message.GetCorrelationId().Should().Be("42");
    }

    [Fact]
    public void CorrelationId_SurvivesMessageRecordClone()
    {
        var message = new InboundMessage(
            Channel: "test",
            SenderId: "user-1",
            ChatId: "chat-1",
            Content: "original",
            Timestamp: DateTimeOffset.UtcNow,
            Media: [],
            Metadata: new Dictionary<string, object>());

        var enriched = message.EnsureCorrelationId(out var originalId);

        // Clone via with expression (simulating passage through pipeline)
        var cloned = enriched with { Content = "modified" };

        cloned.GetCorrelationId().Should().Be(originalId,
            "correlation ID should survive record cloning through the pipeline");
    }

    [Fact]
    public void CorrelationId_IsUniquePerMessage()
    {
        var message1 = new InboundMessage(
            Channel: "test", SenderId: "user-1", ChatId: "chat-1",
            Content: "msg1", Timestamp: DateTimeOffset.UtcNow,
            Media: [], Metadata: new Dictionary<string, object>());

        var message2 = new InboundMessage(
            Channel: "test", SenderId: "user-2", ChatId: "chat-2",
            Content: "msg2", Timestamp: DateTimeOffset.UtcNow,
            Media: [], Metadata: new Dictionary<string, object>());

        message1.EnsureCorrelationId(out var id1);
        message2.EnsureCorrelationId(out var id2);

        id1.Should().NotBe(id2, "different messages should get different correlation IDs");
    }

    [Fact]
    public void CorrelationId_FlowsThroughMetadataDictionary()
    {
        var message = new InboundMessage(
            Channel: "test", SenderId: "user-1", ChatId: "chat-1",
            Content: "hello", Timestamp: DateTimeOffset.UtcNow,
            Media: [], Metadata: new Dictionary<string, object>());

        var enriched = message.EnsureCorrelationId(out var correlationId);

        // Verify it's in the metadata dictionary
        enriched.Metadata.Should().ContainKey("correlation_id");
        enriched.Metadata["correlation_id"].Should().Be(correlationId);

        // Simulate downstream component reading it
        var downstreamId = enriched.GetCorrelationId();
        downstreamId.Should().Be(correlationId, "downstream components should see the same ID");
    }
}
