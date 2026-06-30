# Changelog

All notable changes to ThreeXui.Net are documented here. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/); versions follow SemVer.

## [0.1.0] - 2026-06-30

### Added

- Initial extraction from TelegramPnvPanel into a standalone, multi-targeted
  (`netstandard2.0` + `net10.0`) library.
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

### Removed (relative to the in-app code)

- Temporary diagnostic (`TEMP-DIAG`) logging that existed only while diagnosing the
  inbound-overwrite wire format.
