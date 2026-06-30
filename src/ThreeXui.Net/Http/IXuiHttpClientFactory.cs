using System.Net.Http;

namespace ThreeXui.Http;

/// <summary>
/// Builds an <see cref="HttpClient"/> tuned for talking to a single 3x-ui
/// backend: a private <c>CookieContainer</c> (so the login cookie session
/// works), no auto-redirect (so login 302s aren't silently followed away from
/// <c>/login</c>), and an optional per-backend opt-in to skip TLS certificate
/// validation for self-signed panels.
///
/// <para>
/// This is the only piece of the original app-side factory that is reusable
/// without a database / secret store: the caller still owns where the base URL,
/// credentials and TLS flag come from.
/// </para>
/// </summary>
public interface IXuiHttpClientFactory
{
    /// <summary>
    /// Creates a fresh <see cref="HttpClient"/> for the given backend. The
    /// caller owns the returned instance (and should dispose it / cache it with
    /// a sensible lifetime). A new handler is built per call so the cookie
    /// container and TLS policy stay isolated per backend.
    /// </summary>
    /// <param name="baseAddress">The 3x-ui panel API root, e.g. <c>https://panel.example.com:2053/</c>.</param>
    /// <param name="allowInsecureTls">When <c>true</c>, certificate validation is skipped (self-signed panels).</param>
    /// <param name="timeout">Per-request timeout. Defaults to 30s when <c>null</c>.</param>
    HttpClient Create(Uri baseAddress, bool allowInsecureTls, TimeSpan? timeout = null);
}
