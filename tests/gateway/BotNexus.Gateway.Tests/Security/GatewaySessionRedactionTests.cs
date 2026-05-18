using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using Moq;

namespace BotNexus.Gateway.Tests.Security;

/// <summary>
/// Verifies that <see cref="GatewaySession"/> applies the injected <see cref="ISecretRedactor"/>
/// to all content written via <c>AddEntry</c> and <c>AddEntries</c>.
/// </summary>
public sealed class GatewaySessionRedactionTests
{
    [Fact]
    public void AddEntry_WithRedactor_RedactsContent()
    {
        var redactor = new Mock<ISecretRedactor>();
        redactor.Setup(r => r.Redact(It.IsAny<string>())).Returns("[REDACTED]");

        var session = new GatewaySession(new Session(), redactor.Object);
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "my secret" });

        var history = session.GetHistorySnapshot();
        history.Count.ShouldBe(1);
        history[0].Content.ShouldBe("[REDACTED]");
    }

    [Fact]
    public void AddEntry_WithoutRedactor_ContentUnchanged()
    {
        var session = new GatewaySession(new Session());
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "safe text" });

        var history = session.GetHistorySnapshot();
        history[0].Content.ShouldBe("safe text");
    }

    [Fact]
    public void AddEntries_WithRedactor_RedactsAllContent()
    {
        var redactor = new Mock<ISecretRedactor>();
        redactor.Setup(r => r.Redact(It.IsAny<string>())).Returns("[REDACTED]");

        var session = new GatewaySession(new Session(), redactor.Object);
        session.AddEntries([
            new SessionEntry { Role = MessageRole.User, Content = "secret1" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "secret2" }
        ]);

        var history = session.GetHistorySnapshot();
        history.Count.ShouldBe(2);
        history.ShouldAllBe(e => e.Content == "[REDACTED]");
    }

    [Fact]
    public void AddEntry_RedactorCalledOnce_PerEntry()
    {
        var redactor = new Mock<ISecretRedactor>();
        redactor.Setup(r => r.Redact(It.IsAny<string>())).Returns<string>(s => s);

        var session = new GatewaySession(new Session(), redactor.Object);
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "hello" });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "world" });

        redactor.Verify(r => r.Redact(It.IsAny<string>()), Times.Exactly(2));
    }

    [Fact]
    public void AddEntry_RedactsToolArgs_WhenPresent()
    {
        var redactor = new Mock<ISecretRedactor>();
        redactor.Setup(r => r.Redact(It.IsAny<string>())).Returns("[REDACTED]");

        var session = new GatewaySession(new Session(), redactor.Object);
        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.Assistant,
            Content = "calling tool",
            ToolArgs = "{\"api_key\": \"secret-key\"}"
        });

        var history = session.GetHistorySnapshot();
        history[0].ToolArgs.ShouldBe("[REDACTED]");
    }

    [Fact]
    public void AddEntry_NullContent_HandledGracefully()
    {
        // Content is required but we want to ensure no NullReferenceException when
        // the redactor is wired; an empty string is the safe fallback.
        var redactor = new Mock<ISecretRedactor>();
        redactor.Setup(r => r.Redact(string.Empty)).Returns(string.Empty);

        var session = new GatewaySession(new Session(), redactor.Object);
        var act = () => session.AddEntry(new SessionEntry { Role = MessageRole.System, Content = string.Empty });
        act.ShouldNotThrow();
    }
}
