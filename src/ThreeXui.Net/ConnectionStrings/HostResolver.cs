namespace ThreeXui.ConnectionStrings;

/// <summary>
/// Shared host-resolution helper used by every protocol builder. Honours the
/// caller-supplied public host first; falls back to the host portion of the
/// backend base URL. Stripping the base-URL port is intentional — VPN
/// connection strings carry their own port (the inbound port), not the 3x-ui
/// control-plane port.
/// </summary>
internal static class HostResolver
{
    public static string Resolve(string? publicHost, string baseUrl)
    {
        if (!string.IsNullOrWhiteSpace(publicHost))
            return publicHost!;
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            return uri.Host;
        return baseUrl;
    }
}
