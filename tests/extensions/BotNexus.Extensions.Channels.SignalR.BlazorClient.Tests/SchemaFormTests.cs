using System.Text.Json.Nodes;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// bUnit tests for <see cref="SchemaForm"/> (config-parity PBI 3/6 of #1579, issue #1611): a generic
/// renderer that walks the UI-schema envelope from <c>GET /api/config/schema</c> (ConfigSchemaBuilder,
/// PBI2 #1610) and draws an editor per node -- text/bool/number/select/secret widgets, nested objects,
/// dictionaries, and lists -- grouped + ordered, with field-level validation, two-way bound back to a
/// config JSON object for PUT. Scoped to the renderer only (panel migration is PBI4).
///
/// The envelope is { schemaVersion, root, schema:{ type:"object", properties:{...} } } where each
/// property carries x-ui-label/widget/group/order/secret/validation/options/default overlays. Tests
/// build minimal authentic-shaped schemas (matching ConfigSchemaBuilder output) and a parallel value
/// object, then assert markup, masking, grouping, validation, and write-back.
/// </summary>
public sealed class SchemaFormTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public SchemaFormTests()
    {
        // SchemaForm injects IModelOptionsProvider (#1893). Default to an empty stub; tests that
        // exercise dynamic options register their own via ReplaceModels().
        _ctx.Services.AddSingleton<IModelOptionsProvider>(new StubModelOptionsProvider());
    }

    private void ReplaceModels(IReadOnlyDictionary<string, IReadOnlyList<ModelOption>> byProvider)
    {
        _ctx.Services.AddSingleton<IModelOptionsProvider>(new StubModelOptionsProvider(byProvider));
    }

    private sealed class StubModelOptionsProvider : IModelOptionsProvider
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<ModelOption>> _byProvider;
        public StubModelOptionsProvider(IReadOnlyDictionary<string, IReadOnlyList<ModelOption>>? byProvider = null)
            => _byProvider = byProvider ?? new Dictionary<string, IReadOnlyList<ModelOption>>();
        public Task<IReadOnlyList<ModelOption>> GetModelsAsync(string provider)
            => Task.FromResult(_byProvider.TryGetValue(provider, out var m) ? m : []);
    }

    public void Dispose() => _ctx.Dispose();

    // -- Helpers -------------------------------------------------------------

    private static JsonObject Scalar(string type, string widget, string label, int order = 0, string? group = null)
    {
        var o = new JsonObject { ["type"] = type, ["x-ui-widget"] = widget, ["x-ui-label"] = label, ["x-ui-order"] = order };
        if (group is not null) o["x-ui-group"] = group;
        return o;
    }

    private static JsonObject Envelope(JsonObject properties) => new()
    {
        ["schemaVersion"] = "1.0",
        ["root"] = "PlatformConfig",
        ["schema"] = new JsonObject { ["type"] = "object", ["properties"] = properties },
    };

    private IRenderedComponent<SchemaForm> Render(JsonObject schema, JsonObject value, Action<JsonObject>? onChange = null)
        => _ctx.Render<SchemaForm>(p =>
        {
            p.Add(c => c.Schema, schema);
            p.Add(c => c.Value, value);
            if (onChange is not null)
                p.Add(c => c.ValueChanged, EventCallback.Factory.Create(this, onChange));
        });

    // -- 1. Renders all widget types ----------------------------------------

    [Fact]
    public void Renders_text_field()
    {
        var schema = Envelope(new JsonObject { ["listenUrl"] = Scalar("string", "text", "Listen URL") });
        var cut = Render(schema, new JsonObject { ["listenUrl"] = "http://x" });

        var input = cut.Find("[data-testid='field-listenUrl'] input[type='text']");
        Assert.Equal("http://x", input.GetAttribute("value"));
        Assert.Contains("Listen URL", cut.Markup);
    }

    [Fact]
    public void Renders_bool_field_as_checkbox()
    {
        var schema = Envelope(new JsonObject { ["enabled"] = Scalar("boolean", "toggle", "Enabled") });
        var cut = Render(schema, new JsonObject { ["enabled"] = true });

        var box = cut.Find("[data-testid='field-enabled'] input[type='checkbox']");
        Assert.True(box.HasAttribute("checked"));
    }

    [Fact]
    public void Renders_number_field()
    {
        var schema = Envelope(new JsonObject { ["port"] = Scalar("integer", "number", "Port") });
        var cut = Render(schema, new JsonObject { ["port"] = 60 });

        cut.Find("[data-testid='field-port'] input[type='number']");
    }

    [Fact]
    public void Renders_select_field_with_options()
    {
        var node = Scalar("string", "select", "Retention");
        node["x-ui-options"] = new JsonArray("none", "short", "long");
        var schema = Envelope(new JsonObject { ["cacheRetention"] = node });
        var cut = Render(schema, new JsonObject { ["cacheRetention"] = "short" });

        var opts = cut.FindAll("[data-testid='field-cacheRetention'] select option");
        // 3 enum options + a leading blank sentinel.
        Assert.Equal(4, opts.Count);
        Assert.Contains("short", cut.Markup);
    }

    [Fact]
    public void Renders_secret_field_masked()
    {
        var node = Scalar("string", "secret", "API key");
        node["x-ui-secret"] = true;
        var schema = Envelope(new JsonObject { ["apiKey"] = node });
        var cut = Render(schema, new JsonObject { ["apiKey"] = "***" });

        var input = cut.Find("[data-testid='field-apiKey'] input");
        Assert.Equal("password", input.GetAttribute("type"));
    }

    // -- 2. Nested objects / dicts / lists ----------------------------------

    [Fact]
    public void Renders_nested_object_recursively()
    {
        var inner = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["enabled"] = Scalar("boolean", "toggle", "Enabled") } };
        var gateway = new JsonObject { ["type"] = "object", ["x-ui-label"] = "Gateway", ["properties"] = new JsonObject { ["dateTimeInjection"] = inner } };
        var schema = Envelope(new JsonObject { ["gateway"] = gateway });
        var cut = Render(schema, new JsonObject { ["gateway"] = new JsonObject { ["dateTimeInjection"] = new JsonObject { ["enabled"] = true } } });

        cut.Find("[data-testid='field-gateway.dateTimeInjection.enabled'] input[type='checkbox']");
    }

    [Fact]
    public void Renders_dictionary_entries_via_additionalProperties()
    {
        var valueSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["apiKey"] = Scalar("string", "text", "API key") } };
        var providers = new JsonObject { ["type"] = "object", ["x-ui-label"] = "Providers", ["additionalProperties"] = valueSchema };
        var schema = Envelope(new JsonObject { ["providers"] = providers });
        var value = new JsonObject { ["providers"] = new JsonObject { ["openai"] = new JsonObject { ["apiKey"] = "k" } } };
        var cut = Render(schema, value);

        cut.Find("[data-testid='field-providers.openai.apiKey'] input");
        Assert.Contains("openai", cut.Markup);
    }

    [Fact]
    public void Renders_list_string_items()
    {
        var models = new JsonObject { ["type"] = "array", ["x-ui-label"] = "Models", ["items"] = new JsonObject { ["type"] = "string", ["x-ui-widget"] = "text" } };
        var schema = Envelope(new JsonObject { ["models"] = models });
        var cut = Render(schema, new JsonObject { ["models"] = new JsonArray("a", "b") });

        Assert.Equal(2, cut.FindAll("[data-testid^='field-models['] input").Count);
    }

    // -- 3. Grouping + ordering ---------------------------------------------

    [Fact]
    public void Groups_and_orders_fields()
    {
        var schema = Envelope(new JsonObject
        {
            ["b"] = Scalar("string", "text", "Beta", order: 2, group: "general"),
            ["a"] = Scalar("string", "text", "Alpha", order: 1, group: "general"),
        });
        var cut = Render(schema, new JsonObject { ["a"] = "1", ["b"] = "2" });

        cut.Find(".schema-group[data-group='general']");
        var ids = cut.FindAll("[data-testid^='field-']").Select(e => e.GetAttribute("data-testid")).ToList();
        Assert.True(ids.IndexOf("field-a") < ids.IndexOf("field-b"), "Alpha (order 1) must render before Beta (order 2)");
    }

    // -- 4. Client-side validation ------------------------------------------

    [Fact]
    public void Number_below_minimum_shows_validation_error()
    {
        var node = Scalar("integer", "number", "Tick");
        node["x-ui-validation"] = new JsonObject { ["minimum"] = 1 };
        var schema = Envelope(new JsonObject { ["tickIntervalSeconds"] = node });
        var cut = Render(schema, new JsonObject { ["tickIntervalSeconds"] = 60 });

        cut.Find("[data-testid='field-tickIntervalSeconds'] input").Change("0");
        Assert.Contains("schema-field-error", cut.Markup);
    }

    // -- 5. Two-way bind back to config JSON --------------------------------

    [Fact]
    public void Editing_text_writes_back_to_value()
    {
        JsonObject? updated = null;
        var schema = Envelope(new JsonObject { ["listenUrl"] = Scalar("string", "text", "Listen URL") });
        var cut = Render(schema, new JsonObject { ["listenUrl"] = "old" }, v => updated = v);

        cut.Find("[data-testid='field-listenUrl'] input").Change("new");

        Assert.NotNull(updated);
        Assert.Equal("new", updated["listenUrl"]!.GetValue<string>());
    }

    [Fact]
    public void Toggling_bool_writes_back_to_value()
    {
        JsonObject? updated = null;
        var schema = Envelope(new JsonObject { ["enabled"] = Scalar("boolean", "toggle", "Enabled") });
        var cut = Render(schema, new JsonObject { ["enabled"] = false }, v => updated = v);

        cut.Find("[data-testid='field-enabled'] input").Change(true);

        Assert.NotNull(updated);
        Assert.True(updated["enabled"]!.GetValue<bool>());
    }

    // -- 6. Section navigation (#1892) --------------------------------------

    private static JsonObject TwoSectionSchema() => Envelope(new JsonObject
    {
        ["gateway"] = new JsonObject
        {
            ["type"] = "object",
            ["x-ui-label"] = "Gateway",
            ["x-ui-order"] = 0,
            ["properties"] = new JsonObject { ["listenUrl"] = Scalar("string", "text", "Listen URL") },
        },
        ["cron"] = new JsonObject
        {
            ["type"] = "object",
            ["x-ui-label"] = "Cron",
            ["x-ui-order"] = 1,
            ["properties"] = new JsonObject { ["tickIntervalSeconds"] = Scalar("integer", "number", "Tick") },
        },
    });

    private static JsonObject TwoSectionValue() => new()
    {
        ["gateway"] = new JsonObject { ["listenUrl"] = "http://x" },
        ["cron"] = new JsonObject { ["tickIntervalSeconds"] = 60 },
    };

    [Fact]
    public void SectionKey_renders_only_the_selected_section()
    {
        var cut = _ctx.Render<SchemaForm>(p =>
        {
            p.Add(c => c.Schema, TwoSectionSchema());
            p.Add(c => c.Value, TwoSectionValue());
            p.Add(c => c.SectionKey, "cron");
        });

        cut.Find("[data-testid='field-cron.tickIntervalSeconds'] input");
        Assert.Empty(cut.FindAll("[data-testid='field-gateway.listenUrl'] input"));
    }

    [Fact]
    public void SectionKey_null_renders_the_whole_tree()
    {
        var cut = _ctx.Render<SchemaForm>(p =>
        {
            p.Add(c => c.Schema, TwoSectionSchema());
            p.Add(c => c.Value, TwoSectionValue());
        });

        cut.Find("[data-testid='field-gateway.listenUrl'] input");
        cut.Find("[data-testid='field-cron.tickIntervalSeconds'] input");
    }

    [Fact]
    public void SectionKey_unknown_falls_back_to_whole_tree()
    {
        var cut = _ctx.Render<SchemaForm>(p =>
        {
            p.Add(c => c.Schema, TwoSectionSchema());
            p.Add(c => c.Value, TwoSectionValue());
            p.Add(c => c.SectionKey, "does-not-exist");
        });

        cut.Find("[data-testid='field-gateway.listenUrl'] input");
        cut.Find("[data-testid='field-cron.tickIntervalSeconds'] input");
    }

    [Fact]
    public void RootSections_lists_sections_ordered_by_ui_order()
    {
        var cut = _ctx.Render<SchemaForm>(p =>
        {
            p.Add(c => c.Schema, TwoSectionSchema());
            p.Add(c => c.Value, TwoSectionValue());
        });

        var sections = cut.Instance.RootSections();
        Assert.Equal(new[] { "gateway", "cron" }, sections.Select(s => s.Key).ToArray());
        Assert.Equal("Gateway", sections[0].Label);
    }

    // -- 7. Dynamic option sources (#1893) ----------------------------------

    // A providers dictionary whose entry value has a defaultModel select sourced from "models".
    private static JsonObject ProvidersSchema(string optionsSource = "models")
    {
        var defaultModel = Scalar("string", "select", "Default model");
        defaultModel["x-ui-options-source"] = optionsSource;
        var valueSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["defaultModel"] = defaultModel },
        };
        var providers = new JsonObject { ["type"] = "object", ["x-ui-label"] = "Providers", ["additionalProperties"] = valueSchema };
        return Envelope(new JsonObject { ["providers"] = providers });
    }

    private static JsonObject ProvidersValue(string? selected = null) => new()
    {
        ["providers"] = new JsonObject { ["openai"] = new JsonObject { ["defaultModel"] = selected } },
    };

    [Fact]
    public void Model_select_populates_from_provider_model_list()
    {
        ReplaceModels(new Dictionary<string, IReadOnlyList<ModelOption>>
        {
            ["openai"] = new[]
            {
                new ModelOption("gpt-4.1", "GPT-4.1", ["low", "high"], [128000]),
                new ModelOption("gpt-4o-mini", "GPT-4o mini", [], [128000]),
            },
        });

        var cut = _ctx.Render<SchemaForm>(p =>
        {
            p.Add(c => c.Schema, ProvidersSchema());
            p.Add(c => c.Value, ProvidersValue());
        });

        // Wait for the async model fetch + re-render to surface the dynamic options.
        cut.WaitForAssertion(() =>
        {
            var opts = cut.FindAll("[data-testid='field-providers.openai.defaultModel'] option")
                .Select(o => o.GetAttribute("value")).ToList();
            Assert.Contains("gpt-4.1", opts);
            Assert.Contains("gpt-4o-mini", opts);
        });
    }

    [Fact]
    public void Model_select_falls_back_to_static_options_when_no_dynamic_list()
    {
        // No models registered for the provider -> select uses static x-ui-options.
        var schema = ProvidersSchema();
        schema["schema"]!["properties"]!["providers"]!["additionalProperties"]!["properties"]!["defaultModel"]!["x-ui-options"]
            = new JsonArray("static-a", "static-b");

        var cut = _ctx.Render<SchemaForm>(p =>
        {
            p.Add(c => c.Schema, schema);
            p.Add(c => c.Value, ProvidersValue());
        });

        var opts = cut.FindAll("[data-testid='field-providers.openai.defaultModel'] option")
            .Select(o => o.GetAttribute("value")).ToList();
        Assert.Contains("static-a", opts);
        Assert.Contains("static-b", opts);
    }

    // -- 8. Lossless path traversal through arrays (#2062 core defect) -------

    // Regression for the core defect: Set() only walked JsonObject intermediates, so a path
    // through a JsonArray element silently wrote into a fresh graph and lost the edit. Editing a
    // scalar field inside an array-of-objects entry must write back to the SAME array element.
    [Fact]
    public void Editing_field_through_array_element_writes_back_lossless()
    {
        JsonObject? updated = null;
        var itemSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["name"] = Scalar("string", "text", "Name") },
        };
        var models = new JsonObject { ["type"] = "array", ["x-ui-label"] = "Items", ["items"] = itemSchema };
        var schema = Envelope(new JsonObject { ["items"] = models });
        var value = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["name"] = "a" },
                new JsonObject { ["name"] = "b" }),
        };
        var cut = Render(schema, value, v => updated = v);

        cut.Find("[data-testid='field-items[1].name'] input").Change("b-edited");

        Assert.NotNull(updated);
        // The sibling element must be untouched and the edited element updated in place.
        Assert.Equal("a", updated["items"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("b-edited", updated["items"]![1]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Editing_nested_dictionary_of_objects_writes_back_without_clobbering_siblings()
    {
        JsonObject? updated = null;
        var valueSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["apiKey"] = Scalar("string", "text", "API key") } };
        var providers = new JsonObject { ["type"] = "object", ["x-ui-label"] = "Providers", ["additionalProperties"] = valueSchema };
        var schema = Envelope(new JsonObject { ["providers"] = providers });
        var value = new JsonObject
        {
            ["providers"] = new JsonObject
            {
                ["openai"] = new JsonObject { ["apiKey"] = "o" },
                ["anthropic"] = new JsonObject { ["apiKey"] = "a" },
            },
        };
        var cut = Render(schema, value, v => updated = v);

        cut.Find("[data-testid='field-providers.anthropic.apiKey'] input").Change("a-new");

        Assert.NotNull(updated);
        Assert.Equal("o", updated["providers"]!["openai"]!["apiKey"]!.GetValue<string>());
        Assert.Equal("a-new", updated["providers"]!["anthropic"]!["apiKey"]!.GetValue<string>());
    }

    // -- 9. List lifecycle: add / remove / reorder --------------------------

    private static JsonObject StringListSchema()
    {
        var models = new JsonObject { ["type"] = "array", ["x-ui-label"] = "Models", ["items"] = new JsonObject { ["type"] = "string", ["x-ui-widget"] = "text" } };
        return Envelope(new JsonObject { ["models"] = models });
    }

    [Fact]
    public void Adding_a_list_item_appends_and_writes_back()
    {
        JsonObject? updated = null;
        var cut = Render(StringListSchema(), new JsonObject { ["models"] = new JsonArray("a") }, v => updated = v);

        cut.Find("[data-testid='array-add-models']").Click();

        Assert.NotNull(updated);
        Assert.Equal(2, updated["models"]!.AsArray().Count);
        Assert.Equal("a", updated["models"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Removing_a_list_item_drops_it_and_keeps_siblings()
    {
        JsonObject? updated = null;
        var cut = Render(StringListSchema(), new JsonObject { ["models"] = new JsonArray("a", "b", "c") }, v => updated = v);

        cut.Find("[data-testid='array-remove-models-1']").Click();

        Assert.NotNull(updated);
        var arr = updated["models"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "a", "c" }, arr.ToArray());
    }

    [Fact]
    public void Reordering_a_list_item_moves_it_down()
    {
        JsonObject? updated = null;
        var cut = Render(StringListSchema(), new JsonObject { ["models"] = new JsonArray("a", "b", "c") }, v => updated = v);

        cut.Find("[data-testid='array-down-models-0']").Click();

        Assert.NotNull(updated);
        var arr = updated["models"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "b", "a", "c" }, arr.ToArray());
    }

    [Fact]
    public void Reordering_a_list_item_moves_it_up()
    {
        JsonObject? updated = null;
        var cut = Render(StringListSchema(), new JsonObject { ["models"] = new JsonArray("a", "b", "c") }, v => updated = v);

        cut.Find("[data-testid='array-up-models-2']").Click();

        Assert.NotNull(updated);
        var arr = updated["models"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "a", "c", "b" }, arr.ToArray());
    }

    [Fact]
    public void Adding_object_list_item_seeds_default_child_fields()
    {
        JsonObject? updated = null;
        var itemSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["name"] = Scalar("string", "text", "Name"),
                ["enabled"] = Scalar("boolean", "toggle", "Enabled"),
            },
        };
        var arrNode = new JsonObject { ["type"] = "array", ["x-ui-label"] = "Rules", ["items"] = itemSchema };
        var schema = Envelope(new JsonObject { ["rules"] = arrNode });
        var cut = Render(schema, new JsonObject { ["rules"] = new JsonArray() }, v => updated = v);

        cut.Find("[data-testid='array-add-rules']").Click();

        Assert.NotNull(updated);
        var item = updated["rules"]![0]!.AsObject();
        Assert.True(item.ContainsKey("name"));
        Assert.True(item.ContainsKey("enabled"));
        Assert.False(item["enabled"]!.GetValue<bool>());
    }

    // -- 10. Dictionary lifecycle: add / delete / rename --------------------

    private static JsonObject ProvidersDictSchema(bool immutableKeys = false, string? keyPattern = null)
    {
        var valueSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["apiKey"] = Scalar("string", "text", "API key") } };
        var providers = new JsonObject { ["type"] = "object", ["x-ui-label"] = "Providers", ["additionalProperties"] = valueSchema };
        if (immutableKeys) providers["x-ui-immutable-keys"] = true;
        if (keyPattern is not null) providers["x-ui-key-pattern"] = keyPattern;
        return Envelope(new JsonObject { ["providers"] = providers });
    }

    [Fact]
    public void Adding_a_dictionary_entry_creates_it_with_defaulted_value()
    {
        JsonObject? updated = null;
        var value = new JsonObject { ["providers"] = new JsonObject { ["openai"] = new JsonObject { ["apiKey"] = "o" } } };
        var cut = Render(ProvidersDictSchema(), value, v => updated = v);

        cut.Find("[data-testid='dict-newkey-providers']").Change("anthropic");
        cut.Find("[data-testid='dict-add-providers']").Click();

        Assert.NotNull(updated);
        Assert.True(updated["providers"]!.AsObject().ContainsKey("anthropic"));
        // Existing sibling survives.
        Assert.Equal("o", updated["providers"]!["openai"]!["apiKey"]!.GetValue<string>());
    }

    [Fact]
    public void Adding_a_dictionary_entry_with_empty_key_shows_error_and_does_not_add()
    {
        JsonObject? updated = null;
        var value = new JsonObject { ["providers"] = new JsonObject { ["openai"] = new JsonObject { ["apiKey"] = "o" } } };
        var cut = Render(ProvidersDictSchema(), value, v => updated = v);

        cut.Find("[data-testid='dict-add-providers']").Click();

        cut.Find("[data-testid='dict-error-providers']");
        // No write-back should have added a blank key.
        Assert.Single(value["providers"]!.AsObject());
    }

    [Fact]
    public void Adding_a_duplicate_dictionary_key_shows_error()
    {
        var value = new JsonObject { ["providers"] = new JsonObject { ["openai"] = new JsonObject { ["apiKey"] = "o" } } };
        var cut = Render(ProvidersDictSchema(), value);

        cut.Find("[data-testid='dict-newkey-providers']").Change("openai");
        cut.Find("[data-testid='dict-add-providers']").Click();

        var err = cut.Find("[data-testid='dict-error-providers']");
        Assert.Contains("already exists", err.TextContent);
    }

    [Fact]
    public void Adding_a_key_violating_pattern_shows_error()
    {
        var value = new JsonObject { ["providers"] = new JsonObject() };
        var cut = Render(ProvidersDictSchema(keyPattern: "^[a-z]+$"), value);

        cut.Find("[data-testid='dict-newkey-providers']").Change("Bad Key!");
        cut.Find("[data-testid='dict-add-providers']").Click();

        cut.Find("[data-testid='dict-error-providers']");
        Assert.Empty(value["providers"]!.AsObject());
    }

    [Fact]
    public void Deleting_a_dictionary_entry_removes_only_that_entry()
    {
        JsonObject? updated = null;
        var value = new JsonObject
        {
            ["providers"] = new JsonObject
            {
                ["openai"] = new JsonObject { ["apiKey"] = "o" },
                ["anthropic"] = new JsonObject { ["apiKey"] = "a" },
            },
        };
        var cut = Render(ProvidersDictSchema(), value, v => updated = v);

        cut.Find("[data-testid='dict-delete-providers-openai']").Click();

        Assert.NotNull(updated);
        Assert.False(updated["providers"]!.AsObject().ContainsKey("openai"));
        Assert.True(updated["providers"]!.AsObject().ContainsKey("anthropic"));
    }

    [Fact]
    public void Renaming_a_dictionary_key_preserves_value_and_order()
    {
        JsonObject? updated = null;
        var value = new JsonObject
        {
            ["providers"] = new JsonObject
            {
                ["openai"] = new JsonObject { ["apiKey"] = "o" },
                ["anthropic"] = new JsonObject { ["apiKey"] = "a" },
            },
        };
        var cut = Render(ProvidersDictSchema(), value, v => updated = v);

        cut.Find("[data-testid='dict-rename-input-providers-openai']").Change("openai-2");
        cut.Find("[data-testid='dict-rename-providers-openai']").Click();

        Assert.NotNull(updated);
        var keys = updated["providers"]!.AsObject().Select(kv => kv.Key).ToList();
        Assert.Equal(new[] { "openai-2", "anthropic" }, keys.ToArray());
        Assert.Equal("o", updated["providers"]!["openai-2"]!["apiKey"]!.GetValue<string>());
    }

    [Fact]
    public void Renaming_to_an_existing_key_shows_error()
    {
        var value = new JsonObject
        {
            ["providers"] = new JsonObject
            {
                ["openai"] = new JsonObject { ["apiKey"] = "o" },
                ["anthropic"] = new JsonObject { ["apiKey"] = "a" },
            },
        };
        var cut = Render(ProvidersDictSchema(), value);

        cut.Find("[data-testid='dict-rename-input-providers-openai']").Change("anthropic");
        cut.Find("[data-testid='dict-rename-providers-openai']").Click();

        cut.Find("[data-testid='dict-error-providers']");
        Assert.True(value["providers"]!.AsObject().ContainsKey("openai"));
    }

    [Fact]
    public void Immutable_key_dictionary_hides_rename_control()
    {
        var value = new JsonObject { ["providers"] = new JsonObject { ["openai"] = new JsonObject { ["apiKey"] = "o" } } };
        var cut = Render(ProvidersDictSchema(immutableKeys: true), value);

        Assert.Empty(cut.FindAll("[data-testid='dict-rename-providers-openai']"));
        // Delete + add remain available even when keys are immutable.
        cut.Find("[data-testid='dict-delete-providers-openai']");
    }

    // -- 11. Secret-valued dictionary lifecycle -----------------------------

    [Fact]
    public void Adding_secret_valued_dictionary_entry_renders_password_widget()
    {
        var valueSchema = new JsonObject { ["type"] = "string", ["x-ui-widget"] = "secret", ["x-ui-secret"] = true, ["x-ui-label"] = "Key" };
        var apiKeys = new JsonObject { ["type"] = "object", ["x-ui-label"] = "API Keys", ["additionalProperties"] = valueSchema };
        var schema = Envelope(new JsonObject { ["apiKeys"] = apiKeys });
        var value = new JsonObject { ["apiKeys"] = new JsonObject { ["k1"] = "***" } };
        var cut = Render(schema, value);

        cut.Find("[data-testid='dict-newkey-apiKeys']").Change("k2");
        cut.Find("[data-testid='dict-add-apiKeys']").Click();

        var input = cut.Find("[data-testid='field-apiKeys.k2'] input");
        Assert.Equal("password", input.GetAttribute("type"));
    }
}
