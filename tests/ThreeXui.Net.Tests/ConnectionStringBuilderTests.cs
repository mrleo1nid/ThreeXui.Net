using System.Text;
using System.Text.Json;
using FluentAssertions;
using ThreeXui;
using ThreeXui.ConnectionStrings;
using Xunit;

namespace ThreeXui.Net.Tests;

/// <summary>
/// Test-only stand-ins for the host application's domain entities. The library
/// builders take a flat <see cref="XuiConnectionStringRequest"/>; these records
/// + the <see cref="ConnStrBuilderTestExtensions.Build"/> bridge let the ported
/// test bodies keep their original
/// <c>builder.Build(config, inbound, xui, backend)</c> call shape.
/// </summary>
internal sealed record TestConfig(string ExternalClientId, string Name);

internal sealed record TestInbound(int Port);

internal sealed record TestBackend(string? PublicHost, string BaseUrl);

internal static class ConnStrBuilderTestExtensions
{
    public static string Build(
        this IXuiConnectionStringBuilder builder,
        TestConfig config,
        TestInbound inbound,
        XuiInboundDto xui,
        TestBackend backend
    ) =>
        builder.Build(
            new XuiConnectionStringRequest(
                ExternalClientId: config.ExternalClientId,
                Name: config.Name,
                InboundPort: inbound.Port,
                PublicHost: backend.PublicHost,
                BaseUrl: backend.BaseUrl,
                Inbound: xui
            )
        );
}

/// <summary>
/// Round-trip tests for the four protocol builders. The relevant client lives
/// inside <c>settings.clients[]</c> indexed by the request's
/// <c>ExternalClientId</c> (UUID for vless/vmess, password for trojan, email for
/// shadowsocks).
///
/// <para>
/// The "two clients in one inbound get DIFFERENT connection strings" test is the
/// critical bug-surface — pre-rework the builders read <c>clients[0]</c> so every
/// user on the same inbound got the first user's secret. The match-by-id fix MUST
/// produce distinct strings for distinct configs sharing an inbound.
/// </para>
/// </summary>
public class ConnectionStringBuilderTests
{
    private static TestBackend BackendWith(string? publicHost) =>
        new(PublicHost: publicHost, BaseUrl: "https://xui.example.com:2053");

    private static TestInbound InboundWith(string externalId, int port, string protocol) =>
        new(Port: port);

    private static TestConfig MakeConfig(string externalClientId, string name = "My EU") =>
        new(ExternalClientId: externalClientId, Name: name);

    // ─── Vless ──────────────────────────────────────────────────────────

