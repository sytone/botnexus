# gateway-provider-bootstrap

## Intent
Wire BotNexus Gateway so in-process agents can call LLM providers with platform-level auth resolution.

## Pattern

1. In `BotNexus.Gateway.Api/Program.cs`, register:
   - `ApiProviderRegistry`
   - `ModelRegistry`
   - `BuiltInModels`
   - shared `HttpClient` singleton (`Timeout = 10 minutes`)
2. In the `LlmClient` factory:
   - register Anthropic/OpenAI/OpenAICompat provider implementations
   - register built-in models
3. Add a gateway-scoped auth resolver (`GatewayAuthManager`) in `BotNexus.Gateway/Configuration`:
   - load `~/.botnexus/auth.json`
   - refresh Copilot OAuth entries via `CopilotOAuth.RefreshAsync`
   - fallback to `EnvironmentApiKeys`
   - fallback to `PlatformConfig.Providers[{provider}].ApiKey`
4. Inject `GatewayAuthManager` into `InProcessIsolationStrategy` and pass
   `GetApiKey: (provider, ct) => _authManager.GetApiKeyAsync(provider, ct)` when constructing `AgentOptions`.
5. Ensure project references exist in Gateway/Gateway.Api for provider assemblies used at compile time.

## Verification

- `dotnet build --nologo`
- `dotnet test tests\gateway\BotNexus.Gateway.Tests --nologo`
- smoke run: `dotnet run --project src\gateway\BotNexus.Gateway.Api` (verify startup, then stop)
