# Build and use the Windows satellite client

This developer guide explains how to build the first Windows tray companion. A satellite is a separate process that reports its live connection to a BotNexus gateway. This client does not display notifications or Canvas content and cannot run remote commands.

## Prerequisites

- Windows with an interactive desktop and the repository's .NET SDK.
- A gateway with the SignalR extension and an enabled satellite registration. Use the existing `botnexus satellite register` command; see the [CLI reference](../cli-reference.md) for its options.
- The satellite API key from registration, not a gateway administrator key.
- For a private development tunnel, a client access token for that tunnel. Browser sign-in alone does not authorize the separate .NET process.

There is no installed CLI command that starts this tray application. This is a source-built developer client, not a prebuilt installer.

## Build and start

From the repository root, run:

```powershell
dotnet build tools/BotNexus.Satellite.Client.Windows/BotNexus.Satellite.Client.Windows.csproj
dotnet run --project tools/BotNexus.Satellite.Client.Windows/BotNexus.Satellite.Client.Windows.csproj
```

The Windows project opts into Windows targeting so Linux CI can compile it. Linux cannot run its tray UI. The portable core and transport tests run in the normal remote CORE gate. The client projects are included in the root build graph, but not in the gateway deployment graph.

On first start, **Settings** opens. Enter the gateway origin, such as `https://gateway.example`, and the local satellite identifier. The identifier selects a local credential profile; it is not sent to the gateway as an authorization claim. The API key determines the server-side satellite identity.

Enter the API key in its masked field. Choose **Save**, then **Connect** from the tray menu. **Connect when the client starts** controls subsequent startup. An HTTPS origin is required, except when **Allow HTTP for exact loopback hosts only** is enabled for local development. User information, query strings, fragments, and non-root paths are rejected.

## Credentials and preferences

API keys and tunnel access tokens are stored as generic credentials in the current user's Windows Credential Manager. Their target names are scoped to the normalized gateway origin and local satellite identifier. There is no plaintext fallback. Blank secret fields preserve existing credentials for the selected profile. Changing the endpoint or identifier selects a separate profile. **Delete credentials for the selected profile** removes that profile's saved secrets.

Non-secret preferences are saved separately under your Windows local application-data directory, in `BotNexus.Satellite.Client/settings.json`. They are not gateway configuration and do not read or modify `BOTNEXUS_HOME`.

**Heartbeat seconds** defaults to 30. **Gateway stale seconds** defaults to 120 and must match the gateway registration's configured timeout. The heartbeat interval must be positive and strictly shorter than that timeout. Changing the client preference does not change the gateway setting. See the [satellite connection contract](../api/satellites.md#live-connection-contract).

## Private development tunnel sign-in

1. Configure an HTTPS `devtunnels.ms` origin and enable **Use devtunnels token authentication**.
2. If the tray reports **Needs authentication**, select **Sign in to configured tunnel… (token still required)**. The application opens only your configured origin in the default browser, without an API key or token in the URL.
3. Complete browser sign-in using the tunnel's permitted account. Obtain a client access token through the tunnel owner's supported tooling, such as Visual Studio's **Copy Tunnel Access Token** action. Do not use a management or host token as a substitute.
4. Enter the client token in the masked **Tunnel access token** field, save, and select **Connect**.

The client sends the gateway API key and tunnel token in separate headers. It does not extract browser cookies, parse arbitrary login-page HTML, follow credential-bearing redirects to another origin, or enable anonymous tunnel access. An expired or rejected token requires another explicit update through Settings.

Microsoft documents the non-browser token contract in [Dev Tunnels security](https://learn.microsoft.com/azure/developer/dev-tunnels/security#tunnel-access) and the token-copy action in [Visual Studio Dev Tunnels](https://learn.microsoft.com/aspnet/core/test/dev-tunnels#dev-tunnels-tool-window).

## Connection states and shutdown

| Tray state | Meaning and action |
| --- | --- |
| Offline | No active connection. Check Settings and select Connect. |
| Connecting | The initial authenticated connection is being established. |
| Online | The SignalR connection is active; parameterless heartbeats maintain registry freshness. |
| Reconnecting | A network or active-connection failure triggered a retry. Delays grow from 1 to 30 seconds and remain capped. |
| Needs authentication | Missing credentials or an authentication/permission rejection stopped retries. Update Settings; browser handoff never starts automatically. |

**Stop** cancels the session and closes the active connection. **Exit** awaits shutdown and removes the tray icon. Connection and heartbeat operations have finite timeouts. Retry delays reset after a successful connection.

Only one tray process runs per Windows session. The gateway's existing duplicate contract is last connection wins: replacing a connection invalidates the older connection's heartbeat, and the older disconnect cannot take the replacement offline. Do not share one satellite API key among concurrently running devices; use separate registrations to avoid repeated replacement/reconnect behavior.

## Validation coverage and limits

Portable tests cover credential loading, cancellation, state transitions, configurable heartbeat scheduling, replacement recovery, bounded backoff, explicit browser handoff, separated authentication headers, and origin containment in negotiation and WebSocket upgrades. Existing server tests own authenticated registration, active heartbeat, disconnect, duplicate replacement, and owner isolation.

A successful build or portable test run is not evidence that Windows tray rendering or an external tunnel sign-in was exercised. Record those runtime checks separately when performed. No real tunnel is required by the portable tests.
