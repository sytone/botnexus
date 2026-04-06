using System.Collections;
using System.CommandLine;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Gateway.Configuration;

var verboseOption = new Option<bool>("--verbose", "Show additional command output.");
var root = new RootCommand("BotNexus platform CLI");
root.AddGlobalOption(verboseOption);
root.AddCommand(BuildValidateCommand(verboseOption));
root.AddCommand(BuildInitCommand(verboseOption));
root.AddCommand(BuildAgentCommand(verboseOption));
root.AddCommand(BuildConfigCommand(verboseOption));
return await root.InvokeAsync(args);

JsonSerializerOptions CreateWriteJsonOptions() => new()
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

JsonSerializerOptions CreateReadJsonOptions() => new()
{
    PropertyNameCaseInsensitive = true
};

Command BuildValidateCommand(Option<bool> verboseOption)
{
    var remoteOption = new Option<bool>("--remote", "Validate using the running gateway /api/config/validate endpoint.");
    var gatewayUrlOption = new Option<string?>("--gateway-url", "Gateway base URL override for remote validation.");
    var command = new Command("validate", "Validate BotNexus platform configuration.")
    {
        remoteOption,
        gatewayUrlOption
    };

    command.SetHandler(async context =>
    {
        var remote = context.ParseResult.GetValueForOption(remoteOption);
        var verbose = context.ParseResult.GetValueForOption(verboseOption);
        var gatewayUrlOverride = context.ParseResult.GetValueForOption(gatewayUrlOption);
        context.ExitCode = remote
            ? await ValidateRemoteAsync(gatewayUrlOverride, verbose, CancellationToken.None)
            : await ValidateLocalAsync(verbose, CancellationToken.None);
    });

    return command;
}

Command BuildInitCommand(Option<bool> verboseOption)
{
    var forceOption = new Option<bool>("--force", "Overwrite existing config.json.");
    var command = new Command("init", "Initialize ~/.botnexus with a default config and required directories.")
    {
        forceOption
    };

    command.SetHandler(async context =>
    {
        var force = context.ParseResult.GetValueForOption(forceOption);
        var verbose = context.ParseResult.GetValueForOption(verboseOption);
        context.ExitCode = await InitAsync(force, verbose, CancellationToken.None);
    });

    return command;
}

Command BuildAgentCommand(Option<bool> verboseOption)
{
    var command = new Command("agent", "Manage configured agents.");

    var listCommand = new Command("list", "List configured agents.");
    listCommand.SetHandler(async context =>
    {
        var verbose = context.ParseResult.GetValueForOption(verboseOption);
        context.ExitCode = await AgentListAsync(verbose, CancellationToken.None);
    });

    var idArgument = new Argument<string>("id", "Agent ID.");
    var providerOption = new Option<string>("--provider", () => "copilot", "Agent provider name.");
    var modelOption = new Option<string>("--model", () => "gpt-4.1", "Agent model name.");
    var enabledOption = new Option<bool>("--enabled", () => true, "Whether the agent is enabled.");
    var addCommand = new Command("add", "Add an agent to config.json.")
    {
        idArgument,
        providerOption,
        modelOption,
        enabledOption
    };

    addCommand.SetHandler(async context =>
    {
        var id = context.ParseResult.GetValueForArgument(idArgument);
        var provider = context.ParseResult.GetValueForOption(providerOption);
        var model = context.ParseResult.GetValueForOption(modelOption);
        var enabled = context.ParseResult.GetValueForOption(enabledOption);
        var verbose = context.ParseResult.GetValueForOption(verboseOption);
        context.ExitCode = await AgentAddAsync(id, provider, model, enabled, verbose, CancellationToken.None);
    });

    var removeCommand = new Command("remove", "Remove an agent from config.json.")
    {
        idArgument
    };
    removeCommand.SetHandler(async context =>
    {
        var id = context.ParseResult.GetValueForArgument(idArgument);
        var verbose = context.ParseResult.GetValueForOption(verboseOption);
        context.ExitCode = await AgentRemoveAsync(id, verbose, CancellationToken.None);
    });

    command.AddCommand(listCommand);
    command.AddCommand(addCommand);
    command.AddCommand(removeCommand);
    return command;
}

