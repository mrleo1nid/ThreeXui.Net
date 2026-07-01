# Changelog

All notable changes to ThreeXui.Net are documented here. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/); versions follow SemVer.

## [Unreleased]

### Added

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
