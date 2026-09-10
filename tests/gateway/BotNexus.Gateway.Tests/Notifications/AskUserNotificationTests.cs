using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Notifications;
using BotNexus.Gateway.Services;

namespace BotNexus.Gateway.Tests.Notifications;

/// <summary>
/// Pins the notification raised when an agent blocks on <c>ask_user</c>.
/// </summary>
/// <remarks>
/// This is the one kind where the work is genuinely stopped until a person acts, so it is the one
/// most worth reaching a phone for. It is raised from a synchronous registration on the path that
/// is about to block, which is why the tests care that a failing or slow publisher cannot affect
/// registration at all.
/// </remarks>
public sealed class AskUserNotificationTests
{
    private static ConversationId Conversation(string id = "c_test") => ConversationId.From(id);

    [Fact]
    public void Registering_a_prompt_raises_a_waiting_notification()
    {
        var publisher = new RecordingPublisher();
        var registry = new AskUserResponseRegistry(publisher);

        registry.Register(Conversation(), timeout: null);

        var raised = Assert.Single(publisher.Published);
        Assert.Equal(NotificationKind.AgentWaitingForInput, raised.Kind);
        Assert.Equal(NotificationSeverity.Warning, raised.Severity);
        Assert.Equal("c_test", raised.ConversationId);
        Assert.Contains("c_test", raised.Link);
    }

    // Registration must not depend on the notification succeeding: asking a question cannot be
    // allowed to fail because the thing that reports the question failed.
    [Fact]
    public void A_throwing_publisher_does_not_break_registration()
    {
        var registry = new AskUserResponseRegistry(new ThrowingPublisher());

        var (requestId, task) = registry.Register(Conversation(), timeout: null);

        Assert.NotEmpty(requestId);
        Assert.NotNull(task);
        Assert.False(task.IsCompleted);
    }

    // The registry is constructed in many places that know nothing about notifications.
    [Fact]
    public void A_registry_without_a_publisher_still_registers()
    {
        var registry = new AskUserResponseRegistry();

        var (requestId, _) = registry.Register(Conversation(), timeout: null);

        Assert.NotEmpty(requestId);
    }

    // One pending prompt per conversation is an existing invariant; raising a notification must not
    // have loosened it, and a refused registration must not report a question nobody was asked.
    [Fact]
    public void A_refused_duplicate_registration_raises_nothing_further()
    {
        var publisher = new RecordingPublisher();
        var registry = new AskUserResponseRegistry(publisher);
        registry.Register(Conversation(), timeout: null);

        Assert.Throws<InvalidOperationException>(() => registry.Register(Conversation(), timeout: null));

        Assert.Single(publisher.Published);
    }

    private sealed class RecordingPublisher : INotificationPublisher
    {
        private readonly List<Notification> _published = [];

        public IReadOnlyList<Notification> Published
        {
            get
            {
                lock (_published)
                    return [.. _published];
            }
        }

        public Task PublishAsync(Notification notification, CancellationToken ct = default)
        {
            lock (_published)
                _published.Add(notification);

            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingPublisher : INotificationPublisher
    {
        public Task PublishAsync(Notification notification, CancellationToken ct = default) =>
            throw new InvalidOperationException("notification store is unavailable");
    }
}
