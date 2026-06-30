using System.Text;
using System.Text.Json;

namespace ThreeXui.ConnectionStrings;

/// <summary>
/// Builds <c>ss://&lt;base64(method:password)&gt;@&lt;host&gt;:&lt;port&gt;#&lt;name&gt;</c>.
/// 3x-ui SS uses a top-level <c>password</c> + <c>method</c> shape (single-client
/// inbound). Match-by-client is moot here; the top-level fields are read
/// directly.
/// </summary>
public sealed class ShadowsocksConnectionStringBuilder : IXuiConnectionStringBuilder
{
    public string Protocol => "shadowsocks";

    public string Build(XuiConnectionStringRequest request)
    {
        var xuiData = request.Inbound;
        string method = "chacha20-ietf-poly1305";
        string password = string.Empty;
        if (!string.IsNullOrWhiteSpace(xuiData.Settings))
        {
            try
            {
                using var doc = JsonDocument.Parse(xuiData.Settings);
                if (
                    doc.RootElement.TryGetProperty("method", out var m)
                    && m.ValueKind == JsonValueKind.String
                )
                    method = m.GetString() ?? method;
                if (
                    doc.RootElement.TryGetProperty("password", out var p)
                    && p.ValueKind == JsonValueKind.String
                )
                    password = p.GetString() ?? string.Empty;
            }
            catch (JsonException)
            {
                // Surface a still-well-formed URI for diagnostics; the client app
                // will refuse the connection on empty creds.
            }
        }

        var userInfo = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{method}:{password}"));
        var name = Uri.EscapeDataString(request.Name);
        // SS carries no transport query; only the public host/port can be
        // overridden by streamSettings.externalProxy[0] (CDN/front endpoint).
        var parsed = StreamSettingsExtractor.Parse(xuiData.StreamSettings);
        var ep = EndpointResolver.Resolve(
            parsed,
            request.InboundPort,
            request.PublicHost,
            request.BaseUrl
        );
        return $"ss://{userInfo}@{ep.Host}:{ep.Port}#{name}";
    }
}
