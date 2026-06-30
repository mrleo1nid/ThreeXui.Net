using System.Net;

namespace ThreeXui;

/// <summary>
/// Pure-function validator for a 3x-ui backend's base URL. Two rules:
///
/// <list type="bullet">
///   <item><c>https://</c> — always allowed.</item>
///   <item><c>http://</c> — allowed only when the host is private / loopback /
///         docker-internal / *.local. Public hostnames over plain HTTP are
///         rejected.</item>
/// </list>
///
/// <para>Pure — no DI, no I/O.</para>
/// </summary>
public static class XuiBaseUrlValidator
{
    /// <summary>
    /// Validates the URL string. Returns <c>true</c> when the URL passes both
    /// syntactic checks (absolute, http/https) AND the scheme-specific host
    /// policy. On failure, <paramref name="reason"/> carries an English
    /// description suitable as an error-message fallback.
    /// </summary>
    public static bool IsAllowed(string? url, out string? reason)
    {
        reason = null;

        if (string.IsNullOrWhiteSpace(url))
        {
            reason = "BaseUrl is empty.";
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            reason = "BaseUrl is not a valid absolute URL.";
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
            return true;

        if (uri.Scheme != Uri.UriSchemeHttp)
        {
            reason = $"BaseUrl scheme must be http or https (was '{uri.Scheme}').";
            return false;
        }

        // http:// path — gate on host policy.
        if (IsAllowedHttpHost(uri.Host))
            return true;

        reason = "HTTP allowed only for private/loopback hosts; use HTTPS for public endpoints.";
        return false;
    }

    private static bool IsAllowedHttpHost(string host)
    {
        if (string.IsNullOrEmpty(host))
            return false;

        // Case-insensitive named hosts that should always be accepted.
        if (
            string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "host.docker.internal", StringComparison.OrdinalIgnoreCase)
        )
            return true;

        // *.local mDNS — gate on suffix; "local" alone is too unspecific to
        // match here (and is handled by the .local FQDN case below).
        if (
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            && host.Length > ".local".Length
        )
            return true;

        // IP literals: loopback / RFC1918 private ranges / IPv6 loopback.
        if (IPAddress.TryParse(host, out var ip))
        {
            if (IPAddress.IsLoopback(ip))
                return true;
            return IsPrivateIp(ip);
        }

        return false;
    }

    private static bool IsPrivateIp(IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            // Only IPv4 ranges are enumerated here — IPv6 ULA (fc00::/7) is
            // private but rare in dev; explicit operator-allowed IPv6 should use
            // https://.
            return false;

        var bytes = ip.GetAddressBytes();
        // 10/8
        if (bytes[0] == 10)
            return true;
        // 172.16/12 — 172.16.0.0 through 172.31.255.255
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            return true;
        // 192.168/16
        if (bytes[0] == 192 && bytes[1] == 168)
            return true;
        return false;
    }
}
