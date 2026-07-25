namespace BotNexus.Integration.ConfigDiskE2E.Tests;

/// <summary>
/// A maximal, realistic <c>config.json</c> seed. Every mutation test starts from this exact
/// document so a before/after comparison can assert that <em>only</em> the intended semantic
/// delta occurred - the collateral-damage class of bug (#1954 / #1955) that a narrow, hand-built
/// two-key fixture cannot expose.
/// </summary>
/// <remarks>
/// It intentionally includes surfaces the typed <c>PlatformConfig</c> model does not fully bind:
/// a <c>$schema</c> pointer, per-channel extension subtrees, arbitrary agent <c>extensions</c>
/// bags, and an entirely unknown top-level <c>customVendorBlock</c>. Those exist to prove that a
/// production write preserves JSON it cannot model, rather than silently dropping it on the way
/// through a typed round trip.
/// </remarks>
internal static class MaximalConfig
{
    /// <summary>The seed document written to disk before each mutation scenario.</summary>
    internal const string Json = """
        {
          "$schema": "https://botnexus.dev/schema/config.json",
          "version": 1,
          "apiKey": "sk-root-REAL-secret",
          "customVendorBlock": {
            "unknownArray": [1, 2, 3],
            "nested": { "deep": { "value": "preserve-me" } }
          },
          "gateway": {
            "listenUrl": "http://localhost:5005",
            "defaultAgentId": "assistant",
            "logLevel": "Information",
            "apiKeys": {
              "primary": {
                "apiKey": "gw-primary-REAL-secret",
                "tenantId": "tenant-a",
                "permissions": ["chat:send"]
              },
              "secondary": {
                "apiKey": "gw-secondary-REAL-secret",
                "tenantId": "tenant-b",
                "permissions": ["chat:send", "admin"]
              }
            },
            "sessionStore": {
              "type": "Sqlite",
              "connectionString": "Data Source=REAL-sessions.db"
            },
            "cors": { "allowedOrigins": ["http://localhost:5173"] },
            "extensions": {
              "defaults": {
                "botnexus-skills": { "enabled": true, "root": "skills" }
              }
            }
          },
          "providers": {
            "github-copilot": {
              "enabled": true,
              "apiKey": "sk-copilot-REAL-secret",
              "defaultModel": "claude-sonnet-4",
              "models": ["claude-sonnet-4", "gpt-4.1"]
            },
            "anthropic": {
              "enabled": false,
              "apiKey": "sk-anthropic-REAL-secret",
              "baseUrl": "https://api.anthropic.com"
            }
          },
          "channels": {
            "telegram": {
              "enabled": true,
              "bots": {
                "main": { "token": "123456:REAL-telegram-token", "name": "MainBot" },
                "ops": { "token": "654321:REAL-ops-token", "name": "OpsBot" }
              }
            },
            "serviceBus": {
              "enabled": true,
              "namespace": "contoso.servicebus.windows.net",
              "queues": {
                "inbound": { "name": "inbound-q", "maxConcurrent": 4 },
                "outbound": { "name": "outbound-q", "maxConcurrent": 2 }
              }
            }
          },
          "agents": {
            "defaults": {
              "toolIds": ["read", "write"],
              "memory": { "enabled": true, "promptInjection": "summary" },
              "heartbeat": { "enabled": false, "intervalMinutes": 30 }
            },
            "assistant": {
              "provider": "github-copilot",
              "model": "claude-sonnet-4",
              "displayName": "Assistant",
              "enabled": true,
              "toolIds": ["read", "write", "exec"],
              "metadata": { "role": "general", "builtin": false },
              "extensions": {
                "botnexus-skills": { "allow": ["github", "worktree"] }
              }
            },
            "builder": {
              "provider": "github-copilot",
              "model": "gpt-4.1",
              "displayName": "builder",
              "enabled": true,
              "subAgentRoles": ["reviewer"]
            }
          },
          "cron": {
            "enabled": true,
            "tickIntervalSeconds": 60,
            "jobs": {
              "nightly": {
                "name": "Nightly summary",
                "schedule": "0 3 * * *",
                "actionType": "agent-prompt",
                "agentId": "assistant",
                "message": "summarise",
                "enabled": true
              }
            }
          }
        }
        """;
}
