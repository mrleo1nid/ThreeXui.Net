namespace ThreeXui;

/// <summary>
/// Abstraction over a single configured 3x-ui / x-ui backend. Construct one per
/// panel with <see cref="XuiClient"/> (pass an <c>HttpClient</c> pre-configured
/// for the backend's base URL + TLS policy, plus the admin login/password).
///
/// <para>
/// 3x-ui authenticates via cookie sessions. The implementation handles lazy
/// login + 401 re-auth + classified error reporting; callers only see
/// success / exceptions for transport faults.
/// </para>
///
/// <para>
/// The surface is read-only on inbounds themselves and mutation-only on the
/// clients <em>inside</em> them: the panel does not create inbounds (admins do
/// that on the 3x-ui side); callers list inbounds and then add/update/remove
/// the per-user clients within a chosen inbound.
/// </para>
///
/// <para>
/// Implements <see cref="System.IDisposable"/>: the client owns its
/// <c>HttpClient</c> for its lifetime, so dispose it (or let the DI container
/// dispose the registered singleton) at shutdown.
/// </para>
/// </summary>
public interface IXuiClient : IDisposable
{
    /// <summary>
    /// Probes the backend's health endpoint. Returns ok=true with a non-null
    /// latency when the backend responded 2xx; ok=false with a best-effort
    /// error message otherwise. Never throws.
    /// </summary>
    Task<XuiHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns minimal server-info (version, inbound count) for a status card.
    /// The inbound count comes from <c>/panel/api/inbounds/list</c> and throws on
    /// transport errors. The version is best-effort across fork shapes (GET then
    /// POST <c>/panel/api/server/status</c>) and degrades to <c>"unknown"</c> when
    /// the server API is absent (e.g. x-ui v2.4.11) rather than throwing.
    /// </summary>
    Task<XuiServerInfo> GetServerInfoAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Fetches every inbound the backend exposes via
    /// <c>/panel/api/inbounds/list</c>. The <c>SettingsJson</c> field is
    /// returned as the raw stringified JSON (3x-ui nests JSON inside JSON) —
    /// callers parse it only when they need to mutate.
    /// </summary>
    Task<IReadOnlyList<XuiInboundSummaryDto>> ListInboundsAsync(
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Fetches a single inbound by its 3x-ui id. Returns <c>null</c> on 404 so
    /// callers can detect an inbound that was deleted out-of-band on the 3x-ui
    /// side.
    ///
    /// <para>
    /// <b>Does not reliably carry per-client traffic.</b> On at least 3x-ui
    /// v2.8.11, the single-inbound endpoint's backing query omits
    /// <c>clientStats</c> entirely (confirmed against source — no
    /// <c>Preload("ClientStats")</c> on that code path), so
    /// <c>RawInboundJson</c>'s <c>clientStats</c> will be <c>null</c> even
    /// though clients have real traffic. Use
    /// <see cref="GetInboundClientTrafficAsync"/> for traffic instead.
    /// </para>
    /// </summary>
    Task<XuiInboundDto?> GetInboundAsync(string externalId, CancellationToken cancellationToken);

    /// <summary>
    /// Per-client traffic (email → up/down bytes) for the given inbound.
    /// Sourced from <c>/panel/api/inbounds/list</c> — unlike
    /// <see cref="GetInboundAsync"/>'s single-inbound endpoint, the list
    /// endpoint reliably preloads <c>clientStats</c> on 3x-ui (its own UI list
    /// view depends on it). Returns an empty list if the inbound has no
    /// clients or wasn't found. Fetches and scans every inbound each call —
    /// 3x-ui doesn't expose a per-inbound traffic-only endpoint that's
    /// guaranteed cross-version, so this trades one extra bit of parsing for
    /// correctness rather than adding a second, narrower HTTP call.
    /// </summary>
    Task<IReadOnlyList<XuiClientTrafficInfo>> GetInboundClientTrafficAsync(
        string externalId,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Appends a new client to an inbound's <c>settings.clients[]</c> via the
    /// Get-mutate-UpdateInbound fallback (x-ui v2.4.11 doesn't expose a reliable
    /// per-client addClient endpoint on every fork). Serialized by a per-inbound
    /// mutex inside the client.
    ///
    /// <para>
    /// Returns the generated <c>ExternalClientId</c> (UUID for vless/vmess,
    /// password for trojan, email for shadowsocks).
    /// </para>
    ///
    /// <para>
    /// Throws <see cref="System.InvalidOperationException"/> on upstream
    /// conflicts (3x-ui rejected the mutation) and on transport errors so the
    /// caller can run its compensating cleanup.
    /// </para>
    /// </summary>
    Task<XuiAddClientResult> AddClientAsync(
        string inboundExternalId,
        AddClientRequest request,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Removes a client from an inbound's <c>settings.clients[]</c> via the same
    /// Get-mutate-UpdateInbound fallback. Idempotent: if the client is not
    /// present (already removed) the call returns without error. Throws on
    /// transport faults.
    /// </summary>
    Task RemoveClientAsync(
        string inboundExternalId,
        string externalClientId,
        string protocol,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Updates fields on a specific client inside an inbound (best-effort — some
    /// 3x-ui forks ignore per-client <c>expiryTime</c> / <c>enable</c>; the
    /// client writes them anyway and tolerates a silent no-op). Same per-inbound
    /// mutex applies.
    /// </summary>
    Task UpdateClientAsync(
        string inboundExternalId,
        string externalClientId,
        string protocol,
        UpdateClientRequest request,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Summary row from <see cref="IXuiClient.ListInboundsAsync"/>. Settings is the
/// raw stringified JSON 3x-ui returns (its <c>settings</c> field is itself a
/// JSON-stringified object); it is not parsed here because the sync path only
/// needs the technical mirror fields, not the per-client list.
/// </summary>
public sealed record XuiInboundSummaryDto(
    string ExternalId,
    int Port,
    string Protocol,
    string Remark,
    bool Enable,
    string SettingsJson
);

/// <summary>
/// One client's cumulative traffic, from <see cref="IXuiClient.GetInboundClientTrafficAsync"/>.
/// <see cref="Email"/> matches <c>settings.clients[].email</c> — the same field
/// callers use to correlate a locally-stored client with its 3x-ui row.
/// </summary>
public sealed record XuiClientTrafficInfo(string Email, long UpBytes, long DownBytes);

/// <summary>
/// Request body for <see cref="IXuiClient.AddClientAsync"/>. Carries the
/// protocol so the implementation can pick the right secret-generator
/// (vless/vmess → UUID, trojan → password, ss → email-only single-client).
///
/// <para>
/// <see cref="Name"/> is the user-visible label. <see cref="Email"/> is the
/// deterministic, ASCII-safe 3x-ui <c>email</c> label; the implementation
/// writes it into <c>settings.clients[].email</c> (and uses it as the
/// shadowsocks match-field) instead of the raw <see cref="Name"/>.
/// </para>
/// </summary>
public sealed record AddClientRequest(
    string Name,
    string Email,
    string Protocol,
    int LimitIp,
    DateTimeOffset? ExpiresAt
);

/// <summary>
/// Result of <see cref="IXuiClient.AddClientAsync"/>.
/// <see cref="ExternalClientId"/> is the value the caller stores to later match
/// the client inside the inbound.
/// </summary>
public sealed record XuiAddClientResult(string ExternalClientId);

/// <summary>
/// Request body for <see cref="IXuiClient.UpdateClientAsync"/>. All fields
/// optional — null means "leave as is".
/// </summary>
public sealed record UpdateClientRequest(
    int? LimitIp,
    DateTimeOffset? ExpiresAt,
    bool? Enable,
    string? Name
);

/// <summary>
/// Round-trip shape for a 3x-ui inbound (used by GetInbound + share flow). The
/// <c>Settings</c> blob is preserved verbatim so connection-string builders can
/// extract their per-protocol secrets.
/// </summary>
/// <param name="Id">The 3x-ui inbound id.</param>
/// <param name="Port">The inbound's listening port.</param>
/// <param name="Protocol">The inbound protocol (vless/vmess/trojan/shadowsocks).</param>
/// <param name="Settings">Verbatim stringified-JSON <c>settings</c> blob (carries <c>clients[]</c>).</param>
/// <param name="Remark">The inbound remark / label.</param>
/// <param name="Up">Upload byte counter.</param>
/// <param name="Down">Download byte counter.</param>
/// <param name="Total">Total byte quota (0 = unlimited).</param>
/// <param name="Enable">Whether the inbound is enabled.</param>
/// <param name="RawInboundJson">
/// Raw, verbatim JSON of the 3x-ui inbound object (the <c>obj</c> from
/// <c>GetInbound</c>), including unknown fork-specific fields. Client-CRUD echoes
/// this back as the full <c>update/{id}</c> body with only the <c>settings</c>
/// field swapped — 3x-ui's update overwrites the whole row, so a partial body
/// would wipe port/protocol/streamSettings/sniffing/enable.
/// </param>
/// <param name="LastActivityAt">
/// Last-seen timestamp from 3x-ui's <c>clientStats[*].last_login</c>. Null if
/// 3x-ui hasn't recorded any session yet or the field is absent in the backend's
/// response.
/// </param>
/// <param name="StreamSettings">
/// Verbatim stringified-JSON of the 3x-ui <c>streamSettings</c> object (e.g.
/// <c>{"network":"ws","security":"tls","wsSettings":{...}}</c>). Null when the
/// inbound response doesn't include the field (older forks). Parsed by the
/// connection-string builders to produce the correct transport query / VMess
/// JSON fields.
/// </param>
public sealed record XuiInboundDto(
    string Id,
    int Port,
    string Protocol,
    string Settings,
    string Remark,
    long Up,
    long Down,
    long Total,
    bool Enable,
    string RawInboundJson = "",
    DateTime? LastActivityAt = null,
    string? StreamSettings = null
);

/// <summary>
/// Outcome of <see cref="IXuiClient.CheckHealthAsync"/>. <see cref="Latency"/>
/// is recorded even on failure so a metrics histogram still receives a sample.
/// </summary>
public sealed record XuiHealthCheckResult(bool Ok, string? ErrorMessage, TimeSpan Latency);

/// <summary>Minimal view of the 3x-ui server (version, inbound count).</summary>
public sealed record XuiServerInfo(string Version, int InboundCount);