Command BuildConfigCommand(Option<bool> verboseOption)
{
    var command = new Command("config", "Read and update BotNexus configuration.");

    var keyArgument = new Argument<string>("key", "Dotted config key path (example: gateway.listenUrl).");
    var getCommand = new Command("get", "Get a config value by dotted key.")
    {
        keyArgument
    };
    getCommand.SetHandler(async context =>
    {
        var key = context.ParseResult.GetValueForArgument(keyArgument);
        var verbose = context.ParseResult.GetValueForOption(verboseOption);
        context.ExitCode = await ConfigGetAsync(key, verbose, CancellationToken.None);
    });

    var valueArgument = new Argument<string>("value", "Value to set.");
    var setCommand = new Command("set", "Set a config value by dotted key.")
    {
        keyArgument,
        valueArgument
    };
    setCommand.SetHandler(async context =>
    {
        var key = context.ParseResult.GetValueForArgument(keyArgument);
        var value = context.ParseResult.GetValueForArgument(valueArgument);
        var verbose = context.ParseResult.GetValueForOption(verboseOption);
        context.ExitCode = await ConfigSetAsync(key, value, verbose, CancellationToken.None);
    });

    command.AddCommand(getCommand);
    command.AddCommand(setCommand);
    return command;
}

async Task<int> InitAsync(bool force, bool verbose, CancellationToken cancellationToken)
{
    var homePath = PlatformConfigLoader.DefaultHomePath;
    var configPath = PlatformConfigLoader.DefaultConfigPath;
    PlatformConfigLoader.EnsureConfigDirectory(homePath);

    if (File.Exists(configPath) && !force)
    {
        Console.WriteLine($"Warning: config already exists at '{configPath}'. Use --force to overwrite.");
        Console.WriteLine($"BotNexus home: {homePath}");
        return 0;
    }

    var defaultConfig = new PlatformConfig
    {
        Gateway = new GatewaySettingsConfig
        {
            ListenUrl = "http://localhost:5005",
            DefaultAgentId = "assistant"
        },
        Agents = new Dictionary<string, AgentDefinitionConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["assistant"] = new()
            {
                Provider = "copilot",
                Model = "gpt-4.1",
                Enabled = true
            }
        }
    };

    await WriteConfigAsync(defaultConfig, configPath, cancellationToken);
    Console.WriteLine($"Initialized BotNexus home at: {homePath}");
    Console.WriteLine($"Created config: {configPath}");
    Console.WriteLine("Next steps:");
    Console.WriteLine("  - botnexus validate");
    Console.WriteLine("  - botnexus agent list");

    if (verbose)
        Console.WriteLine(JsonSerializer.Serialize(defaultConfig, CreateWriteJsonOptions()));

    return 0;
}

async Task<int> AgentListAsync(bool verbose, CancellationToken cancellationToken)
{
    var config = await LoadConfigRequiredAsync(cancellationToken);
    if (config is null)
        return 1;

    if (config.Agents is null || config.Agents.Count == 0)
    {
        Console.WriteLine("Agents:");
        Console.WriteLine("  (none)");
        return 0;
    }

    Console.WriteLine("Agents:");
    foreach (var (agentId, agent) in config.Agents.OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine($"  {agentId}  provider={agent.Provider ?? "(unset)"}  model={agent.Model ?? "(unset)"}  enabled={agent.Enabled.ToString().ToLowerInvariant()}");
    }

    if (verbose)
        Console.WriteLine($"Loaded from: {PlatformConfigLoader.DefaultConfigPath}");

    return 0;
}

async Task<int> AgentAddAsync(string id, string provider, string model, bool enabled, bool verbose, CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(id))
    {
        Console.WriteLine("Error: agent ID is required.");
        return 1;
    }

    var config = await LoadConfigRequiredAsync(cancellationToken);
    if (config is null)
        return 1;

    config.Agents ??= new Dictionary<string, AgentDefinitionConfig>(StringComparer.OrdinalIgnoreCase);
    if (ContainsDictionaryKey(config.Agents, id))
    {
        Console.WriteLine($"Error: agent '{id}' already exists.");
        return 1;
    }

    config.Agents[id] = new AgentDefinitionConfig
    {
        Provider = provider,
        Model = model,
        Enabled = enabled
    };

    var saveCode = await SaveAndValidateAsync(config, verbose, cancellationToken);
    if (saveCode != 0)
        return saveCode;

    Console.WriteLine($"Added agent '{id}'.");
    return 0;
}

