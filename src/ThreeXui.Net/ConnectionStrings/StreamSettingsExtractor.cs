using System.Text;
using System.Text.Json;

namespace ThreeXui.ConnectionStrings;

/// <summary>
/// Parses the 3x-ui <c>streamSettings</c> stringified-JSON blob
/// (<c>XuiInboundDto.StreamSettings</c>) into a normalized
/// <see cref="ParsedStreamSettings"/> and renders the transport query for
/// vless/trojan share-links (and supplies fields for the VMess JSON payload).
/// Sibling of <see cref="SettingsExtractor"/> — same "poke JSON, never throw,
/// fall back on malformed" contract.
///
/// <para>
/// 3x-ui forks disagree on reality field placement (nested <c>settings.*</c> vs
/// flat top-level), so reality reads both variants. The query order is fixed and
/// deterministic so the output matches what a 3x-ui share-link emits.
/// </para>
///
/// <para>
/// <c>streamSettings.externalProxy[]</c> describes CDN/front endpoints (e.g. a
/// Cloudflare host that proxies to the listening port). When present, the
/// share-link's host/port/security come from the entry, NOT from the inbound
/// port or backend host. Only <c>externalProxy[0]</c> is taken — one share-link
/// per config.
/// </para>
/// </summary>
internal static class StreamSettingsExtractor
{
    /// <summary>
    /// A single <c>streamSettings.externalProxy[]</c> entry (CDN/front
    /// endpoint). <see cref="Dest"/>/<see cref="Port"/> stay null when the field
    /// is absent or the wrong JSON type — callers fall back accordingly.
    /// <see cref="Remark"/> is captured verbatim but not yet consumed.
    /// </summary>
    internal sealed record ExternalProxyEntry(
        string ForceTls,
        string? Dest,
        int? Port,
        string Remark
    );

    /// <summary>
    /// Normalized, protocol-agnostic view of the transport/security knobs needed
    /// to render a share-link. Empty/absent values stay null and are omitted from
    /// the query by <see cref="BuildVlessTrojanQuery"/>.
    /// </summary>
    internal sealed record ParsedStreamSettings(
        string Network,
        string Security,
        string? Path,
        string? Host,
        string? ServiceName,
        string? GrpcMode,
        string? HeaderType,
        string? Seed,
        string? XhttpMode,
        string? Sni,
        string? Fingerprint,
        string[]? Alpn,
        string? PublicKey,
        string? ShortId,
        string? SpiderX,
        ExternalProxyEntry? ExternalProxy
    )
    {
        /// <summary>Fallback when streamSettings is null/whitespace/malformed.</summary>
        public static ParsedStreamSettings Default =>
            new(
                Network: "tcp",
                Security: "none",
                Path: null,
                Host: null,
                ServiceName: null,
                GrpcMode: null,
                HeaderType: null,
                Seed: null,
                XhttpMode: null,
                Sni: null,
                Fingerprint: null,
                Alpn: null,
                PublicKey: null,
                ShortId: null,
                SpiderX: null,
                ExternalProxy: null
            );
    }

