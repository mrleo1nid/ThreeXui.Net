using System.Text;
using System.Text.Json;

namespace ThreeXui.ConnectionStrings;

/// <summary>
/// Builds <c>vmess://&lt;base64-json&gt;</c>. The JSON shape is the de-facto
/// v2rayN format. Base64 is the standard variant (NOT url-safe) — clients expect
/// that exact encoding.
/// </summary>
public sealed class VmessConnectionStringBuilder : IXuiConnectionStringBuilder
{
    public string Protocol => "vmess";

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
        var aid =
            SettingsExtractor.GetClientFieldByMatch(
                xuiData.Settings,
                matchFieldName: "id",
                matchValue: request.ExternalClientId,
                targetFieldName: "alterId"
            ) ?? "0";
        var parsed = StreamSettingsExtractor.Parse(xuiData.StreamSettings);
        var ep = EndpointResolver.Resolve(
            parsed,
            request.InboundPort,
            request.PublicHost,
            request.BaseUrl
        );

        var isGrpc = string.Equals(parsed.Network, "grpc", StringComparison.Ordinal);
        var isKcp = string.Equals(parsed.Network, "kcp", StringComparison.Ordinal);
        var isTcp = string.Equals(parsed.Network, "tcp", StringComparison.Ordinal);

        // VMess "type" carries the gRPC mode for grpc, or the obfuscation header
        // type for tcp/kcp; "none" otherwise.
        var vmessType = isGrpc
            ? parsed.GrpcMode ?? "gun"
            : (isTcp || isKcp) && !string.IsNullOrEmpty(parsed.HeaderType)
                ? parsed.HeaderType
                : "none";

        var vmessHost = isGrpc ? parsed.ServiceName ?? "" : parsed.Host ?? "";
        var vmessPath = isGrpc
            ? parsed.ServiceName ?? ""
            : isKcp
                ? parsed.Seed ?? ""
                : parsed.Path ?? "";
        // tls from the resolved endpoint security (externalProxy forceTls may
        // override streamSettings.security); empty string when none.
        var vmessTls = string.Equals(ep.Security, "none", StringComparison.Ordinal)
            ? ""
            : ep.Security;
        var vmessAlpn = parsed.Alpn is { Length: > 0 } ? string.Join(",", parsed.Alpn) : "";

        var payload = new
        {
            v = "2",
            ps = request.Name,
            add = ep.Host,
            port = ep.Port.ToString(),
            id = uuid,
            aid = aid,
            net = parsed.Network,
            type = vmessType,
            host = vmessHost,
            path = vmessPath,
            tls = vmessTls,
            sni = parsed.Sni ?? "",
            fp = parsed.Fingerprint ?? "",
            alpn = vmessAlpn,
        };
        var json = JsonSerializer.Serialize(payload);
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        return "vmess://" + b64;
    }
}