async Task<int> AgentRemoveAsync(string id, bool verbose, CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(id))
    {
        Console.WriteLine("Error: agent ID is required.");
        return 1;
    }

    var config = await LoadConfigRequiredAsync(cancellationToken);
    if (config is null)
        return 1;

    if (config.Agents is null || !TryFindDictionaryKey(config.Agents, id, out var matchedId))
    {
        Console.WriteLine($"Error: agent '{id}' was not found.");
        return 1;
    }

    var defaultAgent = config.GetDefaultAgentId();
    if (!string.IsNullOrWhiteSpace(defaultAgent) &&
        string.Equals(defaultAgent, matchedId, StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Warning: removing default agent '{matchedId}'. Update gateway.defaultAgentId if needed.");
    }

    config.Agents.Remove(matchedId);
    var saveCode = await SaveAndValidateAsync(config, verbose, cancellationToken);
    if (saveCode != 0)
        return saveCode;

    Console.WriteLine($"Removed agent '{matchedId}'.");
    return 0;
}

async Task<int> ConfigGetAsync(string keyPath, bool verbose, CancellationToken cancellationToken)
{
    var config = await LoadConfigRequiredAsync(cancellationToken);
    if (config is null)
        return 1;

    if (!TryGetByPath(config, keyPath, out var value, out var error))
    {
        Console.WriteLine($"Error: {error}");
        return 1;
    }

    PrintValue(value);
    if (verbose)
        Console.WriteLine($"Read key: {keyPath}");

    return 0;
}

async Task<int> ConfigSetAsync(string keyPath, string rawValue, bool verbose, CancellationToken cancellationToken)
{
    var config = await LoadConfigRequiredAsync(cancellationToken);
    if (config is null)
        return 1;

    if (!TrySetByPath(config, keyPath, rawValue, out var error))
    {
        Console.WriteLine($"Error: {error}");
        return 1;
    }

    var saveCode = await SaveAndValidateAsync(config, verbose, cancellationToken);
    if (saveCode != 0)
        return saveCode;

    Console.WriteLine($"Set {keyPath}.");
    return 0;
}

async Task<PlatformConfig?> LoadConfigRequiredAsync(CancellationToken cancellationToken)
{
    var configPath = PlatformConfigLoader.DefaultConfigPath;
    if (!File.Exists(configPath))
    {
        Console.WriteLine($"Error: config file not found at '{configPath}'. Run 'botnexus init' first.");
        return null;
    }

    try
    {
        return await PlatformConfigLoader.LoadAsync(configPath, cancellationToken, validateOnLoad: false);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: unable to load config: {ex.Message}");
        return null;
    }
}

async Task<int> SaveAndValidateAsync(PlatformConfig config, bool verbose, CancellationToken cancellationToken)
{
    var configPath = PlatformConfigLoader.DefaultConfigPath;
    await WriteConfigAsync(config, configPath, cancellationToken);

    var reloaded = await PlatformConfigLoader.LoadAsync(configPath, cancellationToken, validateOnLoad: false);
    var errors = PlatformConfigLoader.Validate(reloaded);
    if (errors.Count > 0)
    {
        Console.WriteLine("Config validation failed after write:");
        foreach (var error in errors)
            Console.WriteLine($"- {error}");
        return 1;
    }

    if (verbose)
        Console.WriteLine($"Saved config: {configPath}");

    return 0;
}

async Task WriteConfigAsync(PlatformConfig config, string configPath, CancellationToken cancellationToken)
{
    PlatformConfigLoader.EnsureConfigDirectory(PlatformConfigLoader.DefaultHomePath);
    await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config, CreateWriteJsonOptions()), cancellationToken);
}

bool TryGetByPath(object root, string keyPath, out object? value, out string error)
{
    value = null;
    error = string.Empty;
    var segments = SplitKeyPath(keyPath);
    if (segments.Length == 0)
    {
        error = "Key path is required.";
        return false;
    }

    object? current = root;
    Type currentType = root.GetType();
    foreach (var segment in segments)
    {
        if (current is null)
        {
            error = $"Path '{keyPath}' is null before '{segment}'.";
            return false;
        }

        if (TryGetDictionaryValueType(currentType, out _))
        {
            var dictionary = (IDictionary)current;
            if (!TryFindDictionaryLookupKey(dictionary, segment, out var matchedKey))
            {
                error = $"Key '{segment}' was not found.";
                return false;
            }

            current = dictionary[matchedKey];
            currentType = current?.GetType() ?? typeof(object);
            continue;
        }

        var property = FindProperty(currentType, segment);
        if (property is null)
        {
            error = $"Property '{segment}' does not exist on '{currentType.Name}'.";
            return false;
        }

        current = property.GetValue(current);
        currentType = property.PropertyType;
    }

    value = current;
    return true;
}

