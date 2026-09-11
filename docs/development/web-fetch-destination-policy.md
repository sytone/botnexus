# Web-fetch destination policy

`web_fetch` applies its default public-destination policy at the owned HTTP transport, not only
when preparing the textual URL. `WebToolsContributor` constructs that same owned pipeline.
An explicitly injected `HttpClient` remains a caller-owned transport: its caller is responsible
for equivalent connection enforcement. That injection seam is not used by the contributor.

## Connection contract

1. Each request, including each manually followed redirect, checks schemes, literal addresses
   and the configured exact, case-insensitive hostname blocklist.
2. Each new TCP connection resolves the hostname once with cancellation. Empty/failed resolution
   fails closed. All answers are classified through `SsrfValidator.ValidateAddress`, using the
   same table as URL literals, including IPv6 embedded/mapped forms.
3. Any forbidden answer rejects the entire set before the connector is called. A public first
   answer does not excuse a private second answer.
4. Connections use only `IPEndPoint` values from the approved set, never a hostname-based socket
   overload. Socket failure can try another already-approved address, without another DNS lookup.
5. A pooled socket remains bound to its original destination. A new connection resolves and
   validates again. The transport snapshots its security policy for its lifetime; recreate the
   tool to apply configuration changes rather than reinterpreting an existing private socket.
6. The original URL and Host remain intact. The framework retains normal TLS certificate and SNI
   handling. Requests use HTTP/1.1 exactly so HTTP/3 cannot bypass the TCP connection callback.
   Automatic redirects remain disabled; the tool checks every hop.

The shared validator is **not** a complete DNS security boundary on its own. `Validate(Uri)`
remains a lexical admission check for compatibility with its sibling consumers. Its address
classifier preserves the existing table; this change does not broaden that table to ban all
reserved/documentation ranges. Documentation addresses in tests are controlled permitted
examples, not assertions of Internet routability.

## Proxy and NO_PROXY contract

The previous owned `HttpClientHandler` inherited the framework default proxy. The new transport
consults `HttpClient.DefaultProxy`, preserving the framework's environment/platform selection
and its `NO_PROXY`/bypass interpretation; it does not implement its own environment parser.

| Policy | Selected route | Behavior |
| --- | --- | --- |
| `allowPrivateNetworks=false` | Active HTTP, HTTPS or SOCKS proxy | Explicit SSRF error before DNS or connection. No silent direct fallback. |
| `allowPrivateNetworks=false` | Framework approves direct/bypass route, including NO_PROXY | Direct connection with all-answer validation and numerical pinning. |
| `allowPrivateNetworks=true` | Proxy or direct | Explicit opt-in permits private destinations and ordinary proxy routing; hostname blocklist still applies to every request/hop. |

A forward proxy or CONNECT/SOCKS intermediary can resolve the ultimate origin itself. Validating
only the socket to that intermediary cannot prove its chosen origin address. Therefore restricted
mode refuses active proxy routes, rather than presenting a prelookup as end-to-end enforcement.
It uses a direct-only socket handler **after** the per-request proxy decision permits direct
access, preventing a second proxy decision from changing that route. Opt-in mode retains proxy
credentials/routing through `SocketsHttpHandler`. This is an intentional fail-closed compatibility
change for restricted deployments requiring a proxy; it is not advice to enable private access.

## Regression evidence

`WebFetchDnsPolicyTests` uses the real owned transport and `SocketsHttpHandler`, substituting only
DNS, numerical socket connection and proxy-decision dependencies. Every socket in its fixture is
to a newly owned loopback listener. Forbidden addresses are never contacted; the seam records the
requested numerical endpoint before mapping a permitted control to the fixture.

Coverage includes all shared blocked address classes, mixed answers in both orders, empty/failed
resolution, DNS changes on a subsequent connection, permitted addresses and fallback, redirect
hops, opt-in/blocklist behavior, proxy refusal and bypass, cancellation, and contributor-owned
construction and requests. No process-global proxy or environment mutation is needed. Proxy
bypass tests exercise the `IWebProxy` decision consumed from `DefaultProxy`; parsing NO_PROXY is
left to the framework rather than duplicated here.

Tests were written before the implementation. The guard-absent RED snapshot used the same real
socket pipeline and fixture, but omitted the address/proxy/request policy. Remote core run
`20260906173214-203d1b92` recorded 106 named negative failure lines, including
`ExecuteAsync_DnsPrivateAnswer_BlocksBeforeConnector` returning HTTP 200 for forbidden answers.
It then reached its test deadline in the unguarded SOCKS cases: `result.tests` is null, so this is
partial behavioral RED evidence, not a completed RED suite tally. The subsequent candidate must
pass the authoritative complete remote core contract before publication. No assertions were
removed or relaxed to make the guarded candidate pass.

## Bounded sibling audit

Audit baseline: `d67942adf9e3886b7e78c9f132198408eb0272e1`. Exact searches of `src` for
`SsrfValidator`, `WebhookUrl`, `webhook_url`, DNS and connection callbacks plus reads of the
following paths establish the boundary; an absent graph edge was not used as evidence.

| Consumer | Source-based finding | Disposition |
| --- | --- | --- |
| Browser tools | `BrowserToolsUrlGuard.Validate` performs lexical admission; `GuardedBrowserSession.NavigateAsync` then calls the driver; `AgentBrowserCli.NavigateAsync` forwards the original URL to the external browser. Snapshot validation occurs after navigation. No destination pin is supplied by this path. | Independently tracked in [#4030](https://github.com/sytone/botnexus/issues/4030); this web-fetch transport does not secure browser navigation, redirects or subresources. |
| Cron webhook URLs | `CronWebhookUrl.TryNormalize` applies the lexical validator with configured blocked hosts. `CronController` and `CronScheduler` admit/persist the value; the complete source reference search found no outbound sender consuming `CronJob.WebhookUrl`. | No current sending transport or exploit is claimed. Any future sender needs connection-bound validation; accepting a stored hostname is not proof of safe delivery. |

No browser, cron, config-project or unrelated transport implementation is changed here. Terminal
response disposal is separately tracked in #3976 and is not folded into this security patch.

## Framework references

- [SocketsHttpHandler.ConnectCallback (.NET 10)](https://learn.microsoft.com/dotnet/api/system.net.http.socketshttphandler.connectcallback?view=net-10.0)
- [DNS asynchronous resolution](https://learn.microsoft.com/dotnet/api/system.net.dns.gethostaddressesasync?view=net-10.0)
- [Socket.ConnectAsync numerical EndPoint overload](https://learn.microsoft.com/dotnet/api/system.net.sockets.socket.connectasync?view=net-10.0)
- [HttpClient.DefaultProxy and platform/environment selection](https://learn.microsoft.com/dotnet/api/system.net.http.httpclient.defaultproxy?view=net-10.0)