    [Fact]
    public void Vless_Builds_ExpectedShape_PrefersPublicHost()
    {
        var builder = new VlessConnectionStringBuilder();
        var inbound = InboundWith("42", 12345, "vless");
        var xui = new XuiInboundDto(
            Id: "42",
            Port: 12345,
            Protocol: "vless",
            Settings: """{"clients":[{"id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"}]}""",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true
        );
        var config = MakeConfig("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var url = builder.Build(config, inbound, xui, BackendWith("vpn.eu.example.com"));

        url.Should().StartWith("vless://aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee@vpn.eu.example.com:12345");
        url.Should().EndWith("#My%20EU");
    }

    [Fact]
    public void Vless_FallsBackTo_BaseUrlHost_WhenPublicHost_Null()
    {
        var builder = new VlessConnectionStringBuilder();
        var inbound = InboundWith("42", 12345, "vless");
        var xui = new XuiInboundDto(
            "42", 12345, "vless",
            """{"clients":[{"id":"my-uuid"}]}""",
            "", 0, 0, 0, true
        );

        var url = builder.Build(MakeConfig("my-uuid"), inbound, xui, BackendWith(null));

        url.Should().Contain("@xui.example.com:12345");
        url.Should().NotContain(":2053"); // BaseUrl control-plane port must NOT leak.
    }

    [Fact]
    public void Vless_FallsBackToZeroUuid_WhenClientNotFound()
    {
        // Drift surface: our DB row points at an ExternalClientId not present in
        // the 3xui inbound. Builder should produce a well-formed-but-bogus link
        // (zero-UUID) so the client app refuses to connect — diagnostic surface.
        var builder = new VlessConnectionStringBuilder();
        var inbound = InboundWith("42", 12345, "vless");
        var xui = new XuiInboundDto(
            "42", 12345, "vless",
            """{"clients":[{"id":"some-other-client"}]}""",
            "", 0, 0, 0, true
        );

        var url = builder.Build(MakeConfig("missing"), inbound, xui, BackendWith("vpn.eu.example.com"));

        url.Should().StartWith("vless://00000000-0000-0000-0000-000000000000@");
    }

    // ─── CRITICAL bug surface ───────────────────────────────────────────
    [Fact]
    public void Vless_TwoConfigsInSameInbound_GetDistinctConnectionStrings()
    {
        // Highest-priority regression: pre-rework the builders read clients[0]
        // unconditionally; every user on the same inbound got the FIRST user's
        // secret. Post-rework the builder must locate THE client by
        // ExternalClientId — two configs on the same inbound MUST resolve to
        // distinct UUIDs in their connection strings.
        var builder = new VlessConnectionStringBuilder();
        var inbound = InboundWith("42", 12345, "vless");
        var xui = new XuiInboundDto(
            "42", 12345, "vless",
            "{\"clients\":[{\"id\":\"11111111-1111-1111-1111-111111111111\"},{\"id\":\"22222222-2222-2222-2222-222222222222\"}]}",
            "", 0, 0, 0, true
        );

        var aliceConfig = MakeConfig("11111111-1111-1111-1111-111111111111", "Alice");
        var bobConfig = MakeConfig("22222222-2222-2222-2222-222222222222", "Bob");
        var backend = BackendWith("vpn.eu.example.com");

        var aliceUrl = builder.Build(aliceConfig, inbound, xui, backend);
        var bobUrl = builder.Build(bobConfig, inbound, xui, backend);

        aliceUrl.Should().Contain("11111111-1111-1111-1111-111111111111");
        aliceUrl.Should().NotContain("22222222-2222-2222-2222-222222222222");
        bobUrl.Should().Contain("22222222-2222-2222-2222-222222222222");
        bobUrl.Should().NotContain("11111111-1111-1111-1111-111111111111");
        aliceUrl.Should().NotBe(bobUrl);
    }

    // ─── Vmess ──────────────────────────────────────────────────────────

    [Fact]
    public void Vmess_Encodes_Base64Json_WithMatchedClient()
    {
        var builder = new VmessConnectionStringBuilder();
        var inbound = InboundWith("42", 12345, "vmess");
        var xui = new XuiInboundDto(
            "42", 12345, "vmess",
            "{\"clients\":[{\"id\":\"first-uuid\",\"alterId\":0},{\"id\":\"my-uuid\",\"alterId\":42}]}",
            "", 0, 0, 0, true
        );

        var url = builder.Build(MakeConfig("my-uuid"), inbound, xui, BackendWith("v.example.com"));

        url.Should().StartWith("vmess://");
        var b64 = url["vmess://".Length..];
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        using var doc = JsonDocument.Parse(json);
        // The matched client's id MUST be the SECOND row's UUID, not the first.
        doc.RootElement.GetProperty("id").GetString().Should().Be("my-uuid");
        doc.RootElement.GetProperty("add").GetString().Should().Be("v.example.com");
        doc.RootElement.GetProperty("port").GetString().Should().Be("12345");
    }

    [Fact]
    public void Vmess_TwoConfigsInSameInbound_GetDistinctConnectionStrings()
    {
        var builder = new VmessConnectionStringBuilder();
        var inbound = InboundWith("42", 12345, "vmess");
        var xui = new XuiInboundDto(
            "42", 12345, "vmess",
            "{\"clients\":[{\"id\":\"alice-uuid\",\"alterId\":0},{\"id\":\"bob-uuid\",\"alterId\":0}]}",
            "", 0, 0, 0, true
        );
        var backend = BackendWith("v.example.com");

        var aliceUrl = builder.Build(MakeConfig("alice-uuid"), inbound, xui, backend);
        var bobUrl = builder.Build(MakeConfig("bob-uuid"), inbound, xui, backend);

        aliceUrl.Should().NotBe(bobUrl);
        var aliceJson = Encoding.UTF8.GetString(Convert.FromBase64String(aliceUrl["vmess://".Length..]));
        var bobJson = Encoding.UTF8.GetString(Convert.FromBase64String(bobUrl["vmess://".Length..]));
        aliceJson.Should().Contain("\"id\":\"alice-uuid\"");
        bobJson.Should().Contain("\"id\":\"bob-uuid\"");
    }

    // ─── Trojan ─────────────────────────────────────────────────────────

    [Fact]
    public void Trojan_Encodes_PasswordOf_MatchedClient()
    {
        var builder = new TrojanConnectionStringBuilder();
        var inbound = InboundWith("42", 12345, "trojan");
        var xui = new XuiInboundDto(
            "42", 12345, "trojan",
            "{\"clients\":[{\"password\":\"pwd-first\"},{\"password\":\"pwd-mine\"}]}",
            "", 0, 0, 0, true
        );

        var url = builder.Build(MakeConfig("pwd-mine"), inbound, xui, BackendWith("t.example.com"));

        url.Should().StartWith("trojan://pwd-mine@t.example.com:12345");
        url.Should().NotContain("pwd-first");
    }

    [Fact]
    public void Trojan_TwoConfigsInSameInbound_GetDistinctConnectionStrings()
    {
        var builder = new TrojanConnectionStringBuilder();
        var inbound = InboundWith("42", 12345, "trojan");
        var xui = new XuiInboundDto(
            "42", 12345, "trojan",
            "{\"clients\":[{\"password\":\"pwd-alice\"},{\"password\":\"pwd-bob\"}]}",
            "", 0, 0, 0, true
        );
        var backend = BackendWith("t.example.com");

        var aliceUrl = builder.Build(MakeConfig("pwd-alice"), inbound, xui, backend);
        var bobUrl = builder.Build(MakeConfig("pwd-bob"), inbound, xui, backend);

        aliceUrl.Should().Contain("pwd-alice");
        aliceUrl.Should().NotContain("pwd-bob");
        bobUrl.Should().Contain("pwd-bob");
        bobUrl.Should().NotContain("pwd-alice");
    }

    // ─── Shadowsocks ────────────────────────────────────────────────────

    [Fact]
    public void Shadowsocks_ReadsTopLevelMethodAndPassword()
    {
        // SS uses a top-level method+password (single-client). No need to
        // match by ExternalClientId — the builder reads root fields.
        var builder = new ShadowsocksConnectionStringBuilder();
        var inbound = InboundWith("42", 12345, "shadowsocks");
        var xui = new XuiInboundDto(
            "42", 12345, "shadowsocks",
            """{"method":"chacha20-ietf-poly1305","password":"ss-secret"}""",
            "", 0, 0, 0, true
        );

        var url = builder.Build(MakeConfig("anything"), inbound, xui, BackendWith("ss.example.com"));

        url.Should().StartWith("ss://");
        url.Should().Contain("@ss.example.com:12345");
        var b64 = url["ss://".Length..url.IndexOf('@')];
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        decoded.Should().Be("chacha20-ietf-poly1305:ss-secret");
    }

    [Fact]
    public void Shadowsocks_MalformedSettings_StillReturnsWellFormedUri()
    {
        // Diagnostic-surface contract: even if 3xui returns garbage, the URI
        // is parseable. The client app rejects on empty creds.
        var builder = new ShadowsocksConnectionStringBuilder();
        var inbound = InboundWith("42", 12345, "shadowsocks");
        var xui = new XuiInboundDto(
            "42", 12345, "shadowsocks",
            "not-a-json",
            "", 0, 0, 0, true
        );

        var url = builder.Build(MakeConfig("x"), inbound, xui, BackendWith("ss.example.com"));

        url.Should().StartWith("ss://");
        url.Should().Contain("@ss.example.com:12345");
    }

    // ─── Transport / streamSettings (RED → green after StreamSettingsExtractor) ──

    /// <summary>
    /// Main bug case: Trojan + httpupgrade + TLS.
    /// 3xui returns streamSettings with network=httpupgrade; the builder MUST
    /// produce type=httpupgrade (not the old hardcoded type=tcp).
    /// RED until StreamSettingsExtractor is wired into TrojanConnectionStringBuilder.
    /// </summary>
    [Fact]
    public void Trojan_HttpUpgrade_Tls_ProducesCorrectTransportQuery()
    {
        var builder = new TrojanConnectionStringBuilder();
        var inbound = InboundWith("10", 443, "trojan");
        var xui = new XuiInboundDto(
            Id: "10",
            Port: 443,
            Protocol: "trojan",
            Settings: """{"clients":[{"password":"s3cr3t"}]}""",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: """{"network":"httpupgrade","security":"tls","httpupgradeSettings":{"path":"/getoutyou","host":"zov.pipipupu.fun"},"tlsSettings":{"serverName":"zov.pipipupu.fun"}}"""
        );

        var url = builder.Build(MakeConfig("s3cr3t", "Test"), inbound, xui, BackendWith("zov.pipipupu.fun"));

        // Must carry real transport params from streamSettings.
        url.Should().Contain("type=httpupgrade");
        url.Should().Contain("security=tls");
        url.Should().Contain("path=%2Fgetoutyou");
        url.Should().Contain("host=zov.pipipupu.fun");
        url.Should().Contain("sni=zov.pipipupu.fun");
        // Must NOT contain the old hardcoded value.
        url.Should().NotContain("type=tcp");
    }

    /// <summary>
    /// Vless + WebSocket + TLS.
    /// Verifies path/host/sni/fp/alpn are carried from wsSettings and tlsSettings.
    /// RED until VlessConnectionStringBuilder reads StreamSettings.
    /// </summary>
    [Fact]
    public void Vless_Ws_Tls_ProducesCorrectTransportQuery()
    {
        var builder = new VlessConnectionStringBuilder();
        var inbound = InboundWith("20", 443, "vless");
        var xui = new XuiInboundDto(
            Id: "20",
            Port: 443,
            Protocol: "vless",
            Settings: """{"clients":[{"id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"}]}""",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: """{"network":"ws","security":"tls","wsSettings":{"path":"/ray","headers":{"Host":"cdn.example.com"}},"tlsSettings":{"serverName":"cdn.example.com","fingerprint":"chrome","alpn":["h2","http/1.1"]}}"""
        );

        var url = builder.Build(MakeConfig("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "WsTest"), inbound, xui, BackendWith("cdn.example.com"));

        url.Should().Contain("type=ws");
        url.Should().Contain("path=%2Fray");
        url.Should().Contain("host=cdn.example.com");
        url.Should().Contain("sni=cdn.example.com");
        url.Should().Contain("fp=chrome");
        // alpn array ["h2","http/1.1"] → CSV → url-encoded → h2%2Chttp%2F1.1
        url.Should().Contain("alpn=h2%2Chttp%2F1.1");
        url.Should().NotContain("type=tcp");
    }

    /// <summary>
    /// Vless + Reality (nested settings.* field names used by newer 3xui forks).
    /// RED until VlessConnectionStringBuilder reads StreamSettings with reality support.
    /// </summary>
    [Fact]
    public void Vless_Reality_NestedSettings_ProducesCorrectQuery()
    {
        var builder = new VlessConnectionStringBuilder();
        var inbound = InboundWith("21", 443, "vless");
        var xui = new XuiInboundDto(
            Id: "21",
            Port: 443,
            Protocol: "vless",
            Settings: """{"clients":[{"id":"11111111-2222-3333-4444-555555555555"}]}""",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: """{"network":"tcp","security":"reality","realitySettings":{"serverNames":["sni.example.com"],"shortIds":["abcd"],"settings":{"publicKey":"PBK123","spiderX":"/","fingerprint":"chrome"}}}"""
        );

        var url = builder.Build(MakeConfig("11111111-2222-3333-4444-555555555555", "RealityTest"), inbound, xui, BackendWith("sni.example.com"));

        url.Should().Contain("security=reality");
        url.Should().Contain("sni=sni.example.com");
        url.Should().Contain("pbk=PBK123");
        url.Should().Contain("sid=abcd");
        url.Should().Contain("spx=%2F");
        url.Should().Contain("fp=chrome");
    }

    /// <summary>
    /// Vless + Reality with top-level (flat) field names used by older 3xui forks:
    /// serverName (singular), shortId (singular), publicKey, spiderX, fingerprint
    /// all at the root of realitySettings.
    /// RED until VlessConnectionStringBuilder reads StreamSettings with reality support.
    /// </summary>
    [Fact]
    public void Vless_Reality_TopLevelFieldNames_ProducesCorrectQuery()
    {
        var builder = new VlessConnectionStringBuilder();
        var inbound = InboundWith("22", 443, "vless");
        var xui = new XuiInboundDto(
            Id: "22",
            Port: 443,
            Protocol: "vless",
            Settings: """{"clients":[{"id":"aaaabbbb-cccc-dddd-eeee-ffffffffffff"}]}""",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: """{"network":"tcp","security":"reality","realitySettings":{"serverName":"sni.example.com","shortId":"abcd","publicKey":"PBK123","spiderX":"/","fingerprint":"chrome"}}"""
        );

        var url = builder.Build(MakeConfig("aaaabbbb-cccc-dddd-eeee-ffffffffffff", "RealityFlat"), inbound, xui, BackendWith("sni.example.com"));

        url.Should().Contain("security=reality");
        url.Should().Contain("sni=sni.example.com");
        url.Should().Contain("pbk=PBK123");
        url.Should().Contain("sid=abcd");
        url.Should().Contain("spx=%2F");
        url.Should().Contain("fp=chrome");
    }

    /// <summary>
    /// Vless + gRPC (multiMode=true → mode=multi).
    /// RED until VlessConnectionStringBuilder reads StreamSettings.
    /// </summary>
    [Fact]
    public void Vless_Grpc_Multi_ProducesCorrectTransportQuery()
    {
        var builder = new VlessConnectionStringBuilder();
        var inbound = InboundWith("23", 443, "vless");
        var xui = new XuiInboundDto(
            Id: "23",
            Port: 443,
            Protocol: "vless",
            Settings: """{"clients":[{"id":"cccccccc-dddd-eeee-ffff-000000000000"}]}""",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: """{"network":"grpc","security":"tls","grpcSettings":{"serviceName":"GunService","multiMode":true},"tlsSettings":{"serverName":"g.example.com"}}"""
        );

        var url = builder.Build(MakeConfig("cccccccc-dddd-eeee-ffff-000000000000", "GrpcTest"), inbound, xui, BackendWith("g.example.com"));

        url.Should().Contain("type=grpc");
        url.Should().Contain("serviceName=GunService");
        url.Should().Contain("mode=multi");
        url.Should().Contain("sni=g.example.com");
        url.Should().NotContain("type=tcp");
    }

    /// <summary>
    /// Vmess + WebSocket + TLS.
    /// The VMess link is base64-encoded JSON; verify the decoded payload
    /// reflects the transport fields from streamSettings.
    /// RED until VmessConnectionStringBuilder reads StreamSettings.
    /// </summary>
    [Fact]
    public void Vmess_Ws_Tls_Base64PayloadContainsCorrectTransportFields()
    {
        var builder = new VmessConnectionStringBuilder();
        var inbound = InboundWith("30", 443, "vmess");
        var xui = new XuiInboundDto(
            Id: "30",
            Port: 443,
            Protocol: "vmess",
            Settings: """{"clients":[{"id":"vm-uuid-1111","alterId":0}]}""",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: """{"network":"ws","security":"tls","wsSettings":{"path":"/vm","headers":{"Host":"vm.example.com"}},"tlsSettings":{"serverName":"vm.example.com"}}"""
        );

        var url = builder.Build(MakeConfig("vm-uuid-1111", "VmessWs"), inbound, xui, BackendWith("vm.example.com"));

        url.Should().StartWith("vmess://");
        var b64 = url["vmess://".Length..];
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("net").GetString().Should().Be("ws");
        doc.RootElement.GetProperty("path").GetString().Should().Be("/vm");
        doc.RootElement.GetProperty("host").GetString().Should().Be("vm.example.com");
        doc.RootElement.GetProperty("tls").GetString().Should().Be("tls");
        doc.RootElement.GetProperty("sni").GetString().Should().Be("vm.example.com");
    }

    /// <summary>
    /// Trojan + tcp + none: null streamSettings falls back to tcp/none, well-formed URL,
    /// no exception thrown.
    /// RED until TrojanConnectionStringBuilder applies the Default fallback (currently
    /// hardcodes type=tcp&security=tls — the security value is wrong for null streamSettings).
    /// </summary>
    [Fact]
    public void Trojan_NullStreamSettings_FallsBackTo_TcpNone_WellFormed()
    {
        var builder = new TrojanConnectionStringBuilder();
        var inbound = InboundWith("40", 12345, "trojan");
        var xui = new XuiInboundDto(
            Id: "40",
            Port: 12345,
            Protocol: "trojan",
            Settings: """{"clients":[{"password":"fallback-pwd"}]}""",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: null
        );

        var url = builder.Build(MakeConfig("fallback-pwd", "Fallback"), inbound, xui, BackendWith("t.example.com"));

        url.Should().StartWith("trojan://");
        url.Should().Contain("type=tcp");
        url.Should().Contain("security=none");
    }

    /// <summary>
    /// Trojan + garbage streamSettings: malformed JSON → fallback to tcp/none,
    /// no exception thrown.
    /// RED for same reason as null case (hardcoded security=tls, not security=none).
    /// </summary>
    [Fact]
    public void Trojan_GarbageStreamSettings_FallsBackTo_TcpNone_WellFormed()
    {
        var builder = new TrojanConnectionStringBuilder();
        var inbound = InboundWith("41", 12345, "trojan");
        var xui = new XuiInboundDto(
            Id: "41",
            Port: 12345,
            Protocol: "trojan",
            Settings: """{"clients":[{"password":"fallback-pwd2"}]}""",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: "this is not json at all {{{"
        );

        var urlAction = () => builder.Build(MakeConfig("fallback-pwd2", "Garbage"), inbound, xui, BackendWith("t.example.com"));

        // Must not throw.
        urlAction.Should().NotThrow();
        var url = urlAction();
        url.Should().StartWith("trojan://");
        url.Should().Contain("type=tcp");
        url.Should().Contain("security=none");
    }

    // ─── Regression: previously-uncovered transport branches ─────────────

    /// <summary>
    /// Vless + kcp: headerType and seed are carried from kcpSettings.
    /// </summary>
    [Fact]
    public void Vless_Kcp_ProducesHeaderTypeAndSeed()
    {
        var builder = new VlessConnectionStringBuilder();
        var inbound = InboundWith("50", 443, "vless");
        var xui = new XuiInboundDto(
            Id: "50",
            Port: 443,
            Protocol: "vless",
            Settings: """{"clients":[{"id":"kcp-uuid"}]}""",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: """{"network":"kcp","security":"none","kcpSettings":{"header":{"type":"srtp"},"seed":"sd"}}"""
        );

        var url = builder.Build(MakeConfig("kcp-uuid", "Kcp"), inbound, xui, BackendWith("k.example.com"));

        url.Should().Contain("type=kcp");
        url.Should().Contain("headerType=srtp");
        url.Should().Contain("seed=sd");
    }

    /// <summary>
    /// Vless + tcp + http header: path/host come from tcpSettings.header.request.
    /// </summary>
    [Fact]
    public void Vless_Tcp_HttpHeader_ProducesPathAndHost()
    {
        var builder = new VlessConnectionStringBuilder();
        var inbound = InboundWith("51", 443, "vless");
        var xui = new XuiInboundDto(
            Id: "51",
            Port: 443,
            Protocol: "vless",
            Settings: """{"clients":[{"id":"tcp-uuid"}]}""",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: """{"network":"tcp","security":"none","tcpSettings":{"header":{"type":"http","request":{"path":["/p"],"headers":{"Host":["h.example.com"]}}}}}"""
        );

        var url = builder.Build(MakeConfig("tcp-uuid", "TcpHttp"), inbound, xui, BackendWith("h.example.com"));

        url.Should().Contain("type=tcp");
        url.Should().Contain("headerType=http");
        url.Should().Contain("path=%2Fp");
        url.Should().Contain("host=h.example.com");
    }

    /// <summary>
    /// Vless + grpc with multiMode absent → mode=gun (default).
    /// </summary>
    [Fact]
    public void Vless_Grpc_Gun_WhenMultiModeAbsent()
    {
        var builder = new VlessConnectionStringBuilder();
        var inbound = InboundWith("52", 443, "vless");
        var xui = new XuiInboundDto(
            Id: "52",
            Port: 443,
            Protocol: "vless",
            Settings: """{"clients":[{"id":"grpc-uuid"}]}""",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: """{"network":"grpc","grpcSettings":{"serviceName":"svc"}}"""
        );

        var url = builder.Build(MakeConfig("grpc-uuid", "GrpcGun"), inbound, xui, BackendWith("g.example.com"));

        url.Should().Contain("type=grpc");
        url.Should().Contain("serviceName=svc");
        url.Should().Contain("mode=gun");
    }

    /// <summary>
    /// Regression for the GetBool string-coercion fix: 3xui forks may serialize
    /// multiMode as the string "true" — must still resolve mode=multi.
    /// </summary>
    [Fact]
    public void Vless_Grpc_MultiMode_AsString_ProducesMulti()
    {
        var builder = new VlessConnectionStringBuilder();
        var inbound = InboundWith("53", 443, "vless");
        var xui = new XuiInboundDto(
            Id: "53",
            Port: 443,
            Protocol: "vless",
            Settings: """{"clients":[{"id":"grpc-uuid-2"}]}""",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: """{"network":"grpc","grpcSettings":{"serviceName":"svc","multiMode":"true"}}"""
        );

        var url = builder.Build(MakeConfig("grpc-uuid-2", "GrpcMultiStr"), inbound, xui, BackendWith("g.example.com"));

        url.Should().Contain("mode=multi");
    }

    /// <summary>
    /// Reality with BOTH nested settings.publicKey and top-level publicKey present
    /// (different values): the extractor prefers the nested value.
    /// </summary>
    [Fact]
    public void Vless_Reality_NestedTakesPriorityOverTopLevel()
    {
        var builder = new VlessConnectionStringBuilder();
        var inbound = InboundWith("54", 443, "vless");
        var xui = new XuiInboundDto(
            Id: "54",
            Port: 443,
            Protocol: "vless",
            Settings: """{"clients":[{"id":"reality-uuid"}]}""",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: """{"network":"tcp","security":"reality","realitySettings":{"serverNames":["sni.example.com"],"shortIds":["abcd"],"publicKey":"TOP_LEVEL_PBK","settings":{"publicKey":"NESTED_PBK"}}}"""
        );

        var url = builder.Build(MakeConfig("reality-uuid", "RealityPriority"), inbound, xui, BackendWith("sni.example.com"));

        url.Should().Contain("pbk=NESTED_PBK");
        url.Should().NotContain("TOP_LEVEL_PBK");
    }

    /// <summary>
    /// Regression for major #1: vless+reality with a client flow must carry both
    /// encryption=none (literal) and flow=&lt;escaped&gt;.
    /// </summary>
    [Fact]
    public void Vless_Reality_WithClientFlow_ProducesFlowAndEncryptionNone()
    {
        var builder = new VlessConnectionStringBuilder();
        var inbound = InboundWith("55", 443, "vless");
        var xui = new XuiInboundDto(
            Id: "55",
            Port: 443,
            Protocol: "vless",
            Settings: """{"clients":[{"id":"flow-uuid","flow":"xtls-rprx-vision"}]}""",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: """{"network":"tcp","security":"reality","realitySettings":{"serverNames":["sni.example.com"],"shortIds":["abcd"],"settings":{"publicKey":"PBK123"}}}"""
        );

        var url = builder.Build(MakeConfig("flow-uuid", "RealityFlow"), inbound, xui, BackendWith("sni.example.com"));

        url.Should().Contain("encryption=none");
        url.Should().Contain("flow=xtls-rprx-vision");
    }

    /// <summary>
    /// Documents the actual vmess+grpc layout: net=grpc, and both host and path
    /// are set to the gRPC serviceName.
    /// </summary>
    [Fact]
    public void Vmess_Grpc_HostAndPathAreServiceName()
    {
        var builder = new VmessConnectionStringBuilder();
        var inbound = InboundWith("56", 443, "vmess");
        var xui = new XuiInboundDto(
            Id: "56",
            Port: 443,
            Protocol: "vmess",
            Settings: """{"clients":[{"id":"vm-grpc","alterId":0}]}""",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: """{"network":"grpc","security":"none","grpcSettings":{"serviceName":"MyGrpcSvc"}}"""
        );

        var url = builder.Build(MakeConfig("vm-grpc", "VmessGrpc"), inbound, xui, BackendWith("vg.example.com"));

        var json = Encoding.UTF8.GetString(Convert.FromBase64String(url["vmess://".Length..]));
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("net").GetString().Should().Be("grpc");
        doc.RootElement.GetProperty("host").GetString().Should().Be("MyGrpcSvc");
        doc.RootElement.GetProperty("path").GetString().Should().Be("MyGrpcSvc");
    }
    // ══════════════════════════════════════════════════════════════════════
    // REGRESS: externalProxy / CDN override (RED on main → green after fix)
    //
    // These tests reproduce the bug where builders ignore
    // streamSettings.externalProxy[0] and emit the inbound listening port
    // (25578) + hardcoded transport (type=tcp) instead of the public CDN
    // endpoint (port 443, type=httpupgrade, security=tls).
    //
    // RED MECHANISM ON MAIN:
    //   1. COMPILE-TIME RED: XuiInboundDto has no StreamSettings parameter on
    //      main; every test below that passes StreamSettings: named-arg will
    //      fail with CS1739 / "No argument with name 'StreamSettings'".
    //   2. LOGICAL RED (visible once the developer adds the StreamSettings
    //      field): the current builders ignore externalProxy entirely, so
    //      asserted host:port/transport/security will not match.
    //
    // The one test without StreamSettings (Trojan_WithoutExternalProxy_*) is a
    // logical-red guard on the CURRENT main build that does not need the new
    // field: trojan builder on main emits "type=tcp" which this test rejects.
    // ══════════════════════════════════════════════════════════════════════

    // Reproducing JSON (from analysis.md):
    //   inbound port=25578, protocol=trojan,
    //   settings.clients[0].password = "9c042a1b965e416ea516c0b669dd357d"
    //   streamSettings = {
    //     "network":"httpupgrade","security":"none",
    //     "httpupgradeSettings":{"path":"/getoutyou","host":"zov.pipipupu.fun"},
    //     "externalProxy":[{
    //       "forceTls":"tls","dest":"zov.pipipupu.fun","port":443,"remark":""
    //     }]
    //   }
    //   backend.PublicHost = "origin.example.com"  (DIFFERENT from dest to
    //                                               prove host-override comes
    //                                               from dest, not backend)

    private const string TrojanPassword = "9c042a1b965e416ea516c0b669dd357d";

    private const string StreamSettingsHttpupgradeWithExternalProxy =
        """
        {
          "network": "httpupgrade",
          "security": "none",
          "httpupgradeSettings": {
            "path": "/getoutyou",
            "host": "zov.pipipupu.fun"
          },
          "externalProxy": [
            {
              "forceTls": "tls",
              "dest":     "zov.pipipupu.fun",
              "port":     443,
              "remark":   ""
            }
          ]
        }
        """;

    private const string StreamSettingsHttpupgradeWithExternalProxyForceTlsNone =
        """
        {
          "network": "httpupgrade",
          "security": "tls",
          "httpupgradeSettings": {
            "path": "/getoutyou",
            "host": "zov.pipipupu.fun"
          },
          "externalProxy": [
            {
              "forceTls": "none",
              "dest":     "zov.pipipupu.fun",
              "port":     443,
              "remark":   ""
            }
          ]
        }
        """;

    private const string StreamSettingsHttpupgradeWithExternalProxyForceTlsSame =
        """
        {
          "network": "httpupgrade",
          "security": "tls",
          "httpupgradeSettings": {
            "path": "/getoutyou",
            "host": "zov.pipipupu.fun"
          },
          "externalProxy": [
            {
              "forceTls": "same",
              "dest":     "zov.pipipupu.fun",
              "port":     443,
              "remark":   ""
            }
          ]
        }
        """;

    private const string StreamSettingsRealityWithExternalProxySame =
        """
        {
          "network": "httpupgrade",
          "security": "reality",
          "httpupgradeSettings": {
            "path": "/getoutyou",
            "host": "zov.pipipupu.fun"
          },
          "realitySettings": {
            "publicKey": "reality-pub-key",
            "shortIds": ["abcdef1234567890"],
            "serverNames": ["real.example.com"]
          },
          "externalProxy": [
            {
              "forceTls": "same",
              "dest":     "cdn.example.com",
              "port":     443,
              "remark":   ""
            }
          ]
        }
        """;

    private const string StreamSettingsHttpupgradeNoExternalProxy =
        """
        {
          "network": "httpupgrade",
          "security": "tls",
          "httpupgradeSettings": {
            "path": "/getoutyou",
            "host": "zov.pipipupu.fun"
          }
        }
        """;

    private const string StreamSettingsHttpupgradeExternalProxyNoPort =
        """
        {
          "network": "httpupgrade",
          "security": "none",
          "httpupgradeSettings": {
            "path": "/getoutyou",
            "host": "zov.pipipupu.fun"
          },
          "externalProxy": [
            {
              "forceTls": "tls",
              "dest":     "zov.pipipupu.fun",
              "remark":   ""
            }
          ]
        }
        """;

    private static XuiInboundDto TrojanXuiDto(string streamSettings = "") =>
        new(
            Id: "99",
            Port: 25578,
            Protocol: "trojan",
            Settings: "{\"clients\":[{\"password\":\"" + TrojanPassword + "\",\"email\":\"123-deadbeef\"}]}",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: streamSettings
        );

    private static XuiInboundDto VlessXuiDto(string uuid, string streamSettings = "") =>
        new(
            Id: "100",
            Port: 25578,
            Protocol: "vless",
            Settings: "{\"clients\":[{\"id\":\"" + uuid + "\"}]}",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: streamSettings
        );

    private static XuiInboundDto VmessXuiDto(string uuid, string streamSettings = "") =>
        new(
            Id: "101",
            Port: 25578,
            Protocol: "vmess",
            Settings: "{\"clients\":[{\"id\":\"" + uuid + "\",\"alterId\":0}]}",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: streamSettings
        );

    private static XuiInboundDto SsXuiDto(string streamSettings = "") =>
        new(
            Id: "102",
            Port: 25578,
            Protocol: "shadowsocks",
            Settings: """{"method":"chacha20-ietf-poly1305","password":"ss-secret"}""",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: streamSettings
        );

    // ─── 5.1 Main regression (compile-red on main, logical-red after field added) ─

    /// <summary>
    /// PRIMARY REGRESSION (5.1): trojan + externalProxy(forceTls=tls, port=443,
    /// dest=zov.pipipupu.fun, network=httpupgrade) with backend.PublicHost
    /// deliberately different from dest → URL MUST use dest host + port 443 +
    /// type=httpupgrade + security=tls.
    ///
    /// RED on main: COMPILE (StreamSettings parameter does not exist yet).
    /// LOGICAL RED after developer adds the field: builders still emit
    ///   trojan://...@origin.example.com:25578?type=tcp&amp;security=tls
    /// GREEN after full fix.
    /// </summary>
    [Fact]
    public void Trojan_ExternalProxy_ForceTlsTls_UsesDestHostPort443AndHttpupgrade()
    {
        var builder = new TrojanConnectionStringBuilder();
        var inbound = InboundWith("99", 25578, "trojan");
        var xui = TrojanXuiDto(StreamSettingsHttpupgradeWithExternalProxy);
        var config = MakeConfig(TrojanPassword, "My EU");
        // backend.PublicHost is DIFFERENT from externalProxy.dest — proves host
        // comes from dest, not from HostResolver(backend).
        var backend = BackendWith("origin.example.com");

        var url = builder.Build(config, inbound, xui, backend);

        // Host and port MUST come from externalProxy[0].dest + .port.
        url.Should().StartWith($"trojan://{Uri.EscapeDataString(TrojanPassword)}@zov.pipipupu.fun:443?");
        // Transport fields from streamSettings.httpupgradeSettings.
        url.Should().Contain("type=httpupgrade");
        url.Should().Contain("security=tls");
        url.Should().Contain("path=%2Fgetoutyou");
        url.Should().Contain("host=zov.pipipupu.fun");
        // Fragment with encoded config name.
        url.Should().EndWith("#My%20EU");
        // Must NOT expose the inbound listening port or the backend host.
        url.Should().NotContain(":25578");
        url.Should().NotContain("origin.example.com");
    }

    // ─── 5.2 forceTls=none → security=none regardless of streamSettings.security ─

    [Fact]
    public void Trojan_ExternalProxy_ForceTlsNone_SecurityIsNone()
    {
        var builder = new TrojanConnectionStringBuilder();
        var inbound = InboundWith("99", 25578, "trojan");
        // streamSettings.security=tls but externalProxy.forceTls=none
        var xui = TrojanXuiDto(StreamSettingsHttpupgradeWithExternalProxyForceTlsNone);
        var backend = BackendWith("origin.example.com");

        var url = builder.Build(MakeConfig(TrojanPassword), inbound, xui, backend);

        url.Should().Contain("security=none");
        url.Should().NotContain("security=tls");
    }

    // ─── 5.3 forceTls=same + streamSettings.security=tls → security=tls ────────

    [Fact]
    public void Trojan_ExternalProxy_ForceTlsSame_InheritsTlsFromStreamSettings()
    {
        var builder = new TrojanConnectionStringBuilder();
        var inbound = InboundWith("99", 25578, "trojan");
        var xui = TrojanXuiDto(StreamSettingsHttpupgradeWithExternalProxyForceTlsSame);
        var backend = BackendWith("origin.example.com");

        var url = builder.Build(MakeConfig(TrojanPassword), inbound, xui, backend);

        url.Should().Contain("security=tls");
    }

    // ─── 5.4 forceTls=same + reality → security=reality + pbk/sid/sni ──────────

    [Fact]
    public void Trojan_ExternalProxy_ForceTlsSame_Reality_IncludesRealityParams()
    {
        var builder = new TrojanConnectionStringBuilder();
        var inbound = InboundWith("99", 25578, "trojan");
        var xui = TrojanXuiDto(StreamSettingsRealityWithExternalProxySame);
        var backend = BackendWith("origin.example.com");

        var url = builder.Build(MakeConfig(TrojanPassword), inbound, xui, backend);

        url.Should().Contain("security=reality");
        url.Should().Contain("pbk=reality-pub-key");
        url.Should().Contain("sid=abcdef1234567890");
        // SNI from realitySettings.serverNames[0], NOT from externalProxy.dest.
        url.Should().Contain("sni=real.example.com");
        // Host in address from externalProxy.dest.
        url.Should().Contain("@cdn.example.com:443");
    }

    // ─── 5.4b forceTls=tls over a reality inbound → security=tls, reality params dropped ─

    [Fact]
    public void Trojan_ExternalProxy_ForceTlsTls_OverReality_DropsRealityParams()
    {
        // forceTls=tls overrides the inbound's reality security. Since the link
        // security is now "tls" (not "reality"), the reality-only params
        // (pbk/sid) must NOT appear — the CDN terminates plain TLS in front.
        var streamSettings = StreamSettingsRealityWithExternalProxySame.Replace(
            "\"forceTls\": \"same\"",
            "\"forceTls\": \"tls\"",
            StringComparison.Ordinal
        );
        var builder = new TrojanConnectionStringBuilder();
        var inbound = InboundWith("99", 25578, "trojan");
        var xui = TrojanXuiDto(streamSettings);
        var backend = BackendWith("origin.example.com");

        var url = builder.Build(MakeConfig(TrojanPassword), inbound, xui, backend);

        url.Should().Contain("security=tls");
        url.Should().NotContain("security=reality");
        url.Should().NotContain("pbk=");
        url.Should().NotContain("sid=");
        // Endpoint still overridden by externalProxy.
        url.Should().Contain("@cdn.example.com:443");
    }

    // ─── 5.5 externalProxy absent → fallback: inbound.Port + streamSettings.security ─

    [Fact]
    public void Trojan_NoExternalProxy_FallsBackToInboundPortAndStreamSecurity()
    {
        var builder = new TrojanConnectionStringBuilder();
        var inbound = InboundWith("99", 25578, "trojan");
        var xui = TrojanXuiDto(StreamSettingsHttpupgradeNoExternalProxy);
        var backend = BackendWith("vpn.example.com");

        var url = builder.Build(MakeConfig(TrojanPassword), inbound, xui, backend);

        // Port from inbound, host from backend.PublicHost.
        url.Should().Contain("@vpn.example.com:25578");
        // Security from streamSettings.security (tls), transport httpupgrade.
        url.Should().Contain("security=tls");
        url.Should().Contain("type=httpupgrade");
    }

    // ─── 5.6 dest ≠ backend host, port present → host comes from dest ───────────

    [Fact]
    public void Trojan_ExternalProxy_DestDifferentFromBackendHost_UsesDestHost()
    {
        var builder = new TrojanConnectionStringBuilder();
        var inbound = InboundWith("99", 25578, "trojan");
        var xui = TrojanXuiDto(StreamSettingsHttpupgradeWithExternalProxy);
        // backend.PublicHost explicitly different
        var backend = BackendWith("completely-different.example.com");

        var url = builder.Build(MakeConfig(TrojanPassword), inbound, xui, backend);

        url.Should().Contain("@zov.pipipupu.fun:443");
        url.Should().NotContain("completely-different.example.com");
    }

    // ─── 5.7 port absent in externalProxy → use inbound.Port, host from dest ────

    [Fact]
    public void Trojan_ExternalProxy_NoPort_UsesInboundPortWithDestHost()
    {
        var builder = new TrojanConnectionStringBuilder();
        var inbound = InboundWith("99", 25578, "trojan");
        var xui = TrojanXuiDto(StreamSettingsHttpupgradeExternalProxyNoPort);
        var backend = BackendWith("origin.example.com");

        var url = builder.Build(MakeConfig(TrojanPassword), inbound, xui, backend);

        // Host from dest, port falls back to inbound.Port.
        url.Should().Contain("@zov.pipipupu.fun:25578");
        url.Should().NotContain("origin.example.com");
    }

    // ─── 5.8 vless + externalProxy → correct host/port/transport/security ───────

    [Fact]
    public void Vless_ExternalProxy_ForceTlsTls_UsesDestHostPort443AndHttpupgrade()
    {
        const string uuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var builder = new VlessConnectionStringBuilder();
        var inbound = InboundWith("100", 25578, "vless");
        var xui = VlessXuiDto(uuid, StreamSettingsHttpupgradeWithExternalProxy);
        var backend = BackendWith("origin.example.com");

        var url = builder.Build(MakeConfig(uuid, "My EU"), inbound, xui, backend);

        url.Should().StartWith($"vless://{uuid}@zov.pipipupu.fun:443?");
        url.Should().Contain("type=httpupgrade");
        url.Should().Contain("security=tls");
        url.Should().Contain("path=%2Fgetoutyou");
        url.Should().Contain("host=zov.pipipupu.fun");
        url.Should().EndWith("#My%20EU");
        url.Should().NotContain(":25578");
        url.Should().NotContain("origin.example.com");
    }

    // ─── 5.9 vmess + externalProxy → base64 payload has add=dest, port=443 ──────

    [Fact]
    public void Vmess_ExternalProxy_ForceTlsTls_PayloadHasDestAddressAndPort443()
    {
        const string uuid = "bbbbbbbb-cccc-dddd-eeee-ffffffffffff";
        var builder = new VmessConnectionStringBuilder();
        var inbound = InboundWith("101", 25578, "vmess");
        var xui = VmessXuiDto(uuid, StreamSettingsHttpupgradeWithExternalProxy);
        var backend = BackendWith("origin.example.com");

        var url = builder.Build(MakeConfig(uuid, "My EU"), inbound, xui, backend);

        url.Should().StartWith("vmess://");
        var b64 = url["vmess://".Length..];
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Address and port must come from externalProxy[0].
        root.GetProperty("add").GetString().Should().Be("zov.pipipupu.fun");
        root.GetProperty("port").GetString().Should().Be("443");
        // Transport from streamSettings.
        root.GetProperty("net").GetString().Should().Be("httpupgrade");
        root.GetProperty("path").GetString().Should().Be("/getoutyou");
        root.GetProperty("host").GetString().Should().Be("zov.pipipupu.fun");
        // TLS: forceTls=tls → "tls" (not empty string as hardcoded now).
        root.GetProperty("tls").GetString().Should().Be("tls");
    }

    // ─── 5.10a shadowsocks + externalProxy → dest host + port 443 ───────────────

    [Fact]
    public void Shadowsocks_ExternalProxy_UsesDestHostAndPort443()
    {
        var builder = new ShadowsocksConnectionStringBuilder();
        var inbound = InboundWith("102", 25578, "shadowsocks");
        var xui = SsXuiDto(StreamSettingsHttpupgradeWithExternalProxy);
        var backend = BackendWith("origin.example.com");

        var url = builder.Build(MakeConfig("anything", "SS User"), inbound, xui, backend);

        url.Should().StartWith("ss://");
        url.Should().Contain("@zov.pipipupu.fun:443");
        url.Should().NotContain(":25578");
        url.Should().NotContain("origin.example.com");
    }

    // ─── 5.10b shadowsocks without externalProxy → existing behaviour preserved ─

    [Fact]
    public void Shadowsocks_NoExternalProxy_KeedsBackendHostAndInboundPort()
    {
        // Regression-guard: SS without externalProxy MUST keep the old
        // behaviour (backend host + inbound port). StreamSettings="" simulates
        // no stream settings.
        var builder = new ShadowsocksConnectionStringBuilder();
        var inbound = InboundWith("102", 25578, "shadowsocks");
        var xui = SsXuiDto(streamSettings: "");
        var backend = BackendWith("vpn.example.com");

        var url = builder.Build(MakeConfig("anything"), inbound, xui, backend);

        url.Should().Contain("@vpn.example.com:25578");
    }

    // ─── Empty streamSettings → Parse default (type=tcp&security=none) ────────
    // After the fix, a trojan inbound with no streamSettings blob falls back to
    // ParsedStreamSettings.Default: network=tcp, security=none. No externalProxy
    // means host/port come from the backend + inbound listening port. This is
    // the real transport derived from the (empty) streamSettings, NOT a hardcode.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Empty streamSettings → builder emits the Parse() default transport
    /// (type=tcp&amp;security=none) and the backend host + inbound port. This
    /// replaces the old "documents hardcoded type=tcp" test: the value is now
    /// produced by StreamSettingsExtractor.Parse("") rather than a literal.
    /// </summary>
    [Fact]
    public void Trojan_EmptyStreamSettings_UsesParseDefaultTcpNoneAndInboundEndpoint()
    {
        var builder = new TrojanConnectionStringBuilder();
        var inbound = InboundWith("99", 25578, "trojan");
        var xui = TrojanXuiDto(streamSettings: "");
        var backend = BackendWith("origin.example.com");

        var url = builder.Build(MakeConfig(TrojanPassword, "My EU"), inbound, xui, backend);

        // Default transport from Parse("") — tcp + none, no externalProxy.
        url.Should().Contain("type=tcp");
        url.Should().Contain("security=none");
        // No externalProxy → backend host + inbound listening port.
        url.Should().Contain("@origin.example.com:25578");
    }

    // ─── XHTTP transport (splithttp) ────────────────────────────────────

    /// <summary>
    /// Vless + XHTTP + TLS, empty host (the common reverse-proxy shape 3x-ui
    /// itself produces): path/mode carried, host omitted (Append() skips empty
    /// values, same as ws/httpupgrade), sni/fp/alpn from tlsSettings.
    /// </summary>
    [Fact]
    public void Vless_Xhttp_Tls_EmptyHost_ProducesPathAndModeButNoHost()
    {
        var builder = new VlessConnectionStringBuilder();
        var inbound = InboundWith("60", 443, "vless");
        var xui = new XuiInboundDto(
            Id: "60",
            Port: 443,
            Protocol: "vless",
            Settings: """{"clients":[{"id":"61312703-355f-4ed8-b59e-14ca6631ce15"}]}""",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: """{"network":"xhttp","security":"tls","xhttpSettings":{"path":"/videocdn","host":"","mode":"auto"},"tlsSettings":{"serverName":"zov.pipipupu.fun"}}"""
        );

        var url = builder.Build(
            MakeConfig("61312703-355f-4ed8-b59e-14ca6631ce15", "xhttp_strong"),
            inbound,
            xui,
            BackendWith("zov.pipipupu.fun")
        );

        url.Should().Contain("type=xhttp");
        url.Should().Contain("security=tls");
        url.Should().Contain("path=%2Fvideocdn");
        url.Should().Contain("mode=auto");
        url.Should().Contain("sni=zov.pipipupu.fun");
        url.Should().NotContain("host=");
    }

    /// <summary>
    /// XHTTP mode absent in xhttpSettings → defaults to "auto" (3x-ui's own
    /// default), rather than omitting the parameter — real clients expect it.
    /// </summary>
    [Fact]
    public void Vless_Xhttp_ModeAbsent_DefaultsToAuto()
    {
        var builder = new VlessConnectionStringBuilder();
        var inbound = InboundWith("61", 443, "vless");
        var xui = new XuiInboundDto(
            Id: "61",
            Port: 443,
            Protocol: "vless",
            Settings: """{"clients":[{"id":"aaaaaaaa-0000-0000-0000-000000000001"}]}""",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: """{"network":"xhttp","security":"tls","xhttpSettings":{"path":"/x"},"tlsSettings":{}}"""
        );

        var url = builder.Build(
            MakeConfig("aaaaaaaa-0000-0000-0000-000000000001", "XhttpDefaultMode"),
            inbound,
            xui,
            BackendWith("h.example.com")
        );

        url.Should().Contain("mode=auto");
    }

    /// <summary>
    /// XHTTP with a real (non-empty) host set — the builder must still emit it,
    /// same as ws/httpupgrade; omission is specific to the empty-string case.
    /// </summary>
    [Fact]
    public void Vless_Xhttp_NonEmptyHost_IsIncluded()
    {
        var builder = new VlessConnectionStringBuilder();
        var inbound = InboundWith("62", 443, "vless");
        var xui = new XuiInboundDto(
            Id: "62",
            Port: 443,
            Protocol: "vless",
            Settings: """{"clients":[{"id":"aaaaaaaa-0000-0000-0000-000000000002"}]}""",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: """{"network":"xhttp","security":"tls","xhttpSettings":{"path":"/x","host":"cdn.example.com","mode":"packet-up"},"tlsSettings":{}}"""
        );

        var url = builder.Build(
            MakeConfig("aaaaaaaa-0000-0000-0000-000000000002", "XhttpHost"),
            inbound,
            xui,
            BackendWith("h.example.com")
        );

        url.Should().Contain("host=cdn.example.com");
        url.Should().Contain("mode=packet-up");
    }

    /// <summary>Trojan gets the same XHTTP handling as Vless (shared BuildVlessTrojanQuery).</summary>
    [Fact]
    public void Trojan_Xhttp_Tls_ProducesPathAndMode()
    {
        var builder = new TrojanConnectionStringBuilder();
        var inbound = InboundWith("63", 443, "trojan");
        var xui = new XuiInboundDto(
            Id: "63",
            Port: 443,
            Protocol: "trojan",
            Settings: """{"clients":[{"password":"xhttp-pwd"}]}""",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: """{"network":"xhttp","security":"tls","xhttpSettings":{"path":"/videocdn","host":""},"tlsSettings":{}}"""
        );

        var url = builder.Build(MakeConfig("xhttp-pwd", "TrojanXhttp"), inbound, xui, BackendWith("t.example.com"));

        url.Should().Contain("type=xhttp");
        url.Should().Contain("path=%2Fvideocdn");
        url.Should().Contain("mode=auto");
        url.Should().NotContain("host=");
    }

    /// <summary>
    /// VMess doesn't special-case xhttp — it falls through to the generic
    /// host/path branch, so once StreamSettingsExtractor parses xhttpSettings,
    /// VMess gets correct path/host for free.
    /// </summary>
    [Fact]
    public void Vmess_Xhttp_UsesGenericHostAndPath()
    {
        var builder = new VmessConnectionStringBuilder();
        var inbound = InboundWith("64", 443, "vmess");
        var xui = new XuiInboundDto(
            Id: "64",
            Port: 443,
            Protocol: "vmess",
            Settings: """{"clients":[{"id":"vm-xhttp","alterId":0}]}""",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: """{"network":"xhttp","security":"tls","xhttpSettings":{"path":"/vmx","host":"vmx.example.com"},"tlsSettings":{}}"""
        );

        var url = builder.Build(MakeConfig("vm-xhttp", "VmessXhttp"), inbound, xui, BackendWith("vmx.example.com"));

        var json = Encoding.UTF8.GetString(Convert.FromBase64String(url["vmess://".Length..]));
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("net").GetString().Should().Be("xhttp");
        doc.RootElement.GetProperty("path").GetString().Should().Be("/vmx");
        doc.RootElement.GetProperty("host").GetString().Should().Be("vmx.example.com");
    }

    // ─── ForcedFingerprint / ForcedPacketEncoding overrides ─────────────

    /// <summary>
    /// ForcedFingerprint overrides whatever tlsSettings.fingerprint says — most
    /// 3x-ui inbounds leave it blank, and client apps then fall back to
    /// fingerprinting the panel's own TLS stack unless the link sets one.
    /// </summary>
    [Fact]
    public void Vless_ForcedFingerprint_OverridesStreamSettingsFingerprint()
    {
        var builder = new VlessConnectionStringBuilder();
        var inbound = InboundWith("70", 443, "vless");
        var xui = new XuiInboundDto(
            Id: "70",
            Port: 443,
            Protocol: "vless",
            Settings: """{"clients":[{"id":"fp-uuid"}]}""",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: """{"network":"tcp","security":"tls","tlsSettings":{"serverName":"f.example.com","fingerprint":"chrome"}}"""
        );

        var url = builder.Build(
            new XuiConnectionStringRequest(
                ExternalClientId: "fp-uuid",
                Name: "ForcedFp",
                InboundPort: 443,
                PublicHost: "f.example.com",
                BaseUrl: "https://xui.example.com:2053",
                Inbound: xui,
                ForcedFingerprint: "firefox"
            )
        );

        url.Should().Contain("fp=firefox");
        url.Should().NotContain("fp=chrome");
    }

    /// <summary>ForcedFingerprint is ignored for plaintext (security=none) links — no TLS, no fp.</summary>
    [Fact]
    public void Vless_ForcedFingerprint_IgnoredWhenSecurityIsNone()
    {
        var builder = new VlessConnectionStringBuilder();
        var inbound = InboundWith("71", 443, "vless");
        var xui = new XuiInboundDto(
            Id: "71",
            Port: 443,
            Protocol: "vless",
            Settings: """{"clients":[{"id":"nofp-uuid"}]}""",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: """{"network":"tcp","security":"none"}"""
        );

        var url = builder.Build(
            new XuiConnectionStringRequest(
                "nofp-uuid",
                "NoFp",
                443,
                "n.example.com",
                "https://xui.example.com:2053",
                xui,
                ForcedFingerprint: "firefox"
            )
        );

        url.Should().NotContain("fp=");
    }

    /// <summary>ForcedFingerprint on VMess overrides the JSON "fp" field the same way.</summary>
    [Fact]
    public void Vmess_ForcedFingerprint_OverridesJsonFpField()
    {
        var builder = new VmessConnectionStringBuilder();
        var inbound = InboundWith("72", 443, "vmess");
        var xui = new XuiInboundDto(
            Id: "72",
            Port: 443,
            Protocol: "vmess",
            Settings: """{"clients":[{"id":"vm-fp","alterId":0}]}""",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: """{"network":"tcp","security":"tls","tlsSettings":{"fingerprint":"chrome"}}"""
        );

        var url = builder.Build(
            new XuiConnectionStringRequest(
                "vm-fp",
                "VmForcedFp",
                443,
                "vm.example.com",
                "https://xui.example.com:2053",
                xui,
                ForcedFingerprint: "firefox"
            )
        );

        var json = Encoding.UTF8.GetString(Convert.FromBase64String(url["vmess://".Length..]));
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("fp").GetString().Should().Be("firefox");
    }

    /// <summary>ForcedPacketEncoding is opt-in — omitted by default (existing links keep their exact shape).</summary>
    [Fact]
    public void Vless_PacketEncoding_OmittedByDefault()
    {
        var builder = new VlessConnectionStringBuilder();
        var inbound = InboundWith("73", 443, "vless");
        var xui = new XuiInboundDto(
            Id: "73",
            Port: 443,
            Protocol: "vless",
            Settings: """{"clients":[{"id":"pe-uuid"}]}""",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: """{"network":"xhttp","security":"tls","xhttpSettings":{"path":"/p"},"tlsSettings":{}}"""
        );

        var url = builder.Build(MakeConfig("pe-uuid"), inbound, xui, BackendWith("p.example.com"));

        url.Should().NotContain("packetEncoding=");
    }

    /// <summary>ForcedPacketEncoding, when set, is emitted verbatim — this is how xhttp+vless gets packetEncoding=xudp.</summary>
    [Fact]
    public void Vless_Xhttp_ForcedPacketEncoding_IsEmitted()
    {
        var builder = new VlessConnectionStringBuilder();
        var inbound = InboundWith("74", 443, "vless");
        var xui = new XuiInboundDto(
            Id: "74",
            Port: 443,
            Protocol: "vless",
            Settings: """{"clients":[{"id":"pe-uuid-2"}]}""",
            Remark: "",
            Up: 0,
            Down: 0,
            Total: 0,
            Enable: true,
            StreamSettings: """{"network":"xhttp","security":"tls","xhttpSettings":{"path":"/videocdn","host":""},"tlsSettings":{}}"""
        );

        var url = builder.Build(
            new XuiConnectionStringRequest(
                "pe-uuid-2",
                "XudpTest",
                443,
                "p.example.com",
                "https://xui.example.com:2053",
                xui,
                ForcedFingerprint: "firefox",
                ForcedPacketEncoding: "xudp"
            )
        );

        url.Should().Contain("type=xhttp");
        url.Should().Contain("path=%2Fvideocdn");
        url.Should().Contain("mode=auto");
        url.Should().Contain("fp=firefox");
        url.Should().Contain("packetEncoding=xudp");
        url.Should().NotContain("host=");
    }
}