bool TrySetByPath(object root, string keyPath, string rawValue, out string error)
{
    error = string.Empty;
    var segments = SplitKeyPath(keyPath);
    if (segments.Length == 0)
    {
        error = "Key path is required.";
        return false;
    }

    object current = root;
    Type currentType = root.GetType();
    for (var i = 0; i < segments.Length; i++)
    {
        var segment = segments[i];
        var isLast = i == segments.Length - 1;

        if (TryGetDictionaryValueType(currentType, out var dictionaryValueType))
        {
            var dictionary = (IDictionary)current;
            if (isLast)
            {
                if (!TryConvertValue(rawValue, dictionaryValueType, out var converted, out error))
                    return false;

                var key = FindOrCreateDictionaryKey(dictionary, segment);
                dictionary[key] = converted;
                return true;
            }

            var nextKey = FindOrCreateDictionaryKey(dictionary, segment);
            var existing = dictionary[nextKey];
            if (existing is null)
            {
                if (!TryCreateInstance(dictionaryValueType, out existing, out error))
                    return false;
                dictionary[nextKey] = existing;
            }

            current = existing;
            currentType = existing.GetType();
            continue;
        }

        var property = FindProperty(currentType, segment);
        if (property is null)
        {
            error = $"Property '{segment}' does not exist on '{currentType.Name}'.";
            return false;
        }

        if (isLast)
        {
            if (!property.CanWrite)
            {
                error = $"Property '{property.Name}' is read-only.";
                return false;
            }

            if (!TryConvertValue(rawValue, property.PropertyType, out var converted, out error))
                return false;

            property.SetValue(current, converted);
            return true;
        }

        var next = property.GetValue(current);
        if (next is null)
        {
            if (!TryCreateInstance(property.PropertyType, out next, out error))
                return false;

            property.SetValue(current, next);
        }

        current = next;
        currentType = next.GetType();
    }

    error = $"Unable to set path '{keyPath}'.";
    return false;
}

bool TryConvertValue(string rawValue, Type targetType, out object? converted, out string error)
{
    converted = null;
    error = string.Empty;
    var nonNullableType = Nullable.GetUnderlyingType(targetType) ?? targetType;

    if (string.Equals(rawValue, "null", StringComparison.OrdinalIgnoreCase) && Nullable.GetUnderlyingType(targetType) is not null)
        return true;

    if (nonNullableType == typeof(string))
    {
        converted = rawValue;
        return true;
    }

    if (nonNullableType == typeof(bool))
    {
        if (bool.TryParse(rawValue, out var boolValue))
        {
            converted = boolValue;
            return true;
        }

        error = $"'{rawValue}' is not a valid boolean.";
        return false;
    }

    if (nonNullableType.IsEnum)
    {
        if (Enum.TryParse(nonNullableType, rawValue, ignoreCase: true, out var enumValue))
        {
            converted = enumValue;
            return true;
        }

        error = $"'{rawValue}' is not a valid {nonNullableType.Name} value.";
        return false;
    }

    try
    {
        if (nonNullableType == typeof(int))
            converted = int.Parse(rawValue, CultureInfo.InvariantCulture);
        else if (nonNullableType == typeof(long))
            converted = long.Parse(rawValue, CultureInfo.InvariantCulture);
        else if (nonNullableType == typeof(double))
            converted = double.Parse(rawValue, CultureInfo.InvariantCulture);
        else if (nonNullableType == typeof(float))
            converted = float.Parse(rawValue, CultureInfo.InvariantCulture);
        else if (nonNullableType == typeof(decimal))
            converted = decimal.Parse(rawValue, CultureInfo.InvariantCulture);
        else
            converted = JsonSerializer.Deserialize(rawValue, nonNullableType, CreateReadJsonOptions());
    }
    catch (Exception ex)
    {
        error = $"Unable to convert '{rawValue}' to {nonNullableType.Name}: {ex.Message}";
        return false;
    }

    return true;
}

bool TryCreateInstance(Type targetType, out object? instance, out string error)
{
    instance = null;
    error = string.Empty;
    var nonNullableType = Nullable.GetUnderlyingType(targetType) ?? targetType;

    if (TryGetDictionaryValueType(nonNullableType, out var dictionaryValueType))
    {
        var dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), dictionaryValueType);
        instance = Activator.CreateInstance(dictionaryType, StringComparer.OrdinalIgnoreCase);
        return instance is not null;
    }

    try
    {
        instance = Activator.CreateInstance(nonNullableType);
        if (instance is null)
        {
            error = $"Unable to instantiate {nonNullableType.Name}.";
            return false;
        }

        return true;
    }
    catch (Exception ex)
    {
        error = $"Unable to instantiate {nonNullableType.Name}: {ex.Message}";
        return false;
    }
}

