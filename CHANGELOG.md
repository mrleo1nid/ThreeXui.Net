# Changelog

All notable changes to ThreeXui.Net are documented here. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/); versions follow SemVer.

## [Unreleased]

### Added

- `IXuiClient.GetInboundClientTrafficAsync(externalId, ct)`: per-client traffic
  (email → up/down bytes) sourced from `/panel/api/inbounds/list`. Fixes a
  cross-version trap: `GetInboundAsync`'s single-inbound endpoint
  (`/panel/api/inbounds/get/{id}`) does not reliably preload `clientStats` —
  confirmed against 3x-ui v2.8.11 source, whose single-inbound query has no
  `Preload("ClientStats")` (the list endpoint does). Code built on
  `GetInboundAsync` for traffic would silently see zero traffic for every
  client on that panel version; this method reads from the endpoint that
  actually carries it.
- `StreamSettingsExtractor`/`VlessConnectionStringBuilder`/`TrojanConnectionStringBuilder`/
  `VmessConnectionStringBuilder` now understand the `xhttp` (splithttp) transport —
  the last of 3x-ui's six transports (tcp, mKCP, WebSocket, gRPC, HTTPUpgrade,
  XHTTP) without support. `path`/`host`/`mode` are read from
  `streamSettings.xhttpSettings`, mirroring the existing ws/httpupgrade
  handling; `mode` defaults to `"auto"` when absent (3x-ui's own default), and
  an empty `host` (the common reverse-proxy shape) is omitted rather than
  emitted as `host=`.
- `XuiConnectionStringRequest.ForcedFingerprint`: overrides the TLS/Reality `fp`
  emitted in vless/trojan (`fp=`) and vmess (JSON `fp`) links regardless of
  `streamSettings.tlsSettings`/`realitySettings` — most 3x-ui inbounds leave
  fingerprint blank, and real client apps then fall back to fingerprinting the
  panel's own TLS stack unless a share-link sets one explicitly. Ignored for
  plaintext (`security=none`) links.
- `XuiConnectionStringRequest.ForcedPacketEncoding`: emits
  `packetEncoding=<value>` (e.g. `xudp`) in vless/trojan query links — not a
  3x-ui/streamSettings concept, but required by some clients for UDP relaying
  over certain transports (notably `xhttp`); 3x-ui's own generated links never
  include it. Omitted by default, so existing links are byte-for-byte
  unchanged unless a caller opts in.
- `ThreeXui.DependencyInjection.AddXuiClient(...)`: one-call DI registration of a
  singleton `IXuiClient` from `XuiClientOptions` (base URL, credentials, TLS
  policy, timeout). Builds the `HttpClient` via the registered
  `IXuiHttpClientFactory` (a default is added when absent) and resolves an
  optional `ILogger<XuiClient>`.
- CI: GitHub Actions workflows for build, tests, and tag-driven NuGet publish.
- `IXuiClient` now implements `IDisposable`; `XuiClient.Dispose()` releases the
  owned `HttpClient` and its synchronization primitives.

### Fixed

- `GetServerInfoAsync` targeted a non-existent `/panel/api/server/info` endpoint
  and ignored the response envelope, so it always returned `unknown` / `0`. It
  now reads the inbound count from `/panel/api/inbounds/list` and resolves the
  version tolerantly across fork shapes: GET `/panel/api/server/status`
  (3x-ui v2.9+/v3.x), POST fallback (older x-ui/3x-ui), and a graceful
  `"unknown"` when the server API is absent (x-ui v2.4.11).
- `CheckHealthAsync` now falls back to the `inbounds/list` probe on `405` as well
  as `404`, so forks that register the status endpoint under a different verb are
  reported healthy.
- `AddXuiClient` now enforces the `XuiBaseUrlValidator` policy (HTTPS always;
  plain HTTP only to private/loopback hosts), rejecting a public `http://` panel
  instead of silently shipping credentials in the clear.
- `XuiClient` only re-authenticated on a bare `401`. Some forks/reverse-proxies
  signal an expired session with `403` (e.g. an nginx `auth_request` denial) or a
  `301`/`302`/`307`/`308` redirect back to `/login` instead. `SendAsync` now
  treats all of those as an expired session and re-logs in + retries once, same
  as `401`.
- `CheckHealthAsync` could report a healthy panel as down after its cached
  session silently expired server-side, if the fork/reverse-proxy in front of
  it answers every panel API call with a plain `404` once the session is gone
  (instead of the `401`/`403`/redirect `SendAsync` already retries on its own).
  The 404 was indistinguishable from "this fork has no server API" (the x-ui
  v2.4.11 case the `inbounds/list` fallback exists for), so the health check
  failed instead of re-authenticating — until the next unrelated call happened
  to force a fresh login. `CheckHealthAsync` now forces one real re-login and
  retries before concluding the endpoint is genuinely absent, but only when the
  404 followed a *cached* session (a 404 right after a fresh login is still
  treated as a missing endpoint, not retried again).

### Changed

- `streamSettings` `network` / `security` values are normalized to lower-case, so
  forks emitting `WS` / `TCP` are recognized by the connection-string builders.

## [0.1.0] - 2026-06-30

### Added

- Initial release: a standalone, multi-targeted (`netstandard2.0` + `net10.0`)
  3x-ui / x-ui client library.
- `IXuiClient` / `XuiClient`: cookie-session auth (lazy login + 401 re-auth),
  `CheckHealthAsync`, `GetServerInfoAsync`, `ListInboundsAsync`, `GetInboundAsync`,
  and per-client `AddClientAsync` / `UpdateClientAsync` / `RemoveClientAsync` via the
  Get-mutate-Update fallback with a per-inbound mutex.
- `ThreeXui.Http.IXuiHttpClientFactory` / `XuiHttpClientFactory`: builds a per-backend
  `HttpClient` (cookie container, no auto-redirect, optional self-signed TLS opt-in).
- `ThreeXui.ConnectionStrings`: `vless` / `vmess` / `trojan` / `shadowsocks` builders,
  `streamSettings` parsing (ws/httpupgrade/grpc/tcp/kcp, tls/reality), and
  `externalProxy` (CDN/front) endpoint resolution — decoupled from any domain model via
  `XuiConnectionStringRequest`.
- `XuiBaseUrlValidator`: https-always / http-only-for-private-hosts policy.
