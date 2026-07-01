using System.Net;
using System.Net.Http;
#if !NETSTANDARD2_0
using System.Net.Security;
#endif

namespace ThreeXui.Http;

/// <summary>
/// Default <see cref="IXuiHttpClientFactory"/>. On modern .NET it builds a
/// <c>SocketsHttpHandler</c> (pooled connection lifetime + per-handler TLS
/// options); on netstandard2.0 it falls back to <c>HttpClientHandler</c> with
/// the equivalent cookie / redirect / cert settings. Stateless and thread-safe —
/// a single instance can be shared.
/// </summary>
public sealed class XuiHttpClientFactory : IXuiHttpClientFactory
{
    /// <summary>
    /// Per-request timeout applied when the caller doesn't specify one. Shared as
    /// a single source of truth so error messages that quote the timeout stay in
    /// sync with the value actually used.
    /// </summary>
    internal const int DefaultTimeoutSeconds = 30;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds);

    public HttpClient Create(Uri baseAddress, bool allowInsecureTls, TimeSpan? timeout = null)
    {
        if (baseAddress is null)
            throw new ArgumentNullException(nameof(baseAddress));
        if (!baseAddress.IsAbsoluteUri)
            throw new ArgumentException("Base address must be an absolute URI.", nameof(baseAddress));

        var http = new HttpClient(BuildHandler(allowInsecureTls), disposeHandler: true)
        {
            BaseAddress = baseAddress,
            Timeout = timeout ?? DefaultTimeout,
        };
        return http;
    }

    private static HttpMessageHandler BuildHandler(bool allowInsecureTls)
    {
        // CookieContainer + UseCookies=true is what makes the 3x-ui cookie
        // session work — XuiClient logs in once, the Set-Cookie response header
        // lands here, and every subsequent request automatically attaches the
        // cookie. AllowAutoRedirect=false so login responses don't silently
        // follow 302s away from /login.
#if NETSTANDARD2_0
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = false,
        };
        if (allowInsecureTls)
        {
            // Per-backend opt-in to skip cert validation (self-signed panels).
            handler.ServerCertificateCustomValidationCallback = static (_, _, _, _) => true;
        }
        return handler;
#else
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = false,
        };
        if (allowInsecureTls)
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            };
        }
        return handler;
#endif
    }
}
