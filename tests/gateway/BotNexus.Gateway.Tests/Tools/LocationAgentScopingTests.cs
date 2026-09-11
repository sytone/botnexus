using System.Reflection;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Tools;
using Microsoft.Extensions.Options;
using Shouldly;

namespace BotNexus.Gateway.Tests.Tools;

/// <summary>
/// Locations are scoped to agents (Plan 3·7).
///
/// <remarks>
/// The connection registry already held the harder property — an agent never receives a
/// credential, and an architecture fence proves it. What it did not hold is scope: every agent
/// with <c>list_locations</c> saw every configured location, so an agent that legitimately needed
/// one internal API could also enumerate the Proxmox host, the NAS and the database. Not their
/// passwords, but an endpoint plus a username is most of a targeting problem.
/// </remarks>
/// </summary>
public sealed class LocationAgentScopingTests
{
    // ── The rule ──────────────────────────────────────────────────────────

    [Fact]
    public void An_absent_list_means_every_agent()
    {
        // The upgrade default, and the reason adding the field changed nothing: no operator has
        // written a list yet, so every location stays visible exactly as before.
        var location = new LocationConfig { Type = "api", Agents = null };

        LocationAccessPolicy.IsVisibleTo(location, AgentId.From("gantry-manager")).ShouldBeTrue();
        LocationAccessPolicy.IsVisibleTo(location, AgentId.From("anyone-at-all")).ShouldBeTrue();
    }

    [Fact]
    public void A_wildcard_means_every_agent_explicitly()
    {
        var location = new LocationConfig { Type = "api", Agents = ["*"] };

        LocationAccessPolicy.IsVisibleTo(location, AgentId.From("gantry-manager")).ShouldBeTrue();
    }

    [Fact]
    public void An_empty_list_grants_nobody()
    {
        // Distinct from absent, deliberately: it is how a location is taken out of circulation
        // without deleting its configuration, and it cannot be reached by accident because it
        // requires writing the key.
        var location = new LocationConfig { Type = "api", Agents = [] };

        LocationAccessPolicy.IsVisibleTo(location, AgentId.From("gantry-manager")).ShouldBeFalse();
    }

    [Fact]
    public void A_named_list_admits_only_those_agents()
    {
        var location = new LocationConfig { Type = "api", Agents = ["gantry-manager"] };

        LocationAccessPolicy.IsVisibleTo(location, AgentId.From("gantry-manager")).ShouldBeTrue();
        LocationAccessPolicy.IsVisibleTo(location, AgentId.From("harbor-relay")).ShouldBeFalse();
    }

    [Theory]
    [InlineData(" gantry-manager ")]
    [InlineData("GANTRY-MANAGER")]
    public void Grants_survive_whitespace_and_casing(string configured)
    {
        // An operator typing into a config form produces both.
        var location = new LocationConfig { Type = "api", Agents = [configured] };

        LocationAccessPolicy.IsVisibleTo(location, AgentId.From("gantry-manager")).ShouldBeTrue();
    }

    [Fact]
    public void An_unidentified_caller_matches_no_named_grant()
    {
        // A tool constructed without an agent id must not fall through a named list. It still
        // sees a wildcard location, because one marked "everyone" genuinely is.
        //
        // There is no whitespace case to test any more: the parameter is AgentId, and AgentId.From
        // rejects blank input, so "identified by a blank string" is a state the type will not
        // construct. That is the point of carrying the value object rather than a string.
        LocationAccessPolicy.IsVisibleTo(new LocationConfig { Agents = ["gantry-manager"] }, null).ShouldBeFalse();
        
        LocationAccessPolicy.IsVisibleTo(new LocationConfig { Agents = ["*"] }, null).ShouldBeTrue();
    }

    // ── The tool actually applies it ──────────────────────────────────────

    [Fact]
    public async Task An_agent_sees_only_the_locations_it_was_granted()
    {
        // THE test. Before this, list_locations was constructed with the config alone — no agent
        // id reached it, so it could not filter even in principle.
        var tool = Build("gantry-manager", new()
        {
            ["shared-api"]   = new LocationConfig { Type = "api", Endpoint = "https://api.lan" },
            ["gantry-only"]  = new LocationConfig { Type = "api", Endpoint = "https://gantry.lan", Agents = ["gantry-manager"] },
            ["harbor-only"]  = new LocationConfig { Type = "api", Endpoint = "https://harbor.lan", Agents = ["harbor-relay"] }
        });

        var names = await NamesFrom(tool);

        names.ShouldBe(["gantry-only", "shared-api"]);
    }