    /// <summary>
    /// Parses the streamSettings blob. Null/whitespace/malformed JSON yields
    /// <see cref="ParsedStreamSettings.Default"/>; this method never throws.
    /// </summary>
    public static ParsedStreamSettings Parse(string? streamSettingsJson)
    {
        if (string.IsNullOrWhiteSpace(streamSettingsJson))
            return ParsedStreamSettings.Default;

        try
        {
            using var doc = JsonDocument.Parse(streamSettingsJson!);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ParsedStreamSettings.Default;

            // Normalize to lower-case: the switches below and the ordinal
            // comparisons in the protocol builders match lower-case literals, so
            // a fork that emits "WS"/"TCP" wouldn't otherwise be recognized.
            var network = (GetString(root, "network") ?? "tcp").ToLowerInvariant();
            var security = (GetString(root, "security") ?? "none").ToLowerInvariant();

            string? path = null;
            string? host = null;
            string? serviceName = null;
            string? grpcMode = null;
            string? headerType = null;
            string? seed = null;
            string? xhttpMode = null;

            switch (network)
            {
                case "ws":
                    if (GetNested(root, "wsSettings", out var ws))
                    {
                        path = GetString(ws, "path");
                        if (GetNested(ws, "headers", out var wsHeaders))
                            host = GetString(wsHeaders, "Host");
                    }
                    break;

                case "httpupgrade":
                    if (GetNested(root, "httpupgradeSettings", out var hu))
                    {
                        path = GetString(hu, "path");
                        host = GetString(hu, "host");
                    }
                    break;

                case "xhttp":
                    // 3x-ui's XHTTP (a.k.a. splithttp) inbound. host is frequently
                    // left blank (reverse-proxy setups where the CDN/front supplies
                    // its own Host header) — Append() below omits it when empty,
                    // same as ws/httpupgrade.
                    if (GetNested(root, "xhttpSettings", out var xhttp))
                    {
                        path = GetString(xhttp, "path");
                        host = GetString(xhttp, "host");
                        xhttpMode = GetString(xhttp, "mode");
                    }
                    break;

                case "grpc":
                    if (GetNested(root, "grpcSettings", out var grpc))
                    {
                        serviceName = GetString(grpc, "serviceName");
                        grpcMode = GetBool(grpc, "multiMode") ? "multi" : "gun";
                    }
                    break;

                case "tcp":
                    if (
                        GetNested(root, "tcpSettings", out var tcp)
                        && GetNested(tcp, "header", out var tcpHeader)
                    )
                    {
                        headerType = GetString(tcpHeader, "type");
                        if (
                            string.Equals(headerType, "http", StringComparison.Ordinal)
                            && GetNested(tcpHeader, "request", out var request)
                        )
                        {
                            path = GetStringArrayFirst(request, "path");
                            if (GetNested(request, "headers", out var reqHeaders))
                                host = GetStringArrayFirst(reqHeaders, "Host");
                        }
                    }
                    break;

                case "kcp":
                    if (GetNested(root, "kcpSettings", out var kcp))
                    {
                        if (GetNested(kcp, "header", out var kcpHeader))
                            headerType = GetString(kcpHeader, "type");
                        seed = GetString(kcp, "seed");
                    }
                    break;
            }

            string? sni = null;
            string? fingerprint = null;
            string[]? alpn = null;
            string? publicKey = null;
            string? shortId = null;
            string? spiderX = null;

            switch (security)
            {
                case "tls":
                    if (GetNested(root, "tlsSettings", out var tls))
                    {
                        sni = GetString(tls, "serverName");
                        fingerprint = GetString(tls, "fingerprint");
                        alpn = GetStringArray(tls, "alpn");
                    }
                    break;

                case "reality":
                    if (GetNested(root, "realitySettings", out var reality))
                    {
                        // sni: serverNames[0] then serverName.
                        sni =
                            GetStringArrayFirst(reality, "serverNames")
                            ?? GetString(reality, "serverName");
                        // sid: shortIds[0] then shortId.
                        shortId =
                            GetStringArrayFirst(reality, "shortIds")
                            ?? GetString(reality, "shortId");

                        // pbk/spx/fp: nested settings.* first, then top-level.
                        var hasNested = GetNested(reality, "settings", out var realitySettings);
                        publicKey =
                            (hasNested ? GetString(realitySettings, "publicKey") : null)
                            ?? GetString(reality, "publicKey");
                        spiderX =
                            (hasNested ? GetString(realitySettings, "spiderX") : null)
                            ?? GetString(reality, "spiderX");
                        fingerprint =
                            (hasNested ? GetString(realitySettings, "fingerprint") : null)
                            ?? GetString(reality, "fingerprint");
                    }
                    break;
            }

            var externalProxy = ParseExternalProxy(root);

            return new ParsedStreamSettings(
                Network: network,
                Security: security,
                Path: path,
                Host: host,
                ServiceName: serviceName,
                GrpcMode: grpcMode,
                HeaderType: headerType,
                Seed: seed,
                XhttpMode: xhttpMode,
                Sni: sni,
                Fingerprint: fingerprint,
                Alpn: alpn,
                PublicKey: publicKey,
                ShortId: shortId,
                SpiderX: spiderX,
                ExternalProxy: externalProxy
            );
        }
        catch (Exception)
        {
            return ParsedStreamSettings.Default;
        }
    }

    /// <summary>
    /// Reads <c>externalProxy[0]</c> tolerantly. Returns null when the array is
    /// absent, empty, or the first element is not an object. Missing forceTls
    /// defaults to <c>"same"</c>; non-string dest and non-numeric port stay null.
    /// </summary>
    private static ExternalProxyEntry? ParseExternalProxy(JsonElement root)
    {
        if (
            !root.TryGetProperty("externalProxy", out var arr)
            || arr.ValueKind != JsonValueKind.Array
        )
            return null;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                return null;

            var forceTls = GetString(item, "forceTls") ?? "same";
            var dest = GetStringStrict(item, "dest");
            int? port = null;
            if (
                item.TryGetProperty("port", out var portEl)
                && portEl.ValueKind == JsonValueKind.Number
                && portEl.TryGetInt32(out var p)
            )
                port = p;
            var remark = GetStringStrict(item, "remark") ?? string.Empty;

            return new ExternalProxyEntry(
                ForceTls: forceTls,
                Dest: dest,
                Port: port,
                Remark: remark
            );
        }

