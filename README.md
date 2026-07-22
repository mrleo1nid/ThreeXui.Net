# ThreeXui.Net

[![Build](https://github.com/mrleo1nid/ThreeXui.Net/actions/workflows/build.yml/badge.svg)](https://github.com/mrleo1nid/ThreeXui.Net/actions/workflows/build.yml)
[![Tests](https://github.com/mrleo1nid/ThreeXui.Net/actions/workflows/test.yml/badge.svg)](https://github.com/mrleo1nid/ThreeXui.Net/actions/workflows/test.yml)
[![NuGet](https://img.shields.io/nuget/v/ThreeXui.Net.svg)](https://www.nuget.org/packages/ThreeXui.Net/)
[![Downloads](https://img.shields.io/nuget/dt/ThreeXui.Net.svg)](https://www.nuget.org/packages/ThreeXui.Net/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A standalone, reusable .NET client for the **3x-ui / x-ui** panel REST API.
Multi-targets **netstandard2.0** (max reach: .NET Framework 4.6.1+, Mono, Unity)
and **net10.0** (in-box BCL, no polyfills).

## What it does

- **Cookie-session auth** with lazy login and automatic re-auth on an expired session
  (`401`, `403`, or a `301`/`302`/`307`/`308` redirect back to `/login`), deduplicated
  under a login gate so concurrent calls never burn duplicate logins.
- **Inbound listing** (`/panel/api/inbounds/list`) and single-inbound fetch.
- **Per-client traffic** (`GetInboundClientTrafficAsync`) from the list endpoint —
  not the single-inbound one, which some 3x-ui versions (confirmed on v2.8.11)
  don't preload `clientStats` on.
- **Per-client CRUD** (add / update / remove) via a *Get → mutate `settings.clients[]` →
  Update inbound* fallback, which works on forks without reliable `addClient` / `delClient`
  endpoints (notably x-ui v2.4.11). Serialized by a per-inbound mutex.
- **Health probing** with classified error messages (TLS handshake, unreachable host,
  HTML-instead-of-API, auth failure, server 5xx).
- **Connection-string builders** for `vless`, `vmess`, `trojan`, `shadowsocks`, including
  `streamSettings` transport/security rendering (tcp, mKCP, WebSocket, gRPC,
  HTTPUpgrade, XHTTP — all six 3x-ui transports) and `externalProxy` (CDN/front)
  endpoints. `XuiConnectionStringRequest.ForcedFingerprint`/`ForcedPacketEncoding`
  let the caller override/add link params (`fp`, `packetEncoding`) that 3x-ui
  itself doesn't set but real client apps need.

## Install

```bash
dotnet add package ThreeXui.Net
```

## Dependency injection

The simplest way to wire the client into an `IServiceCollection`
(ASP.NET Core, generic host, worker service):

```csharp
using ThreeXui;
using ThreeXui.DependencyInjection;

builder.Services.AddXuiClient(options =>
{
    options.BaseAddress = new Uri("https://panel.example.com:2053/");
    options.Username = "admin";
    options.Password = "secret";
    options.AllowInsecureTls = false;            // true for self-signed panels
    // options.Timeout = TimeSpan.FromSeconds(30);
});
```

Then just inject `IXuiClient`:

```csharp
public sealed class PanelService(IXuiClient client)
{
    public Task<XuiHealthCheckResult> PingAsync(CancellationToken ct) =>
        client.CheckHealthAsync(ct);
}
```

`AddXuiClient` registers `IXuiClient` as a **singleton** — one instance keeps the
cookie session and per-inbound mutexes that serialize concurrent client
mutations. The `HttpClient` is built once from the registered
`IXuiHttpClientFactory` (a default is added unless you registered your own), and
an `ILogger<XuiClient>` is picked up automatically when logging is configured.

## Quick start

```csharp
using ThreeXui;
using ThreeXui.Http;

var httpFactory = new XuiHttpClientFactory();
var http = httpFactory.Create(
    baseAddress: new Uri("https://panel.example.com:2053/"),
    allowInsecureTls: false);

using IXuiClient client = new XuiClient(http, username: "admin", password: "secret");

// Health
var health = await client.CheckHealthAsync(ct);

// List inbounds
var inbounds = await client.ListInboundsAsync(ct);

// Add a client to an inbound
var result = await client.AddClientAsync(
    inboundExternalId: "1",
    new AddClientRequest(
        Name: "alice",
        Email: "alice-ab12cd34",
        Protocol: "vless",
        LimitIp: 0,
        ExpiresAt: DateTimeOffset.UtcNow.AddDays(30)),
    ct);

// Build a share link
using ThreeXui.ConnectionStrings;

var resolver = new XuiConnectionStringBuilderResolver();
var inbound = await client.GetInboundAsync("1", ct);
var link = resolver.Resolve("vless")!.Build(new XuiConnectionStringRequest(
    ExternalClientId: result.ExternalClientId,
    Name: "alice",
    InboundPort: inbound!.Port,
    PublicHost: null,
    BaseUrl: "https://panel.example.com:2053/",
    Inbound: inbound));
```

## Notes

- `XuiClient` takes an already-built `HttpClient` and owns it for its lifetime —
  it implements `IDisposable`, so `Dispose()` (or the DI container at shutdown)
  disposes the `HttpClient` and the internal locks. Use `XuiHttpClientFactory`
  (cookie container + no auto-redirect + optional self-signed TLS opt-in), or
  supply your own.
- `AddXuiClient` enforces the base-URL policy: HTTPS is always allowed, plain
  HTTP only for private/loopback/`*.local` hosts. A public `http://` panel is
  rejected. Use `XuiBaseUrlValidator` directly to check a URL yourself.
- Multiple 3x-ui / x-ui versions are supported: cross-version behaviour (e.g.
  `GetServerInfoAsync`, health probing) degrades gracefully from the newest
  `/panel/api/server/status` shape down to old forks (x-ui v2.4.11) that expose
  only `/panel/api/inbounds`.
- `AllowInsecureTls` on the netstandard2.0 target requires .NET Framework 4.7.1+
  (self-signed cert skipping is a no-op-throwing API on 4.6.1–4.7.0); on
  net10.0 / modern .NET it always works.
- Logging is optional and uses `Microsoft.Extensions.Logging.Abstractions` —
  pass an `ILogger<XuiClient>` or nothing.
- Secret storage, credential decryption, caching and metrics are intentionally **out of
  scope**: the library does the protocol, the host application owns persistence.

## License

MIT — see [LICENSE](LICENSE).
