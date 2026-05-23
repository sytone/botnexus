using BotNexus.Domain;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Citizens;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Citizens;

public sealed class DefaultUserRegistryTests
{
    [Fact]
    public void Register_WithValidUser_AddsUser()
    {
        var registry = CreateRegistry();
        var user = CreateUser("alice");

        registry.Register(user);

        registry.Get(UserId.From("alice")).ShouldBe(user);
    }

    [Fact]
    public void Unregister_WithKnownUser_RemovesUser()
    {
        var registry = CreateRegistry();
        registry.Register(CreateUser("alice"));

        registry.Unregister(UserId.From("alice"));

        registry.Contains(UserId.From("alice")).ShouldBeFalse();
    }

    [Fact]
    public void Unregister_WithUnknownUser_DoesNotThrow()
    {
        var registry = CreateRegistry();

        Action act = () => registry.Unregister(UserId.From("unknown"));

        act.ShouldNotThrow();
    }

    [Fact]
    public void Register_WithDuplicateUserId_ThrowsInvalidOperationException()
    {
        var registry = CreateRegistry();
        registry.Register(CreateUser("alice"));

        Action act = () => registry.Register(CreateUser("alice"));

        act.ShouldThrow<InvalidOperationException>();
    }

    [Fact]
    public void Update_WithExistingUser_ReplacesPayload()
    {
        var registry = CreateRegistry();
        registry.Register(CreateUser("alice", displayName: "Alice"));

        var updated = registry.Update(UserId.From("alice"), CreateUser("alice", displayName: "Alicia"));

        updated.ShouldBeTrue();
        registry.Get(UserId.From("alice"))!.DisplayName.ShouldBe("Alicia");
    }

    [Fact]
    public void Update_WithUnknownUser_ReturnsFalse()
    {
        var registry = CreateRegistry();

        var updated = registry.Update(UserId.From("alice"), CreateUser("alice"));

        updated.ShouldBeFalse();
    }

    [Fact]
    public void Update_WithMismatchedUserId_ThrowsArgumentException()
    {
        var registry = CreateRegistry();
        registry.Register(CreateUser("alice"));

        Action act = () => registry.Update(UserId.From("alice"), CreateUser("bob"));

        act.ShouldThrow<ArgumentException>();
    }

    [Fact]
    public void Get_WithUnknownUserId_ReturnsNull()
    {
        var registry = CreateRegistry();

        var user = registry.Get(UserId.From("unknown"));

        user.ShouldBeNull();
    }

    [Fact]
    public void GetAll_WithMultipleUsers_ReturnsAllRegisteredUsers()
    {
        var registry = CreateRegistry();
        registry.Register(CreateUser("alice"));
        registry.Register(CreateUser("bob"));

        var users = registry.GetAll();

        users.Count.ShouldBe(2);
    }

    [Fact]
    public void Contains_WithRegisteredAndUnknownIds_ReportsCorrectMembership()
    {
        var registry = CreateRegistry();
        registry.Register(CreateUser("alice"));

        registry.Contains(UserId.From("alice")).ShouldBeTrue();
        registry.Contains(UserId.From("unknown")).ShouldBeFalse();
    }

    [Fact]
    public async Task Register_AndRead_FromConcurrentCalls_RemainsConsistent()
    {
        var registry = CreateRegistry();
        const int userCount = 100;

        var tasks = Enumerable.Range(0, userCount)
            .Select(i => Task.Run(() =>
            {
                var userId = $"user-{i}";
                registry.Register(CreateUser(userId));
                _ = registry.Get(UserId.From(userId));
                _ = registry.Contains(UserId.From(userId));
            }));

        await Task.WhenAll(tasks);

        registry.GetAll().Count.ShouldBe(userCount);
    }

    private static DefaultUserRegistry CreateRegistry()
        => new(NullLogger<DefaultUserRegistry>.Instance);

    private static User CreateUser(string id, string? displayName = null)
        => new()
        {
            Id = UserId.From(id),
            DisplayName = displayName ?? id,
            World = new WorldIdentity { Id = "world-a", Name = "World A" },
        };
}
