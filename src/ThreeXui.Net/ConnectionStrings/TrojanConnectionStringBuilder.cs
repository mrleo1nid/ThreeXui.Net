namespace ThreeXui.ConnectionStrings;

/// <summary>
/// Builds <c>trojan://&lt;password&gt;@&lt;host&gt;:&lt;port&gt;?...#&lt;name&gt;</c>.
/// Password is extracted from the client whose <c>password</c> matches the
/// <c>ExternalClientId</c>. On miss it falls back to an empty string (the client
/// app refuses to connect — same diagnostic surface as VLESS with zero-UUID).
/// </summary>
public sealed class TrojanConnectionStringBuilder : IXuiConnectionStringBuilder
{
    public string Protocol => "trojan";

    public string Build(XuiConnectionStringRequest request)
    {
        var xuiData = request.Inbound;
        var password =
            SettingsExtractor.GetClientFieldByMatch(
                xuiData.Settings,
                matchFieldName: "password",
                matchValue: request.ExternalClientId,
                targetFieldName: "password"
            ) ?? string.Empty;
        var name = Uri.EscapeDataString(request.Name);
        var parsed = StreamSettingsExtractor.Parse(xuiData.StreamSettings);
        var ep = EndpointResolver.Resolve(
            parsed,
            request.InboundPort,
            request.PublicHost,
            request.BaseUrl
        );
        var query = StreamSettingsExtractor.BuildVlessTrojanQuery(parsed, ep.Security);
        return $"trojan://{Uri.EscapeDataString(password)}@{ep.Host}:{ep.Port}?{query}#{name}";
    }
}
