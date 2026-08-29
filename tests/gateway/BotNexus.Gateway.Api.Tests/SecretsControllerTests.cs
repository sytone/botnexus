using System.Reflection;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace BotNexus.Gateway.Api.Tests;

/// <summary>
/// Contract tests for <see cref="SecretsController"/> (#3528), including the reflective proof that
/// the controller exposes no read-value action (AC3).
/// </summary>
public sealed class SecretsControllerTests
{
    private const string Sentinel = "SENTINEL-c8f2a71d-DO-NOT-LEAK-THIS-VALUE";

    /// <summary>
    /// In-memory store standing in for the filesystem. Deliberately records the values it is given
    /// so a leak test can assert the value was genuinely present to leak.
    /// </summary>
    private sealed class FakeStore : IFileSecretStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public HashSet<string> InvalidKeys { get; } = new(StringComparer.Ordinal);

        public string SecretsDirectory => "/fake/secrets";

        public IReadOnlyList<SecretDescriptor> List() => Values
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new SecretDescriptor(
                kv.Key, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, kv.Value.Length))
            .ToList();

        public SecretDescriptor Set(string key, string value)
        {
            Guard(key);
            Values[key] = value;
            return new SecretDescriptor(key, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, value.Length);
        }

        public bool Delete(string key)
        {
            Guard(key);
            return Values.Remove(key);
        }

        public bool Exists(string key)
        {
            Guard(key);
            return Values.ContainsKey(key);
        }

        private void Guard(string key)
        {
            if (InvalidKeys.Contains(key))
                throw new InvalidSecretKeyException(key);
        }
    }

    private static SecretsController NewController(FakeStore store) =>
        new(store, NullLogger<SecretsController>.Instance);

    [Fact]
    public void Controller_exposes_no_action_that_can_return_a_secret_value()
    {
        // AC3. Enumerated by reflection over the PUBLIC ACTION SURFACE rather than asserted by
        // reading the file: the property this protects is "no route returns content", and a route
        // added a year from now is exactly the case a source review will not catch.
        var actions = typeof(SecretsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .ToList();

        actions.ShouldNotBeEmpty("Vacuity guard: if no actions are discovered the assertion below " +
                                 "would pass on an empty set and prove nothing.");

        foreach (var action in actions)
        {
            var returned = UnwrapReturnType(action.ReturnType);

            returned.ShouldNotBe(typeof(string),
                $"SecretsController.{action.Name} returns a bare string. The write-only contract of " +
                "#3528 means no action may return secret content; recovering a value requires " +
                "filesystem access on the host, by design.");

            // A descriptor is metadata-only by construction (see SecretDescriptor), so returning it
            // is safe. Anything else returning content-shaped data must be justified deliberately.
            var isAllowed = returned == typeof(void)
                            || returned == typeof(SecretDescriptor)
                            || typeof(IEnumerable<SecretDescriptor>).IsAssignableFrom(returned)
                            || typeof(IActionResult).IsAssignableFrom(returned);

            isAllowed.ShouldBeTrue(
                $"SecretsController.{action.Name} returns {returned.Name}, which is neither void, " +
                "a metadata-only SecretDescriptor projection, nor a bare IActionResult. A new " +
                "return shape on this controller needs an explicit decision about whether it can " +
                "carry secret content. See #3528 AC3.");
        }
    }

    private static Type UnwrapReturnType(Type type)
    {
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(Task<>) || definition == typeof(ActionResult<>))
                return UnwrapReturnType(type.GetGenericArguments()[0]);
        }

        return type;
    }

    [Fact]
    public void List_response_contains_no_trace_of_the_stored_value()
    {
        // AC2, non-vacuously: the sentinel is genuinely stored first, so an empty or broken list
        // cannot make this pass by accident - the count assertion pins that.
        var store = new FakeStore();
        store.Set("api-key", Sentinel);

        var result = NewController(store).List();

        var listed = (result.Result as OkObjectResult)?.Value as IReadOnlyList<SecretDescriptor>;
        listed.ShouldNotBeNull();
        listed.Count.ShouldBe(1);

        var serialised = System.Text.Json.JsonSerializer.Serialize(listed);
        serialised.ShouldContain("api-key", Case.Sensitive, "the key itself must be listed");
        serialised.ShouldNotContain(Sentinel);
        serialised.ShouldNotContain("SENTINEL");
        serialised.ShouldNotContain(Sentinel[..8]);
    }

    [Fact]
    public void Set_stores_the_value_and_returns_metadata_only()
    {
        var store = new FakeStore();

        var result = NewController(store).Set("api-key", new SecretWriteRequest(Sentinel));

        store.Values["api-key"].ShouldBe(Sentinel);
        var descriptor = (result.Result as OkObjectResult)?.Value as SecretDescriptor;
        descriptor.ShouldNotBeNull();
        descriptor.Key.ShouldBe("api-key");
        System.Text.Json.JsonSerializer.Serialize(descriptor).ShouldNotContain(Sentinel);
    }

    [Fact]
    public void Set_overwrites_an_existing_key_with_the_full_new_value()
    {
        // AC7 at the API boundary.
        var store = new FakeStore();
        store.Set("api-key", Sentinel);

        NewController(store).Set("api-key", new SecretWriteRequest("brand-new-value"));

        store.Values["api-key"].ShouldBe("brand-new-value");
        store.Values["api-key"].ShouldNotContain(Sentinel);
    }

    [Fact]
    public void Set_with_a_rejected_key_returns_bad_request_and_stores_nothing()
    {
        var store = new FakeStore();
        store.InvalidKeys.Add("../escape");

        var result = NewController(store).Set("../escape", new SecretWriteRequest(Sentinel));

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
        store.Values.ShouldBeEmpty();
    }

    [Fact]
    public void Set_with_a_missing_body_returns_bad_request()
    {
        var store = new FakeStore();

        var result = NewController(store).Set("api-key", null!);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
        store.Values.ShouldBeEmpty();
    }

    [Fact]
    public void Delete_removes_the_key_and_the_list_no_longer_reports_it()
    {
        // AC9 at the API boundary.
        var store = new FakeStore();
        store.Set("doomed", Sentinel);
        var controller = NewController(store);

        controller.Delete("doomed").ShouldBeOfType<NoContentResult>();

        var listed = (controller.List().Result as OkObjectResult)?.Value as IReadOnlyList<SecretDescriptor>;
        listed.ShouldNotBeNull();
        listed.ShouldBeEmpty();
    }

    [Fact]
    public void Delete_of_an_absent_key_returns_not_found()
    {
        NewController(new FakeStore()).Delete("never-existed").ShouldBeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public void Delete_with_a_rejected_key_returns_bad_request()
    {
        var store = new FakeStore();
        store.InvalidKeys.Add("../config.json");

        NewController(store).Delete("../config.json").ShouldBeOfType<BadRequestObjectResult>();
    }
}