        return null;
    }

    /// <summary>
    /// Renders the vless/trojan share-link query (without the leading '?'). Order
    /// is fixed and deterministic; empty values are omitted. Literal keys
    /// (type/security/mode/headerType) are not escaped; values are. The
    /// <paramref name="securityOverride"/> drives the tls/reality/none branch (it
    /// may differ from <c>parsed.Security</c> when an externalProxy
    /// <c>forceTls</c> overrides it); reality/transport params come from
    /// <paramref name="parsed"/>. <paramref name="forcedFingerprint"/> and
    /// <paramref name="forcedPacketEncoding"/> mirror
    /// <see cref="XuiConnectionStringRequest.ForcedFingerprint"/> /
    /// <see cref="XuiConnectionStringRequest.ForcedPacketEncoding"/> — see their
    /// doc for semantics.
    /// </summary>
    public static string BuildVlessTrojanQuery(
        ParsedStreamSettings parsed,
        string securityOverride,
        string? forcedFingerprint = null,
        string? forcedPacketEncoding = null
    )
    {
        var sb = new StringBuilder();

        Append(sb, "type", parsed.Network, escape: false);
        Append(sb, "security", securityOverride, escape: false);

        switch (parsed.Network)
        {
            case "ws":
            case "httpupgrade":
            case "xhttp":
                Append(sb, "path", parsed.Path);
                Append(sb, "host", parsed.Host);
                if (string.Equals(parsed.Network, "xhttp", StringComparison.Ordinal))
                    Append(sb, "mode", parsed.XhttpMode ?? "auto", escape: false);
                break;
            case "grpc":
                Append(sb, "serviceName", parsed.ServiceName);
                Append(sb, "mode", parsed.GrpcMode, escape: false);
                break;
            case "tcp":
                if (string.Equals(parsed.HeaderType, "http", StringComparison.Ordinal))
                {
                    Append(sb, "headerType", "http", escape: false);
                    Append(sb, "path", parsed.Path);
                    Append(sb, "host", parsed.Host);
                }
                break;
            case "kcp":
                Append(sb, "headerType", parsed.HeaderType, escape: false);
                Append(sb, "seed", parsed.Seed);
                break;
        }

        switch (securityOverride)
        {
            case "tls":
                Append(sb, "sni", parsed.Sni);
                Append(sb, "fp", forcedFingerprint ?? parsed.Fingerprint);
                if (parsed.Alpn is { Length: > 0 })
                    Append(sb, "alpn", string.Join(",", parsed.Alpn));
                break;
            case "reality":
                Append(sb, "sni", parsed.Sni);
                Append(sb, "pbk", parsed.PublicKey);
                Append(sb, "sid", parsed.ShortId);
                Append(sb, "spx", parsed.SpiderX);
                Append(sb, "fp", forcedFingerprint ?? parsed.Fingerprint);
                break;
        }

        Append(sb, "packetEncoding", forcedPacketEncoding, escape: false);

        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string key, string? value, bool escape = true)
    {
        if (string.IsNullOrEmpty(value))
            return;
        if (sb.Length > 0)
            sb.Append('&');
        sb.Append(key).Append('=').Append(escape ? Uri.EscapeDataString(value!) : value);
    }

    private static string? GetString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    /// <summary>
    /// Like <see cref="GetString"/> but only accepts a JSON string — numbers and
    /// bools yield null. Used for externalProxy.dest/remark where a non-string
    /// value must NOT be coerced into a host.
    /// </summary>
    private static string? GetStringStrict(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string[]? GetStringArray(JsonElement obj, string name)
    {
        if (
            obj.ValueKind != JsonValueKind.Object
            || !obj.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array
        )
            return null;
        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrEmpty(s))
                    list.Add(s!);
            }
        }
        return list.Count > 0 ? list.ToArray() : null;
    }

    private static string? GetStringArrayFirst(JsonElement obj, string name)
    {
        var arr = GetStringArray(obj, name);
        return arr is { Length: > 0 } ? arr[0] : null;
    }

    private static bool GetBool(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value))
            return false;
        // 3xui forks serialize bools as native true, as the string "true", or as
        // a number (1) inside the stringified-JSON blob — coerce all three.
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => string.Equals(
                value.GetString(),
                "true",
                StringComparison.OrdinalIgnoreCase
            ),
            JsonValueKind.Number => value.TryGetInt64(out var n) && n == 1,
            _ => false,
        };
    }

    private static bool GetNested(JsonElement obj, string name, out JsonElement nested)
    {
        if (
            obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out nested)
            && nested.ValueKind == JsonValueKind.Object
        )
            return true;
        nested = default;
        return false;
    }
}