PropertyInfo? FindProperty(Type type, string segment)
{
    return type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .FirstOrDefault(property =>
            property.Name.Equals(segment, StringComparison.OrdinalIgnoreCase) ||
            ToCamelCase(property.Name).Equals(segment, StringComparison.OrdinalIgnoreCase));
}

string ToCamelCase(string value)
{
    if (string.IsNullOrEmpty(value))
        return value;

    if (value.Length == 1)
        return value.ToLowerInvariant();

    return char.ToLowerInvariant(value[0]) + value[1..];
}

bool ContainsDictionaryKey<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key)
    where TKey : notnull
{
    if (dictionary.ContainsKey(key))
        return true;

    if (key is string stringKey)
        return dictionary.Keys.OfType<string>().Any(k => string.Equals(k, stringKey, StringComparison.OrdinalIgnoreCase));

    return false;
}

bool TryFindDictionaryKey<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key, out TKey matchedKey)
    where TKey : notnull
{
    if (dictionary.ContainsKey(key))
    {
        matchedKey = key;
        return true;
    }

    if (key is string stringKey)
    {
        foreach (var existingKey in dictionary.Keys)
        {
            if (existingKey is string existingString &&
                string.Equals(existingString, stringKey, StringComparison.OrdinalIgnoreCase))
            {
                matchedKey = existingKey;
                return true;
            }
        }
    }

    matchedKey = default!;
    return false;
}

bool TryFindDictionaryLookupKey(IDictionary dictionary, string key, out object? matchedKey)
{
    if (dictionary.Contains(key))
    {
        matchedKey = key;
        return true;
    }

    foreach (var existingKey in dictionary.Keys)
    {
        if (existingKey is string existingString &&
            string.Equals(existingString, key, StringComparison.OrdinalIgnoreCase))
        {
            matchedKey = existingKey;
            return true;
        }
    }

    matchedKey = null;
    return false;
}

object FindOrCreateDictionaryKey(IDictionary dictionary, string key)
{
    if (TryFindDictionaryLookupKey(dictionary, key, out var matchedKey) && matchedKey is not null)
        return matchedKey;

    return key;
}

bool TryGetDictionaryValueType(Type type, out Type valueType)
{
    var target = Nullable.GetUnderlyingType(type) ?? type;
    if (target.IsGenericType &&
        target.GetGenericTypeDefinition() == typeof(Dictionary<,>) &&
        target.GetGenericArguments()[0] == typeof(string))
    {
        valueType = target.GetGenericArguments()[1];
        return true;
    }

    foreach (var implemented in target.GetInterfaces())
    {
        if (!implemented.IsGenericType || implemented.GetGenericTypeDefinition() != typeof(IDictionary<,>))
            continue;

        var args = implemented.GetGenericArguments();
        if (args[0] != typeof(string))
            continue;

        valueType = args[1];
        return true;
    }

    valueType = typeof(object);
    return false;
}

