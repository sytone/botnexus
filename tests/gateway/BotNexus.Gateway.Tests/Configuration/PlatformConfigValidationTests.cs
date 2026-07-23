using System.Text.Json;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Configuration;

public sealed class PlatformConfigValidationTests
{
    [Fact]
    public Task Validate_MinimalValidConfig_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "version": 1,
              "providers": {
                "copilot": {
                  "apiKey": "test-key"
                }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                var errors = PlatformConfigLoader.Validate(config);

                errors.ShouldBeEmpty();
                config.PlatformVersion.ShouldBe(1);
                config.Agents.ShouldNotBeNull();
                var agents = config.Agents ?? throw new InvalidOperationException("Expected agents config.");
                agents.ShouldContainKey("assistant");
                config.Providers.ShouldNotBeNull();
                var providers = config.Providers ?? throw new InvalidOperationException("Expected providers config.");
                providers.ShouldContainKey("copilot");
            });

    [Fact]
    public Task Validate_FullGatewaySectionWithInMemorySessionStore_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "version": 1,
              "gateway": {
                "listenUrl": "http://localhost:5005",
                "defaultAgentId": "assistant",
                "sessionStore": {
                  "type": "InMemory"
                },
                "cors": {
                  "allowedOrigins": ["https://app.example.test"]
                },
                "rateLimit": {
                  "enabled": true,
                  "requestsPerMinute": 120,
                  "windowSeconds": 60
                },
                "extensions": {
                  "path": "extensions",
                  "defaults": {
                    "botnexus-skills": { "enabled": true },
                    "botnexus-exec": { "timeout": 30 }
                  }
                }
              },
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                var errors = PlatformConfigLoader.Validate(config);

                errors.ShouldBeEmpty();
                config.Gateway.ShouldNotBeNull();
                var gateway = config.Gateway ?? throw new InvalidOperationException("Expected gateway config.");
                gateway.SessionStore.ShouldNotBeNull();
                gateway.SessionStore.Type.ShouldBe("InMemory");
                gateway.Extensions.ShouldNotBeNull();
                var extensions = gateway.Extensions ?? throw new InvalidOperationException("Expected gateway extensions.");
                extensions.Defaults.ShouldNotBeNull();
                var defaults = extensions.Defaults ?? throw new InvalidOperationException("Expected extension defaults.");
                defaults.ShouldContainKey("botnexus-skills");
                defaults["botnexus-skills"].GetProperty("enabled").GetBoolean().ShouldBeTrue();
                SerializeShouldNotThrow(config);
            });

    [Fact]
    public Task Validate_FullGatewaySectionWithSqliteSessionStore_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "gateway": {
                "listenUrl": "https://gateway.example.test",
                "defaultAgentId": "assistant",
                "sessionStore": {
                  "type": "Sqlite",
                  "connectionString": "Data Source=sessions.db"
                },
                "cors": {
                  "allowedOrigins": ["https://app.example.test"]
                },
                "rateLimit": {
                  "enabled": true,
                  "requestsPerMinute": 100,
                  "windowSeconds": 120
                },
                "extensions": {
                  "path": "extensions",
                  "defaults": {
                    "botnexus-mcp": { "enabled": true }
                  }
                }
              },
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                var errors = PlatformConfigLoader.Validate(config);

                errors.ShouldBeEmpty();
                config.Gateway!.SessionStore!.Type.ShouldBe("Sqlite");
                config.Gateway.SessionStore.ConnectionString.ShouldBe("Data Source=sessions.db");
                SerializeShouldNotThrow(config);
            });

    [Fact]
    public Task Validate_LegacyRootListenUrl_MigratesToGatewaySection()
        => WithConfigFileAsync(
            """
            {
              "listenUrl": "http://localhost:5005",
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                var errors = PlatformConfigLoader.Validate(config);

                errors.ShouldBeEmpty();
                config.Gateway.ShouldNotBeNull();
                config.Gateway!.ListenUrl.ShouldBe("http://localhost:5005");
            });

    [Fact]
    public Task Validate_MissingGatewaySection_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                config.Gateway.ShouldBeNull();
            });

    [Fact]
    public async Task Validate_InvalidSessionStoreType_ThrowsValidationError()
    {
        await WithConfigFileAsync(
            """
            {
              "gateway": {
                "sessionStore": {
                  "type": "Redis"
                }
              },
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var ex = await Should.ThrowAsync<OptionsValidationException>(() => PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: true));
                ex.Failures.ShouldContain("gateway.sessionStore.type must be either 'InMemory', 'File', or 'Sqlite'.");
            });
    }

    [Fact]
    public async Task Validate_InvalidListenUrlFormat_ThrowsValidationError()
    {
        await WithConfigFileAsync(
            """
            {
              "gateway": {
                "listenUrl": "not-a-url"
              },
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var ex = await Should.ThrowAsync<OptionsValidationException>(() => PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: true));
                ex.Failures.ShouldContain("gateway.listenUrl must be a valid absolute URL (example: http://localhost:5005).");
            });
    }

    [Fact]
    public async Task Validate_InvalidListenUrlScheme_ThrowsValidationError()
    {
        await WithConfigFileAsync(
            """
            {
              "gateway": {
                "listenUrl": "ftp://localhost:5005"
              },
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var ex = await Should.ThrowAsync<OptionsValidationException>(() => PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: true));
                ex.Failures.ShouldContain("gateway.listenUrl must use http or https.");
            });
    }

    [Fact]
    public Task Validate_ListenUrlBindingWildcardPlus_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "gateway": {
                "listenUrl": "http://+:5000"
              },
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                // 'http://+:5000' is the canonical container listenUrl (tests/container/config.json)
                // and a valid Kestrel binding wildcard, even though Uri.TryCreate rejects it.
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                var errors = PlatformConfigLoader.Validate(config);
                errors.ShouldBeEmpty();
                config.Gateway.ShouldNotBeNull();
                config.Gateway!.ListenUrl.ShouldBe("http://+:5000");
            });

    [Fact]
    public Task Validate_ListenUrlBindingWildcardStar_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "gateway": {
                "listenUrl": "http://*:8080"
              },
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                // 'http://*:8080' is the Kestrel strong-binding wildcard form.
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                var errors = PlatformConfigLoader.Validate(config);
                errors.ShouldBeEmpty();
            });

    [Fact]
    public Task Validate_ListenUrlHttpsBindingWildcard_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "gateway": {
                "listenUrl": "https://+:5001"
              },
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                // https wildcard is accepted (scheme is validated, wildcard host is allowed).
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                var errors = PlatformConfigLoader.Validate(config);
                errors.ShouldBeEmpty();
            });

    [Fact]
    public Task Validate_ListenUrlBindingWildcardNoPort_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "gateway": {
                "listenUrl": "http://+"
              },
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                // A wildcard host without an explicit port is still a valid binding form.
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                var errors = PlatformConfigLoader.Validate(config);
                errors.ShouldBeEmpty();
            });

    [Fact]
    public async Task Validate_ListenUrlBindingWildcardInvalidScheme_ThrowsValidationError()
    {
        await WithConfigFileAsync(
            """
            {
              "gateway": {
                "listenUrl": "ftp://+:5000"
              },
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                // The wildcard host is recognized, but a non-http(s) scheme is still rejected.
                var ex = await Should.ThrowAsync<OptionsValidationException>(() => PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: true));
                ex.Failures.ShouldContain("gateway.listenUrl must use http or https.");
            });
    }

    [Fact]
    public Task Validate_SingleAgentMinimal_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                var errors = PlatformConfigLoader.Validate(config);

                errors.ShouldBeEmpty();
                config.Agents!["assistant"].Provider.ShouldBe("copilot");
                config.Agents["assistant"].Model.ShouldBe("gpt-4.1");
            });

    [Fact]
    public Task Validate_AgentWithAllOptionalFields_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "displayName": "Assistant",
                  "description": "General helper",
                  "model": "gpt-4.1",
                  "systemPromptFile": "SOUL.md",
                  "systemPromptFiles": ["SOUL.md", "IDENTITY.md"],
                  "toolIds": ["read", "write"],
                  "allowedModels": ["gpt-4.1", "gpt-4o"],
                  "subAgents": ["scribe", "hermes"],
                  "isolationStrategy": "in-process",
                  "maxConcurrentSessions": 3,
                  "memory": {
                    "enabled": true,
                    "indexing": "auto",
                    "search": {
                      "defaultTopK": 5,
                      "temporalDecay": {
                        "enabled": true,
                        "halfLifeDays": 14
                      }
                    }
                  },
                  "soul": {
                    "enabled": true,
                    "timezone": "America/Los_Angeles",
                    "dayBoundary": "05:00",
                    "reflectionOnSeal": true,
                    "reflectionPrompt": "Reflect before sealing."
                  },
                  "heartbeat": {
                    "enabled": true,
                    "intervalMinutes": 15,
                    "prompt": "Check heartbeat tasks.",
                    "quietHours": {
                      "enabled": true,
                      "start": "23:00",
                      "end": "07:00",
                      "timezone": "America/Los_Angeles"
                    }
                  },
                  "sessionAccess": {
                    "level": "allowlist",
                    "allowedAgents": ["scribe"]
                  },
                  "fileAccess": {
                    "allowedReadPaths": ["./docs/**"],
                    "allowedWritePaths": ["./out/**"],
                    "deniedPaths": ["./secrets/**"]
                  },
                  "extensions": {
                    "botnexus-mcp": { "enabled": true, "servers": ["filesystem"] }
                  },
                  "metadata": {
                    "owner": "test-user",
                    "priority": 1
                  }
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                var errors = PlatformConfigLoader.Validate(config);
                config.Agents.ShouldNotBeNull();
                var agents = config.Agents ?? throw new InvalidOperationException("Expected agents config.");
                var agent = agents["assistant"];

                errors.ShouldBeEmpty();
                agent.DisplayName.ShouldBe("Assistant");
                agent.SystemPromptFiles.ShouldBe(["SOUL.md", "IDENTITY.md"]);
                agent.ToolIds.ShouldBe(["read", "write"]);
                agent.AllowedModels.ShouldBe(["gpt-4.1", "gpt-4o"]);
                agent.SubAgents.ShouldBe(["scribe", "hermes"]);
                agent.Memory.ShouldNotBeNull();
                agent.Soul.ShouldNotBeNull();
                agent.Heartbeat.ShouldNotBeNull();
                agent.SessionAccess.ShouldNotBeNull();
                agent.FileAccess.ShouldNotBeNull();
                agent.Extensions.ShouldNotBeNull();
                var agentExtensions = agent.Extensions ?? throw new InvalidOperationException("Expected agent extensions.");
                agentExtensions.ShouldContainKey("botnexus-mcp");
                agentExtensions["botnexus-mcp"].GetProperty("enabled").GetBoolean().ShouldBeTrue();
                agent.Metadata.ShouldNotBeNull();
                var metadata = agent.Metadata ?? throw new InvalidOperationException("Expected metadata.");
                metadata.GetProperty("owner").GetString().ShouldBe("test-user");
                SerializeShouldNotThrow(config);
            });

    [Fact]
    public Task Validate_AgentsDefaultsKey_IsStrippedAndStoredAsAgentDefaults()
        => WithConfigFileAsync(
            """
            {
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "defaults": {
                  "toolIds": ["read", "write"],
                  "memory": {
                    "enabled": true,
                    "indexing": "auto"
                  },
                  "heartbeat": {
                    "enabled": true,
                    "intervalMinutes": 10
                  },
                  "fileAccess": {
                    "allowedReadPaths": ["./docs/**"]
                  }
                },
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                var errors = PlatformConfigLoader.Validate(config);

                errors.ShouldBeEmpty();
                config.AgentDefaults.ShouldNotBeNull();
                var agentDefaults = config.AgentDefaults ?? throw new InvalidOperationException("Expected agent defaults.");
                agentDefaults.ToolIds.ShouldBe(["read", "write"]);
                config.Agents.ShouldNotBeNull();
                var agents = config.Agents ?? throw new InvalidOperationException("Expected agents config.");
                agents.ShouldContainKey("assistant");
                agents.ShouldNotContainKey("defaults");
                config.AgentRawElements.ShouldNotBeNull();
                var rawElements = config.AgentRawElements ?? throw new InvalidOperationException("Expected agent raw elements.");
                rawElements.ShouldContainKey("assistant");
            });

    [Fact]
    public Task Validate_MultipleAgents_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                },
                "scribe": {
                  "provider": "copilot",
                  "model": "gpt-4o-mini"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                config.Agents!.Count.ShouldBe(2);
            });

    [Fact]
    public Task Validate_AgentEnabledFalse_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1",
                  "enabled": false
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                config.Agents!["assistant"].Enabled.ShouldBeFalse();
            });

    [Fact]
    public async Task Validate_AgentMissingProvider_ThrowsValidationError()
    {
        await WithConfigFileAsync(
            """
            {
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var ex = await Should.ThrowAsync<OptionsValidationException>(() => PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: true));
                ex.Failures.ShouldContain("agents.assistant.provider is required (example: 'copilot').");
            });
    }

    [Fact]
    public async Task Validate_AgentMissingModel_ThrowsValidationError()
    {
        await WithConfigFileAsync(
            """
            {
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot"
                }
              }
            }
            """,
            async configPath =>
            {
                var ex = await Should.ThrowAsync<OptionsValidationException>(() => PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: true));
                ex.Failures.ShouldContain("agents.assistant.model is required (example: 'gpt-4.1').");
            });
    }

    [Fact]
    public Task Validate_AgentWithExtensionsAndMetadataNestedObjects_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1",
                  "extensions": {
                    "botnexus-exec": {
                      "enabled": true,
                      "limits": {
                        "timeout": 30
                      }
                    }
                  },
                  "metadata": {
                    "team": {
                      "name": "platform"
                    }
                  }
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                config.Agents.ShouldNotBeNull();
                var agents = config.Agents ?? throw new InvalidOperationException("Expected agents config.");
                var agent = agents["assistant"];

                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                agent.Extensions.ShouldNotBeNull();
                var extensions = agent.Extensions ?? throw new InvalidOperationException("Expected agent extensions.");
                extensions.ShouldContainKey("botnexus-exec");
                extensions["botnexus-exec"].GetProperty("limits").GetProperty("timeout").GetInt32().ShouldBe(30);
                agent.Metadata.ShouldNotBeNull();
                var metadata = agent.Metadata ?? throw new InvalidOperationException("Expected metadata.");
                metadata.GetProperty("team").GetProperty("name").GetString().ShouldBe("platform");
                SerializeShouldNotThrow(config);
            });

    [Fact]
    public Task Validate_CopilotProviderWithApiKey_NoErrors()
        => WithProviderConfigAsync(
            "copilot",
            """{ "apiKey": "copilot-key" }""",
            config =>
            {
                config.Providers!["copilot"].ApiKey.ShouldBe("copilot-key");
            });

    [Fact]
    public Task Validate_AnthropicProvider_NoErrors()
        => WithProviderConfigAsync(
            "anthropic",
            """{ "apiKey": "anthropic-key", "defaultModel": "claude-sonnet-4-5" }""",
            config =>
            {
                config.Providers!["anthropic"].DefaultModel.ShouldBe("claude-sonnet-4-5");
            });

    [Fact]
    public Task Validate_OpenAiProvider_NoErrors()
        => WithProviderConfigAsync(
            "openai",
            """{ "apiKey": "openai-key", "defaultModel": "gpt-4.1" }""",
            config =>
            {
                config.Providers!["openai"].DefaultModel.ShouldBe("gpt-4.1");
            });

    [Fact]
    public Task Validate_OpenAiCompatProviderWithBaseUrl_NoErrors()
        => WithProviderConfigAsync(
            "openai-compat",
            """{ "apiKey": "compat-key", "baseUrl": "https://llm.example.test/v1" }""",
            config =>
            {
                config.Providers!["openai-compat"].BaseUrl.ShouldBe("https://llm.example.test/v1");
            });

    [Fact]
    public Task Validate_ProviderEnabledFalse_NoErrors()
        => WithProviderConfigAsync(
            "copilot",
            """{ "enabled": false, "baseUrl": "not-a-url" }""",
            config =>
            {
                config.Providers!["copilot"].Enabled.ShouldBeFalse();
            });

    [Fact]
    public Task Validate_ProviderMissingApiKey_NoErrors()
        => WithProviderConfigAsync(
            "copilot",
            """{ "defaultModel": "gpt-4.1" }""",
            config =>
            {
                config.Providers!["copilot"].ApiKey.ShouldBeNull();
            });

    [Fact]
    public Task Validate_MultipleProviders_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "providers": {
                "copilot": { "apiKey": "copilot-key" },
                "anthropic": { "apiKey": "anthropic-key" },
                "openai": { "apiKey": "openai-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                config.Providers!.Count.ShouldBe(3);
            });

    [Fact]
    public async Task Validate_ProviderWithInvalidBaseUrl_ThrowsValidationError()
    {
        await WithConfigFileAsync(
            """
            {
              "providers": {
                "openai-compat": {
                  "apiKey": "compat-key",
                  "baseUrl": "notaurl"
                }
              },
              "agents": {
                "assistant": {
                  "provider": "openai-compat",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var ex = await Should.ThrowAsync<OptionsValidationException>(() => PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: true));
                ex.Failures.ShouldContain("providers.openai-compat.baseUrl must be a valid http or https absolute URL.");
            });
    }

    [Fact]
    public Task Validate_IntegrationMockProviderWithFilesystemBaseUrl_NoErrors()
        => WithProviderConfigAsync(
            "integration-mock",
            """{ "api": "integration-mock", "baseUrl": "C:\\tmp\\catalog.json" }""",
            config =>
            {
                // The integration-mock provider repurposes baseUrl as a path to a JSON
                // catalog file (see IntegrationMockProvider). The http(s) URL check
                // must not fire for this case — both the CLI help and the README say
                // a filesystem path is the expected value here.
                config.Providers!["integration-mock"].BaseUrl.ShouldBe("C:\\tmp\\catalog.json");
                config.Providers!["integration-mock"].Api.ShouldBe("integration-mock");
            });

    [Fact]
    public Task Validate_TelegramFlatChannelConfig_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "channels": {
                "telegram": {
                  "botToken": "123:abc",
                  "agentId": "assistant",
                  "allowedChatIds": [12345, 67890]
                }
              },
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                config.Channels.ShouldNotBeNull();
                var channels = config.Channels ?? throw new InvalidOperationException("Expected channels.");
                channels.ShouldContainKey("telegram");
            });

    [Fact]
    public Task Validate_TelegramMultiBotChannelConfig_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "channels": {
                "telegram": {
                  "bots": {
                    "alpha": {
                      "botToken": "111:aaa",
                      "agentId": "assistant"
                    },
                    "beta": {
                      "botToken": "222:bbb",
                      "agentId": "scribe",
                      "allowedChatIds": [98765]
                    }
                  }
                }
              },
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                },
                "scribe": {
                  "provider": "copilot",
                  "model": "gpt-4o-mini"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                config.Channels.ShouldNotBeNull();
                var channels = config.Channels ?? throw new InvalidOperationException("Expected channels.");
                channels.ShouldContainKey("telegram");
            });

    [Fact]
    public Task Validate_TelegramChannelWithWebhookUrl_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "channels": {
                "telegram": {
                  "botToken": "123:abc",
                  "agentId": "assistant",
                  "webhookUrl": "https://gateway.example.test/telegram/webhook"
                }
              },
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                config.Channels.ShouldNotBeNull();
                var channels = config.Channels ?? throw new InvalidOperationException("Expected channels.");
                channels.ShouldContainKey("telegram");
            });

    [Fact]
    public Task Validate_ChannelWithoutTypeField_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "channels": {
                "telegram": {
                  "enabled": true,
                  "botToken": "123:abc"
                }
              },
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
            });

    [Fact]
    public Task Validate_NoChannelsSection_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                config.Channels.ShouldBeNull();
            });

    [Fact]
    public Task Validate_GatewayExtensionsDefaultsWithMultipleExtensions_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "gateway": {
                "extensions": {
                  "defaults": {
                    "botnexus-skills": {
                      "enabled": true,
                      "paths": ["skills"]
                    },
                    "botnexus-exec": {
                      "enabled": true,
                      "timeout": 30
                    }
                  }
                }
              },
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);

                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                config.Gateway.ShouldNotBeNull();
                var gateway = config.Gateway ?? throw new InvalidOperationException("Expected gateway config.");
                gateway.Extensions.ShouldNotBeNull();
                var extensions = gateway.Extensions ?? throw new InvalidOperationException("Expected gateway extensions.");
                extensions.Defaults.ShouldNotBeNull();
                var defaults = extensions.Defaults ?? throw new InvalidOperationException("Expected extension defaults.");
                defaults.ShouldContainKey("botnexus-skills");
                defaults.ShouldContainKey("botnexus-exec");
                defaults["botnexus-exec"].GetProperty("timeout").GetInt32().ShouldBe(30);
                SerializeShouldNotThrow(config);
            });

    [Fact]
    public Task Validate_GatewayExtensionsDefaultsEmpty_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "gateway": {
                "extensions": {
                  "defaults": {}
                }
              },
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                config.Gateway!.Extensions!.Defaults.ShouldNotBeNull();
                config.Gateway.Extensions.Defaults.ShouldBeEmpty();
            });

    [Fact]
    public Task Validate_NoExtensionsSection_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "gateway": {
                "listenUrl": "http://localhost:5005"
              },
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                config.Gateway!.Extensions.ShouldBeNull();
            });

    [Fact]
    public Task Validate_CronEnabledWithJobs_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "cron": {
                "enabled": true,
                "tickIntervalSeconds": 30,
                "jobs": {
                  "daily-summary": {
                    "name": "Daily summary",
                    "schedule": "0 9 * * *",
                    "actionType": "agent-prompt",
                    "agentId": "assistant",
                    "message": "Summarize the day"
                  }
                }
              },
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                config.Cron.ShouldNotBeNull();
                var cron = config.Cron ?? throw new InvalidOperationException("Expected cron config.");
                cron.Jobs.ShouldNotBeNull();
                var jobs = cron.Jobs ?? throw new InvalidOperationException("Expected cron jobs.");
                jobs.ShouldContainKey("daily-summary");
            });

    [Fact]
    public Task Validate_CronDisabled_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "cron": {
                "enabled": false,
                "tickIntervalSeconds": 60
              },
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                config.Cron!.Enabled.ShouldBeFalse();
            });

    [Fact]
    public Task Validate_CronJobWithModelAndAgentChat_BindsFields()
        => WithConfigFileAsync(
            """
            {
              "cron": {
                "jobs": {
                  "daily-summary": {
                    "schedule": "0 9 * * *",
                    "actionType": "agent-chat",
                    "agentId": "assistant",
                    "message": "Summarize the day",
                    "model": "openai/gpt-4.1"
                  }
                }
              },
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                config.Cron.ShouldNotBeNull();
                var cron = config.Cron ?? throw new InvalidOperationException("Expected cron config.");
                cron.Jobs.ShouldNotBeNull();
                var jobs = cron.Jobs ?? throw new InvalidOperationException("Expected cron jobs.");
                jobs.ShouldContainKey("daily-summary");
                var job = jobs["daily-summary"];
                job.ActionType.ShouldBe("agent-chat");
                job.Model.ShouldBe("openai/gpt-4.1");
            });

    [Fact]
    public Task Validate_NoCronSection_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                config.Cron.ShouldBeNull();
            });

    [Fact]
    public async Task Validate_CronJobMissingSchedule_ThrowsValidationError()
    {
        await WithConfigFileAsync(
            """
            {
              "cron": {
                "jobs": {
                  "daily-summary": {
                    "actionType": "agent-prompt"
                  }
                }
              }
            }
            """,
            async configPath =>
            {
                var ex = await Should.ThrowAsync<OptionsValidationException>(() => PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: true));
                ex.Failures.ShouldContain("cron.jobs.daily-summary.schedule is required.");
            });
    }

    [Fact]
    public Task Validate_InMemorySessionStore_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "gateway": {
                "sessionStore": {
                  "type": "InMemory"
                }
              },
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                config.Gateway!.SessionStore!.Type.ShouldBe("InMemory");
            });

    [Fact]
    public Task Validate_SqliteSessionStoreWithConnectionString_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "gateway": {
                "sessionStore": {
                  "type": "Sqlite",
                  "connectionString": "Data Source=sessions.db"
                }
              },
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
            });

    [Fact]
    public Task Validate_NoSessionStore_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "gateway": {
                "listenUrl": "http://localhost:5005"
              },
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                config.Gateway!.SessionStore.ShouldBeNull();
            });

    [Fact]
    public async Task Validate_SqliteSessionStoreMissingConnectionString_DefaultsWithoutError()
    {
        await WithConfigFileAsync(
            """
            {
              "gateway": {
                "sessionStore": {
                  "type": "Sqlite"
                }
              },
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: true);
                config.Gateway!.SessionStore!.Type.ShouldBe("Sqlite");
            });
    }

    [Fact]
    public async Task Validate_FileSessionStoreMissingFilePath_ThrowsValidationError()
    {
        await WithConfigFileAsync(
            """
            {
              "gateway": {
                "sessionStore": {
                  "type": "File"
                }
              },
              "providers": {
                "copilot": { "apiKey": "test-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var ex = await Should.ThrowAsync<OptionsValidationException>(() => PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: true));
                ex.Failures.ShouldContain("gateway.sessionStore.filePath is required when gateway.sessionStore.type is 'File'.");
            });
    }

    [Fact]
    public Task Validate_RootApiKeySet_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "apiKey": "root-key",
              "providers": {
                "copilot": { "apiKey": "provider-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                config.ApiKey.ShouldBe("root-key");
            });

    [Fact]
    public Task Validate_NoApiKey_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "providers": {
                "copilot": { "apiKey": "provider-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                config.ApiKey.ShouldBeNull();
            });

    [Fact]
    public Task Validate_PerKeyApiKeysWithPermissionsAndAllowedAgents_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "gateway": {
                "apiKeys": {
                  "tenant-a": {
                    "apiKey": "secret-a",
                    "tenantId": "tenant-a",
                    "callerId": "svc-a",
                    "displayName": "Tenant A",
                    "allowedAgents": ["assistant"],
                    "permissions": ["chat:send", "sessions:read"],
                    "isAdmin": false
                  }
                }
              },
              "providers": {
                "copilot": { "apiKey": "provider-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                config.Gateway.ShouldNotBeNull();
                var gateway = config.Gateway ?? throw new InvalidOperationException("Expected gateway config.");
                gateway.ApiKeys.ShouldNotBeNull();
                var apiKeys = gateway.ApiKeys ?? throw new InvalidOperationException("Expected API keys.");
                apiKeys.ShouldContainKey("tenant-a");
                apiKeys["tenant-a"].AllowedAgents.ShouldBe(["assistant"]);
            });

    [Fact]
    public async Task Validate_ApiKeyEntryMissingRequiredFields_ThrowsValidationError()
    {
        await WithConfigFileAsync(
            """
            {
              "gateway": {
                "apiKeys": {
                  "tenant-a": {
                    "displayName": "Tenant A"
                  }
                }
              },
              "providers": {
                "copilot": { "apiKey": "provider-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var ex = await Should.ThrowAsync<OptionsValidationException>(() => PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: true));
                ex.Failures.ShouldContain("gateway.apiKeys.tenant-a.apiKey is required.");
                ex.Failures.ShouldContain("gateway.apiKeys.tenant-a.tenantId is required.");
                ex.Failures.ShouldContain("gateway.apiKeys.tenant-a.permissions must contain at least one permission (example: ['chat:send']).");
            });
    }

    [Fact]
    public Task Validate_LocationsSectionWithFilesystemType_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "gateway": {
                "locations": {
                  "workspace": {
                    "type": "filesystem",
                    "path": "."
                  }
                }
              },
              "providers": {
                "copilot": { "apiKey": "provider-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                config.Gateway!.Locations!.ShouldContainKey("workspace");
            });

    [Fact]
    public Task Validate_LocationsSectionWithDatabaseType_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "gateway": {
                "locations": {
                  "memory-db": {
                    "type": "database",
                    "connectionString": "Data Source=:memory:"
                  }
                }
              },
              "providers": {
                "copilot": { "apiKey": "provider-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
            });

    [Fact]
    public Task Validate_NoLocations_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "providers": {
                "copilot": { "apiKey": "provider-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                config.Gateway?.Locations.ShouldBeNull();
            });

    [Fact]
    public async Task Validate_LocationWithUnsupportedGitType_ThrowsValidationError()
    {
        await WithConfigFileAsync(
            """
            {
              "gateway": {
                "locations": {
                  "repo": {
                    "type": "git",
                    "path": "."
                  }
                }
              },
              "providers": {
                "copilot": { "apiKey": "provider-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var ex = await Should.ThrowAsync<OptionsValidationException>(() => PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: true));
                ex.Failures.ShouldContain("gateway.locations.repo.type must be one of: filesystem, api, mcp-server, database, remote-node.");
            });
    }

    [Fact]
    public Task Validate_CrossWorldWithPeers_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "gateway": {
                "crossWorld": {
                  "peers": {
                    "world-b": {
                      "worldId": "world-b",
                      "endpoint": "https://world-b.example.test",
                      "apiKey": "peer-key",
                      "enabled": true
                    }
                  },
                  "inbound": {
                    "enabled": true,
                    "allowedWorlds": ["world-b"],
                    "apiKeys": {
                      "world-b": "peer-key"
                    }
                  },
                  "agents": {
                    "remote-assistant": {
                      "worldId": "world-b",
                      "agentId": "assistant",
                      "description": "Remote assistant"
                    }
                  }
                }
              },
              "providers": {
                "copilot": { "apiKey": "provider-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                config.Gateway.ShouldNotBeNull();
                var gateway = config.Gateway ?? throw new InvalidOperationException("Expected gateway config.");
                gateway.CrossWorld.ShouldNotBeNull();
                var crossWorld = gateway.CrossWorld ?? throw new InvalidOperationException("Expected cross-world settings.");
                crossWorld.Peers.ShouldNotBeNull();
                var peers = crossWorld.Peers ?? throw new InvalidOperationException("Expected cross-world peers.");
                peers.ShouldContainKey("world-b");
            });

    [Fact]
    public Task Validate_NoCrossWorld_NoErrors()
        => WithConfigFileAsync(
            """
            {
              "providers": {
                "copilot": { "apiKey": "provider-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                config.Gateway?.CrossWorld.ShouldBeNull();
            });

    [Fact]
    public async Task Validate_CrossWorldInboundMissingAllowedWorlds_ThrowsValidationError()
    {
        await WithConfigFileAsync(
            """
            {
              "gateway": {
                "crossWorld": {
                  "inbound": {
                    "enabled": true,
                    "apiKeys": {
                      "world-b": "peer-key"
                    }
                  }
                }
              },
              "providers": {
                "copilot": { "apiKey": "provider-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var ex = await Should.ThrowAsync<OptionsValidationException>(() => PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: true));
                ex.Failures.ShouldContain("gateway.crossWorld.inbound.allowedWorlds must contain at least one world when inbound is enabled.");
            });
    }

    [Fact]
    public async Task Validate_CrossWorldPeerWithInvalidEndpoint_ThrowsValidationError()
    {
        await WithConfigFileAsync(
            """
            {
              "gateway": {
                "crossWorld": {
                  "peers": {
                    "world-b": {
                      "endpoint": "notaurl"
                    }
                  },
                  "inbound": {
                    "enabled": false
                  }
                }
              },
              "providers": {
                "copilot": { "apiKey": "provider-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var ex = await Should.ThrowAsync<OptionsValidationException>(() => PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: true));
                ex.Failures.ShouldContain("gateway.crossWorld.peers.world-b.endpoint must be a valid http or https absolute URL.");
            });
    }

    [Fact]
    public Task Validate_ConfigWithJsonElementFields_SerializesWithoutThrowing()
        => WithConfigFileAsync(
            """
            {
              "gateway": {
                "extensions": {
                  "defaults": {
                    "botnexus-skills": {
                      "enabled": true,
                      "paths": ["skills"]
                    }
                  }
                }
              },
              "providers": {
                "copilot": { "apiKey": "provider-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1",
                  "metadata": {
                    "owner": "test-user",
                    "tags": ["core", "test"]
                  },
                  "isolationOptions": {
                    "timeout": 30,
                    "mode": "strict"
                  },
                  "extensions": {
                    "botnexus-exec": {
                      "enabled": true,
                      "shell": "bash"
                    }
                  }
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                var agent = config.Agents!["assistant"];

                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                config.Gateway!.Extensions!.Defaults!["botnexus-skills"].GetProperty("paths")[0].GetString().ShouldBe("skills");
                agent.Metadata!.Value.GetProperty("owner").GetString().ShouldBe("test-user");
                agent.IsolationOptions!.Value.GetProperty("timeout").GetInt32().ShouldBe(30);
                agent.Extensions!["botnexus-exec"].GetProperty("shell").GetString().ShouldBe("bash");
                SerializeShouldNotThrow(config);
            });

    [Fact]
    public async Task Validate_CorsOriginWithInvalidUrl_ThrowsValidationError()
    {
        await WithConfigFileAsync(
            """
            {
              "gateway": {
                "cors": {
                  "allowedOrigins": ["notaurl"]
                }
              },
              "providers": {
                "copilot": { "apiKey": "provider-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var ex = await Should.ThrowAsync<OptionsValidationException>(() => PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: true));
                ex.Failures.ShouldContain("gateway.cors.allowedOrigins[0] must be a valid http or https absolute URL.");
            });
    }

    [Fact]
    public async Task Validate_AgentDefaultsWithBlankToolId_ThrowsValidationError()
    {
        await WithConfigFileAsync(
            """
            {
              "providers": {
                "copilot": { "apiKey": "provider-key" }
              },
              "agents": {
                "defaults": {
                  "toolIds": [""]
                },
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var ex = await Should.ThrowAsync<OptionsValidationException>(() => PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: true));
                ex.Failures.ShouldContain("agents.defaults.toolIds[0] must be a non-empty string.");
            });
    }

    [Theory]
    [InlineData("full")]
    [InlineData("summary")]
    [InlineData("none")]
    public async Task Validate_MemoryPromptInjection_AllowsSupportedValues(string promptInjection)
    {
        await WithConfigFileAsync(
            $$"""
            {
              "providers": {
                "copilot": { "apiKey": "provider-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1",
                  "memory": {
                    "enabled": true,
                    "promptInjection": "{{promptInjection}}"
                  }
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                var memory = config.Agents!["assistant"].Memory.ShouldNotBeNull();
                GetPromptInjection(memory).ShouldBe(promptInjection);
            });
    }

    [Fact]
    public async Task Validate_MemoryPromptInjection_InvalidValue_ThrowsValidationError()
    {
        await WithConfigFileAsync(
            """
            {
              "providers": {
                "copilot": { "apiKey": "provider-key" }
              },
              "agents": {
                "assistant": {
                  "provider": "copilot",
                  "model": "gpt-4.1",
                  "memory": {
                    "enabled": true,
                    "promptInjection": "verbose"
                  }
                }
              }
            }
            """,
            async configPath =>
            {
                var ex = await Should.ThrowAsync<OptionsValidationException>(() => PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: true));
                ex.Failures.ShouldContain("agents.assistant.memory.promptInjection must be one of: full, summary, none.");
            });
    }

    private static Task WithProviderConfigAsync(string providerKey, string providerJson, Action<PlatformConfig> assertConfig)
        => WithConfigFileAsync(
            $$"""
            {
              "providers": {
                "{{providerKey}}": {{providerJson}}
              },
              "agents": {
                "assistant": {
                  "provider": "{{providerKey}}",
                  "model": "gpt-4.1"
                }
              }
            }
            """,
            async configPath =>
            {
                var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
                PlatformConfigLoader.Validate(config).ShouldBeEmpty();
                assertConfig(config);
            });

    private static async Task WithConfigFileAsync(string json, Func<string, Task> test)
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "botnexus-platform-config-validation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var configPath = Path.Combine(rootPath, "config.json");

        try
        {
            await File.WriteAllTextAsync(configPath, json);
            await test(configPath);
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    private static void SerializeShouldNotThrow(PlatformConfig config)
    {
        var exception = Record.Exception(() => JsonSerializer.Serialize(config));
        exception.ShouldBeNull();
    }

    private static string GetPromptInjection(MemoryAgentConfig config)
    {
        var property = typeof(MemoryAgentConfig).GetProperty("PromptInjection");
        property.ShouldNotBeNull("MemoryAgentConfig.PromptInjection should exist for memory prompt-injection validation.");
        return property!.GetValue(config)?.ToString() ?? string.Empty;
    }

    [Fact]
    public void ValidateJson_SubAgentParentOverrides_AcceptsTrustedBudgetPolicy()
    {
        const string json = """
            {
              "gateway": {
                "subAgents": {
                  "defaultTimeoutSeconds": 1800,
                  "maxTimeoutSeconds": 1800,
                  "parentOverrides": {
                    "farnsworth": {
                      "defaultTimeoutSeconds": 3600,
                      "maxTimeoutSeconds": 3600,
                      "defaultMaxTurns": 60,
                      "maxTurnsCeiling": 90,
                      "maxConcurrentPerSession": 8
                    }
                  }
                }
              }
            }
            """;

        PlatformConfigSchema.ValidateJson(json).ShouldBeEmpty();
    }

}
