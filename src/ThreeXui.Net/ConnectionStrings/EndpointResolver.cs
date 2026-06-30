namespace ThreeXui.ConnectionStrings;

/// <summary>
/// Resolves the public host/port/security a share-link should advertise,
/// reconciling the inbound's own listening endpoint with a
/// <c>streamSettings.externalProxy[0]</c> entry (CDN/front endpoint).
///
/// <para>
/// 3x-ui's own share-link logic: when an externalProxy entry is present the link
/// points at <c>entry.dest:entry.port</c> instead of the listening host/port,
/// and <c>forceTls</c> overrides security (<c>tls</c>→tls, <c>none</c>→none,
/// <c>same</c>→streamSettings.security). Without an entry it falls back to the
/// backend host + inbound listening port + streamSettings.security.
/// </para>
/// </summary>
internal static class EndpointResolver
{
    /// <summary>Resolved public endpoint for a share-link.</summary>
    internal sealed record ResolvedEndpoint(string Host, int Port, string Security);

    public static ResolvedEndpoint Resolve(
        StreamSettingsExtractor.ParsedStreamSettings parsed,
        int inboundPort,
        string? publicHost,
        string baseUrl
    )
    {
        var ep = parsed.ExternalProxy;

        var port = ep?.Port ?? inboundPort;
        var host = !string.IsNullOrWhiteSpace(ep?.Dest)
            ? ep!.Dest!
            : HostResolver.Resolve(publicHost, baseUrl);
        var security =
            ep is null
                ? parsed.Security
                : ep.ForceTls switch
                {
                    "tls" => "tls",
                    "none" => "none",
                    _ => parsed.Security, // "same" (or unknown) → inherit streamSettings.security
                };

        return new ResolvedEndpoint(Host: host, Port: port, Security: security);
    }
}