string[] SplitKeyPath(string keyPath)
{
    if (string.IsNullOrWhiteSpace(keyPath))
        return [];

    return keyPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

void PrintValue(object? value)
{
    if (value is null)
    {
        Console.WriteLine("null");
        return;
    }

    if (value is string stringValue)
    {
        Console.WriteLine(stringValue);
        return;
    }

    Console.WriteLine(JsonSerializer.Serialize(value, CreateWriteJsonOptions()));
}

async Task<int> ValidateLocalAsync(bool verbose, CancellationToken cancellationToken)
{
    var configPath = PlatformConfigLoader.DefaultConfigPath;
    Console.WriteLine("BotNexus config validation (local)");
    Console.WriteLine($"Config path: {configPath}");

    if (!File.Exists(configPath))
    {
        PrintResult(
            valid: false,
            warnings: [],
            errors:
            [
                $"Config file not found at '{configPath}'.",
                "Create ~/.botnexus/config.json (or set BOTNEXUS_HOME) and retry."
            ]);
        return 1;
    }

    PlatformConfig config;
    try
    {
        config = await PlatformConfigLoader.LoadAsync(configPath, cancellationToken, validateOnLoad: false);
    }
    catch (Exception ex)
    {
        PrintResult(valid: false, warnings: [], errors: [$"Unable to load config: {ex.Message}"]);
        return 1;
    }

    var errors = PlatformConfigLoader.Validate(config);
    if (verbose)
    {
        Console.WriteLine();
        Console.WriteLine("Validation trace:");
        Console.WriteLine($"- Loaded config file: {configPath}");
        Console.WriteLine($"- Ran {nameof(PlatformConfigLoader)}.{nameof(PlatformConfigLoader.Validate)}");
        Console.WriteLine();
        Console.WriteLine("Config details:");
        Console.WriteLine(JsonSerializer.Serialize(config, CreateWriteJsonOptions()));
    }

    PrintResult(valid: errors.Count == 0, warnings: [], errors);
    return errors.Count == 0 ? 0 : 1;
}

async Task<int> ValidateRemoteAsync(string? gatewayUrlOverride, bool verbose, CancellationToken cancellationToken)
{
    var gatewayUrl = ResolveGatewayUrl(gatewayUrlOverride);
    if (!Uri.TryCreate(gatewayUrl, UriKind.Absolute, out var gatewayBaseUri))
    {
        PrintResult(valid: false, warnings: [], errors: [$"Invalid gateway URL '{gatewayUrl}'."]);
        return 1;
    }

    var endpoint = new Uri(gatewayBaseUri, "/api/config/validate");
    Console.WriteLine("BotNexus config validation (remote)");
    Console.WriteLine($"Gateway URL: {gatewayBaseUri}");
    Console.WriteLine($"Endpoint: {endpoint}");

    using var httpClient = new HttpClient();
    HttpResponseMessage response;
    try
    {
        response = await httpClient.GetAsync(endpoint, cancellationToken);
    }
    catch (Exception ex)
    {
        PrintResult(valid: false, warnings: [], errors: [$"Remote validation request failed: {ex.Message}"]);
        return 1;
    }

    var payload = await response.Content.ReadAsStringAsync(cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        PrintResult(valid: false, warnings: [], errors: [$"Gateway returned {(int)response.StatusCode} {response.ReasonPhrase}.", payload]);
        return 1;
    }

    ConfigValidationResponse? validation;
    try
    {
        validation = JsonSerializer.Deserialize<ConfigValidationResponse>(payload, CreateReadJsonOptions());
    }
    catch (Exception ex)
    {
        PrintResult(valid: false, warnings: [], errors: [$"Unable to parse gateway response: {ex.Message}", payload]);
        return 1;
    }

    if (validation is null)
    {
        PrintResult(valid: false, warnings: [], errors: ["Gateway response was empty."]);
        return 1;
    }

    if (verbose)
    {
        Console.WriteLine();
        Console.WriteLine("Validation trace:");
        Console.WriteLine($"- GET {endpoint}");
        Console.WriteLine($"- HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        Console.WriteLine($"- Validated config path: {validation.ConfigPath}");
        Console.WriteLine();
        Console.WriteLine("Response details:");
        Console.WriteLine(payload);
    }

    PrintResult(validation.IsValid, [], validation.Errors ?? []);
    return validation.IsValid ? 0 : 1;
}

string ResolveGatewayUrl(string? gatewayUrlOverride)
{
    if (!string.IsNullOrWhiteSpace(gatewayUrlOverride))
        return gatewayUrlOverride;

    try
    {
        var config = PlatformConfigLoader.Load(validateOnLoad: false);
        return config.GetListenUrl() ?? "http://localhost:5005";
    }
    catch
    {
        return "http://localhost:5005";
    }
}

void PrintResult(bool valid, IReadOnlyList<string> warnings, IReadOnlyList<string> errors)
{
    Console.WriteLine();
    Console.WriteLine(valid ? "Result: VALID ✅" : "Result: INVALID ❌");

    Console.WriteLine("Warnings:");
    if (warnings.Count == 0)
    {
        Console.WriteLine("- (none)");
    }
    else
    {
        foreach (var warning in warnings)
            Console.WriteLine($"- {warning}");
    }

    Console.WriteLine("Errors:");
    if (errors.Count == 0)
    {
        Console.WriteLine("- (none)");
    }
    else
    {
        foreach (var error in errors)
            Console.WriteLine($"- {error}");
    }
}

internal sealed record ConfigValidationResponse(bool IsValid, string ConfigPath, IReadOnlyList<string>? Errors);
