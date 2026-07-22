namespace ThreeXui.ConnectionStrings;

/// <summary>
/// Builds <c>vless://&lt;uuid&gt;@&lt;host&gt;:&lt;port&gt;?...#&lt;name&gt;</c>.
/// The UUID is the <c>ExternalClientId</c> matched against
/// <c>settings.clients[*].id</c>. If the match fails it falls back to a zero-UUID
/// so the link is at least well-formed for diagnostics.
/// </summary>
public sealed class VlessConnectionStringBuilder : IXuiConnectionStringBuilder
{
    public string Protocol => "vless";

    public string Build(XuiConnectionStringRequest request)
    {
        var xuiData = request.Inbound;
        var uuid =
            SettingsExtractor.GetClientFieldByMatch(
                xuiData.Settings,
                matchFieldName: "id",
                matchValue: request.ExternalClientId,
                targetFieldName: "id"
            ) ?? Guid.Empty.ToString();
        var name = Uri.EscapeDataString(request.Name);
        var parsed = StreamSettingsExtractor.Parse(xuiData.StreamSettings);
        var ep = EndpointResolver.Resolve(
            parsed,
            request.InboundPort,
            request.PublicHost,
            request.BaseUrl
        );

        // VLESS always carries an explicit encryption=none (literal); trojan does
        // not, so this stays vless-specific rather than in BuildVlessTrojanQuery.
        var query =
            $"encryption=none&{StreamSettingsExtractor.BuildVlessTrojanQuery(parsed, ep.Security, request.ForcedFingerprint, request.ForcedPacketEncoding)}";

        // flow lives in settings.clients[].flow (NOT streamSettings); required for
        // reality+Vision inbounds (e.g. xtls-rprx-vision). Append only if present.
        var flow = SettingsExtractor.GetClientFieldByMatch(
            xuiData.Settings,
            matchFieldName: "id",
            matchValue: request.ExternalClientId,
            targetFieldName: "flow"
        );
        if (!string.IsNullOrEmpty(flow))
            query += $"&flow={Uri.EscapeDataString(flow!)}";

        return $"vless://{uuid}@{ep.Host}:{ep.Port}?{query}#{name}";
    }
}