    [Fact]
    public async Task A_withheld_location_leaves_no_trace_in_the_result()
    {
        // Filtered BEFORE projection, so nothing about the withheld entry — not its endpoint, not
        // its username, not a placeholder — reaches the payload.
        var tool = Build("harbor-relay", new()
        {
            ["secret-node"] = new LocationConfig
            {
                Type = "remote-node",
                Endpoint = "https://pve.example.lan:8006",
                Username = "automation@pve",
                Agents = ["gantry-manager"]
            }
        });

        var json = await JsonFrom(tool);

        json.ShouldNotContain("secret-node");
        json.ShouldNotContain("pve.example.lan");
        json.ShouldNotContain("automation@pve");
    }

    [Fact]
    public async Task Nothing_changes_for_a_gateway_that_configured_no_lists()
    {
        // The chosen default. An install that never writes an `agents` list keeps seeing
        // everything, which is the behaviour that shipped.
        var tool = Build("any-agent", new()
        {
            ["a"] = new LocationConfig { Type = "api", Endpoint = "https://a.lan" },
            ["b"] = new LocationConfig { Type = "api", Endpoint = "https://b.lan" }
        });

        (await NamesFrom(tool)).ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task Scoping_composes_with_the_existing_filters_rather_than_replacing_them()
    {
        // A type filter must not widen the scope: an ungranted location stays hidden however the
        // agent narrows its query.
        var tool = Build("harbor-relay", new()
        {
            ["granted-db"]   = new LocationConfig { Type = "database", Description = "ledger", Agents = ["harbor-relay"] },
            ["ungranted-db"] = new LocationConfig { Type = "database", Description = "ledger", Agents = ["gantry-manager"] }
        });

        var json = await JsonFrom(tool, new Dictionary<string, object?> { ["type"] = "database", ["filter"] = "ledger" });

        json.ShouldContain("granted-db");
        json.ShouldNotContain("ungranted-db");
    }


    // ── The REST round-trip, which is where this could silently widen ─────

    [Fact]
    public void An_edit_that_does_not_mention_the_access_list_must_not_drop_it()
    {
        // Caught by LocationUpdateRoundTripFenceTests before it could ship, and worth its own
        // behavioural test because the consequence is not "a setting is lost". `agents` is absent
        // means EVERY agent, so a PUT that only touched the description would silently re-expose a
        // location that had been narrowed to one agent. Widening access is the worst direction for
        // a silent bug, which is why the fence exists and why preservation is the only correct
        // disposition for a field UpsertLocationRequest does not model.
        var stored = new LocationConfig
        {
            Type = "remote-node",
            Endpoint = "https://pve.example.lan:8006",
            Username = "automation@pve",
            CredentialRef = "env:PVE_TOKEN",
            Agents = ["gantry-manager"]
        };

        var updated = CloneForUpdateViaReflection(stored);

        updated.Agents.ShouldBe(["gantry-manager"]);
    }

    /// <summary>
    /// Reaches the controller's private clone helper, which is the exact code the round-trip fence
    /// governs. Calling it directly beats driving a whole HTTP request to observe one field.
    /// </summary>
    private static LocationConfig CloneForUpdateViaReflection(LocationConfig existing)
    {
        var method = typeof(BotNexus.Gateway.Api.Controllers.LocationsController)
            .GetMethod("CloneForUpdate", BindingFlags.NonPublic | BindingFlags.Static);
        method.ShouldNotBeNull("CloneForUpdate has been renamed - the round-trip fence names it too");
        return (LocationConfig)method.Invoke(null, [existing])!;
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static ListLocationsTool Build(string agentId, Dictionary<string, LocationConfig> locations) =>
        new(new StaticOptionsMonitor(new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig { Locations = locations }
        }), AgentId.From(agentId));

    private static async Task<string> JsonFrom(
        ListLocationsTool tool, IReadOnlyDictionary<string, object?>? arguments = null)
    {
        var result = await tool.ExecuteAsync("call-1", arguments ?? new Dictionary<string, object?>());
        return string.Concat(result.Content.Select(c => c.Value));
    }

    private static async Task<IReadOnlyList<string>> NamesFrom(ListLocationsTool tool)
    {
        using var document = JsonDocument.Parse(await JsonFrom(tool));
        return [.. document.RootElement.EnumerateArray().Select(e => e.GetProperty("name").GetString()!)];
    }

    private sealed class StaticOptionsMonitor(PlatformConfig value) : IOptionsMonitor<PlatformConfig>
    {
        public PlatformConfig CurrentValue => value;
        public PlatformConfig Get(string? name) => value;
        public IDisposable? OnChange(Action<PlatformConfig, string?> listener) => null;
    }
}
