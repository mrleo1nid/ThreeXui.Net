using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using ThreeXui;
using Xunit;

namespace ThreeXui.Net.Tests;

/// <summary>
/// Tests for the per-inbound client CRUD on <see cref="XuiClient"/>:
/// <see cref="IXuiClient.ListInboundsAsync"/>, <c>AddClientAsync</c>,
/// <c>RemoveClientAsync</c>, <c>UpdateClientAsync</c>. Each uses the local
/// <see cref="StubHandler"/> pattern from <see cref="XuiClientHealthCheckTests"/>.
///
/// <para>
/// The mutate-by-Get→edit-settings→Update flow is fragile — the tests pin:
/// <list type="bullet">
///   <item>Envelope parsing (<c>{success, obj:[...]}</c> with stringified
///         <c>settings</c> JSON).</item>
///   <item>Per-protocol secret generation (vless → UUID, trojan → password).</item>
///   <item>Idempotent RemoveClient on missing client (no-op, no error).</item>
///   <item>Per-inbound mutex: two parallel AddClient on the SAME inbound id
///         are serialized (the second waits); two on DIFFERENT inbound ids
///         run concurrently.</item>
///   <item><b>REGRESSION (Bug 2):</b> update body must be the FULL inbound model
///         (port, protocol, streamSettings, enable, etc.), not just {settings}.
///         The assert'ы marked "REGRESSION" are RED until PushSettingsAsync is
///         fixed (variant B).</item>
/// </list>
/// </para>
/// </summary>
public class XuiClientClientCrudTests
{
    private static XuiClient BuildClient(StubHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://xui.test") },
            username: "admin",
            password: "test-pass"
        );

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage LoginOk()
    {
        var ok = JsonResponse(HttpStatusCode.OK, "{\"success\":true,\"msg\":\"\"}");
        ok.Headers.TryAddWithoutValidation("Set-Cookie", "3x-ui=session; Path=/");
        return ok;
    }

    // ─── Realistic inbound JSON helpers ─────────────────────────────────
    //
    // These reflect a real 3xui inbound: non-zero port, protocol, enable:true,
    // remark, streamSettings (stringified JSON), sniffing (stringified JSON).
    // The old stubs had minimal {id,port,protocol,settings,enable} — that was
    // enough for the (broken) code that only sent `settings` in the update body,
    // but not enough to pin the Bug-2 regression.

    private const string RealStreamSettings =
        "{\\\"network\\\":\\\"tcp\\\",\\\"security\\\":\\\"reality\\\"}";

    private const string RealSniffing =
        "{\\\"enabled\\\":true,\\\"destOverride\\\":[\\\"http\\\",\\\"tls\\\"]}";

    /// <summary>
    /// Returns a realistic 3xui GET /inbounds/get/{id} response JSON with
    /// port=1000, protocol=vless, enable=true, remark, streamSettings and
    /// sniffing as stringified JSON. Settings is an empty clients array.
    /// </summary>
    private static string RealisticInboundJson(int id) =>
        "{\"success\":true,\"msg\":\"\",\"obj\":{"
        + $"\"id\":{id},"
        + "\"port\":1000,"
        + "\"protocol\":\"vless\","
        + "\"remark\":\"EU-prod\","
        + "\"enable\":true,"
        + "\"listen\":\"\","
        + "\"expiryTime\":0,"
        + "\"settings\":\"{\\\"clients\\\":[]}\","
        + $"\"streamSettings\":\"{RealStreamSettings}\","
        + $"\"sniffing\":\"{RealSniffing}\""
        + "}}";

    // ─── ListInboundsAsync ──────────────────────────────────────────────

    [Fact]
    public async Task ListInbounds_ParsesEnvelope_AndReturnsSummaries()
    {
        var ct = TestContext.Current.CancellationToken;
        // 3xui's settings field is itself a STRINGIFIED JSON. The summary returns
        // it raw — the caller is the one that may parse it.
        // We escape internal double-quotes to embed it as a json string.
        var settingsJson = "{\\\"clients\\\":[{\\\"id\\\":\\\"abc\\\"}]}";
        var payload =
            "{\"success\":true,\"msg\":\"\",\"obj\":["
            + "{\"id\":7,\"port\":12345,\"protocol\":\"vless\",\"remark\":\"EU\",\"enable\":true,"
            + "\"settings\":\""
            + settingsJson
            + "\"}"
            + "]}";
        var handler = new StubHandler(req =>
        {
            if (
                req.RequestUri!.AbsolutePath.EndsWith(XuiClient.LoginPath, StringComparison.Ordinal)
            )
                return LoginOk();
            if (
                req.RequestUri.AbsolutePath.EndsWith(
                    "/panel/api/inbounds/list",
                    StringComparison.Ordinal
                )
            )
                return JsonResponse(HttpStatusCode.OK, payload);
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var result = await BuildClient(handler).ListInboundsAsync(ct);

        result.Should().HaveCount(1);
        var row = result[0];
        row.ExternalId.Should().Be("7");
        row.Port.Should().Be(12345);
        row.Protocol.Should().Be("vless");
        row.Enable.Should().BeTrue();
        row.SettingsJson.Should().Contain("\"id\":\"abc\"");
    }

    [Fact]
    public async Task ListInbounds_SuccessFalse_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler(req =>
        {
            if (
                req.RequestUri!.AbsolutePath.EndsWith(XuiClient.LoginPath, StringComparison.Ordinal)
            )
                return LoginOk();
            return JsonResponse(HttpStatusCode.OK, "{\"success\":false,\"msg\":\"banned\"}");
        });

        Func<Task> act = async () => await BuildClient(handler).ListInboundsAsync(ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ─── AddClientAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task AddClient_Vless_GeneratesUuid_AndCallsUpdateInbound()
    {
        var ct = TestContext.Current.CancellationToken;
        string? capturedUpdateBody = null;
        var handler = new StubHandler(async req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith(XuiClient.LoginPath, StringComparison.Ordinal))
                return LoginOk();
            if (path.Contains("/panel/api/inbounds/get/", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, RealisticInboundJson(42));
            if (path.Contains("/panel/api/inbounds/update/", StringComparison.Ordinal))
            {
                capturedUpdateBody = await req.Content!.ReadAsStringAsync(ct);
                return JsonResponse(HttpStatusCode.OK, "{\"success\":true,\"msg\":\"\"}");
            }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        const string deterministicEmail = "alice-vless-3f2b1c9a4d5e4f6a8b0c1d2e3f405162";
        var result = await BuildClient(handler)
            .AddClientAsync(
                "42",
                new AddClientRequest(
                    Name: "alice",
                    Email: deterministicEmail,
                    Protocol: "vless",
                    LimitIp: 2,
                    ExpiresAt: null
                ),
                ct
            );

        Guid.TryParse(result.ExternalClientId, out _).Should().BeTrue();
        capturedUpdateBody.Should().NotBeNull();
        // The update body must carry the deterministic email label, not the raw Name.
        capturedUpdateBody!.Should().Contain(result.ExternalClientId);
        capturedUpdateBody
            .Should()
            .Contain(
                deterministicEmail,
                because: "AppendClient must write request.Email into clients[].email, not the raw Name"
            );

        // ── REGRESSION Bug-2 ───────────────────────────────────────────
        // The update body MUST contain the full inbound model, not just the
        // mutated {settings}. These assertions are RED until PushSettingsAsync
        // is fixed (variant B: full-model echo).
        capturedUpdateBody
            .Should()
            .Contain(
                "1000",
                because: "update body must echo the inbound port (1000), not just settings"
            );
        capturedUpdateBody
            .Should()
            .Contain("vless", because: "update body must echo the inbound protocol");
        capturedUpdateBody
            .Should()
            .Contain(
                "reality",
                because: "update body must echo streamSettings containing 'reality'"
            );
        capturedUpdateBody
            .Should()
            .Contain(
                "\"enable\":true",
                because: "update body must echo enable:true from the original inbound"
            );
    }

    [Fact]
    public async Task AddClient_Trojan_GeneratesPassword_AndStoresAsExternalClientId()
    {
        var ct = TestContext.Current.CancellationToken;
        string? capturedBody = null;
        var handler = new StubHandler(async req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith(XuiClient.LoginPath, StringComparison.Ordinal))
                return LoginOk();
            if (path.Contains("/panel/api/inbounds/get/", StringComparison.Ordinal))
                return JsonResponse(
                    HttpStatusCode.OK,
                    "{\"success\":true,\"msg\":\"\",\"obj\":{"
                        + "\"id\":7,\"port\":443,\"protocol\":\"trojan\","
                        + "\"remark\":\"US-prod\",\"enable\":true,\"listen\":\"\","
                        + "\"settings\":\"{\\\"clients\\\":[]}\","
                        + $"\"streamSettings\":\"{RealStreamSettings}\","
                        + $"\"sniffing\":\"{RealSniffing}\""
                        + "}}"
                );
            if (path.Contains("/panel/api/inbounds/update/", StringComparison.Ordinal))
            {
                capturedBody = await req.Content!.ReadAsStringAsync(ct);
                return JsonResponse(HttpStatusCode.OK, "{\"success\":true,\"msg\":\"\"}");
            }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        const string deterministicEmail = "alice-trojan-aabbccdd11223344aabbccdd11223344";
        var result = await BuildClient(handler)
            .AddClientAsync(
                "7",
                new AddClientRequest(
                    Name: "alice",
                    Email: deterministicEmail,
                    Protocol: "trojan",
                    LimitIp: 2,
                    ExpiresAt: null
                ),
                ct
            );

        // Trojan secret is a 32-char hex string (Guid.NewGuid().ToString("N")).
        result.ExternalClientId.Should().NotBeNullOrEmpty();
        result.ExternalClientId.Length.Should().Be(32);
        capturedBody.Should().NotBeNull();
        capturedBody!.Should().Contain(result.ExternalClientId);
        capturedBody.Should().Contain("password");
        // The deterministic email label — not the raw Name — must appear in the body.
        capturedBody
            .Should()
            .Contain(
                deterministicEmail,
                because: "AppendClient must write request.Email into clients[].email for trojan"
            );

        // ── REGRESSION Bug-2 ───────────────────────────────────────────
        capturedBody
            .Should()
            .Contain("443", because: "update body must echo trojan inbound port (443)");
        capturedBody.Should().Contain("trojan", because: "update body must echo trojan protocol");
        capturedBody.Should().Contain("reality", because: "update body must echo streamSettings");
    }

    // ─── RemoveClientAsync ──────────────────────────────────────────────

    [Fact]
    public async Task RemoveClient_Vless_RemovesMatchingClient()
    {
        var ct = TestContext.Current.CancellationToken;
        string? capturedBody = null;
        int updateHits = 0;
        var handler = new StubHandler(async req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith(XuiClient.LoginPath, StringComparison.Ordinal))
                return LoginOk();
            if (path.Contains("/panel/api/inbounds/get/", StringComparison.Ordinal))
                return JsonResponse(
                    HttpStatusCode.OK,
                    "{\"success\":true,\"msg\":\"\",\"obj\":{"
                        + "\"id\":42,\"port\":1000,\"protocol\":\"vless\","
                        + "\"remark\":\"EU-prod\",\"enable\":true,\"listen\":\"\","
                        + "\"settings\":\"{\\\"clients\\\":[{\\\"id\\\":\\\"keep-me\\\"},{\\\"id\\\":\\\"drop-me\\\"}]}\","
                        + $"\"streamSettings\":\"{RealStreamSettings}\","
                        + $"\"sniffing\":\"{RealSniffing}\""
                        + "}}"
                );
            if (path.Contains("/panel/api/inbounds/update/", StringComparison.Ordinal))
            {
                capturedBody = await req.Content!.ReadAsStringAsync(ct);
                updateHits++;
                return JsonResponse(HttpStatusCode.OK, "{\"success\":true,\"msg\":\"\"}");
            }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        await BuildClient(handler).RemoveClientAsync("42", "drop-me", "vless", ct);

        updateHits.Should().Be(1, "remove must perform an Update on the inbound");
        capturedBody.Should().NotBeNull();
        capturedBody!.Should().Contain("keep-me");
        capturedBody.Should().NotContain("drop-me");

        // ── REGRESSION Bug-2 ───────────────────────────────────────────
        capturedBody
            .Should()
            .Contain(
                "1000",
                because: "update body must echo the inbound port (1000), not just settings"
            );
        capturedBody
            .Should()
            .Contain("vless", because: "update body must echo the inbound protocol");
        capturedBody
            .Should()
            .Contain(
                "reality",
                because: "update body must echo streamSettings containing 'reality'"
            );
        capturedBody
            .Should()
            .Contain("\"enable\":true", because: "update body must echo enable:true");
    }

    [Fact]
    public async Task RemoveClient_NotFound_Idempotent_NoUpstreamUpdate()
    {
        var ct = TestContext.Current.CancellationToken;
        int updateHits = 0;
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith(XuiClient.LoginPath, StringComparison.Ordinal))
                return LoginOk();
            if (path.Contains("/panel/api/inbounds/get/", StringComparison.Ordinal))
                return JsonResponse(
                    HttpStatusCode.OK,
                    "{\"success\":true,\"msg\":\"\",\"obj\":{"
                        + "\"id\":42,\"port\":1000,\"protocol\":\"vless\","
                        + "\"remark\":\"EU-prod\",\"enable\":true,\"listen\":\"\","
                        + "\"settings\":\"{\\\"clients\\\":[{\\\"id\\\":\\\"only-this\\\"}]}\","
                        + $"\"streamSettings\":\"{RealStreamSettings}\","
                        + $"\"sniffing\":\"{RealSniffing}\""
                        + "}}"
                );
            if (path.Contains("/panel/api/inbounds/update/", StringComparison.Ordinal))
            {
                updateHits++;
                return JsonResponse(HttpStatusCode.OK, "{\"success\":true,\"msg\":\"\"}");
            }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        // RemoveClient on a missing id should not throw, and should not fire
        // the Update call (no settings change ⇒ no upstream write).
        await BuildClient(handler).RemoveClientAsync("42", "ghost-client", "vless", ct);

        updateHits.Should().Be(0, "no settings change ⇒ no upstream Update");
    }

    [Fact]
    public async Task RemoveClient_InboundGone_404_Idempotent_NoThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        int updateHits = 0;
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith(XuiClient.LoginPath, StringComparison.Ordinal))
                return LoginOk();
            if (path.Contains("/panel/api/inbounds/get/", StringComparison.Ordinal))
                return JsonResponse(
                    HttpStatusCode.NotFound,
                    "{\"success\":false,\"msg\":\"not found\"}"
                );
            if (path.Contains("/panel/api/inbounds/update/", StringComparison.Ordinal))
            {
                updateHits++;
                return JsonResponse(HttpStatusCode.OK, "{\"success\":true,\"msg\":\"\"}");
            }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var act = async () =>
            await BuildClient(handler).RemoveClientAsync("42", "anything", "vless", ct);

        await act.Should().NotThrowAsync();
        updateHits.Should().Be(0);
    }

    // ─── UpdateClientAsync ──────────────────────────────────────────────

    [Fact]
    public async Task UpdateClient_Vless_SetsLimitIpAndExpiry()
    {
        var ct = TestContext.Current.CancellationToken;
        string? capturedBody = null;
        var handler = new StubHandler(async req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith(XuiClient.LoginPath, StringComparison.Ordinal))
                return LoginOk();
            if (path.Contains("/panel/api/inbounds/get/", StringComparison.Ordinal))
                return JsonResponse(
                    HttpStatusCode.OK,
                    "{\"success\":true,\"msg\":\"\",\"obj\":{"
                        + "\"id\":42,\"port\":1000,\"protocol\":\"vless\","
                        + "\"remark\":\"EU-prod\",\"enable\":true,\"listen\":\"\","
                        + "\"settings\":\"{\\\"clients\\\":[{\\\"id\\\":\\\"my-uuid\\\",\\\"limitIp\\\":2,\\\"expiryTime\\\":0}]}\","
                        + $"\"streamSettings\":\"{RealStreamSettings}\","
                        + $"\"sniffing\":\"{RealSniffing}\""
                        + "}}"
                );
            if (path.Contains("/panel/api/inbounds/update/", StringComparison.Ordinal))
            {
                capturedBody = await req.Content!.ReadAsStringAsync(ct);
                return JsonResponse(HttpStatusCode.OK, "{\"success\":true,\"msg\":\"\"}");
            }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var futureExpiry = DateTimeOffset.UtcNow.AddDays(30);
        await BuildClient(handler)
            .UpdateClientAsync(
                "42",
                "my-uuid",
                "vless",
                new UpdateClientRequest(
                    LimitIp: 5,
                    ExpiresAt: futureExpiry,
                    Enable: null,
                    Name: null
                ),
                ct
            );

        capturedBody.Should().NotBeNull();
        capturedBody!.Should().Contain("limitIp");
        capturedBody.Should().Contain("5");
        capturedBody.Should().Contain(futureExpiry.ToUnixTimeMilliseconds().ToString());

        // ── REGRESSION Bug-2 ───────────────────────────────────────────
        capturedBody
            .Should()
            .Contain(
                "1000",
                because: "update body must echo port (1000) from the original inbound, not just settings"
            );
        capturedBody
            .Should()
            .Contain("vless", because: "update body must echo protocol from the original inbound");
        capturedBody
            .Should()
            .Contain(
                "reality",
                because: "update body must echo streamSettings (contains 'reality')"
            );
        capturedBody
            .Should()
            .Contain(
                "\"enable\":true",
                because: "update body must echo enable:true from the original inbound"
            );
    }

    // ─── GetInbound raw-JSON contract (Bug-2 / variant B) ────────────────
    //
    // After the fix: XuiInboundDto gains RawInboundJson so PushSettingsAsync can
    // echo the full model. This test verifies the NEW contract.
    [Fact]
    public async Task GetInbound_PreservesRawJson_WithStreamSettingsAndSniffing()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler(req =>
        {
            if (
                req.RequestUri!.AbsolutePath.EndsWith(XuiClient.LoginPath, StringComparison.Ordinal)
            )
                return LoginOk();
            if (
                req.RequestUri.AbsolutePath.Contains(
                    "/panel/api/inbounds/get/",
                    StringComparison.Ordinal
                )
            )
                return JsonResponse(
                    HttpStatusCode.OK,
                    "{\"success\":true,\"msg\":\"\",\"obj\":{"
                        + "\"id\":42,\"port\":1000,\"protocol\":\"vless\","
                        + "\"remark\":\"EU-prod\",\"enable\":true,\"listen\":\"\","
                        + "\"settings\":\"{\\\"clients\\\":[]}\","
                        + $"\"streamSettings\":\"{RealStreamSettings}\","
                        + $"\"sniffing\":\"{RealSniffing}\""
                        + "}}"
                );
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var dto = await BuildClient(handler).GetInboundAsync("42", ct);

        dto.Should().NotBeNull();

        dto!
            .RawInboundJson.Should()
            .NotBeNullOrEmpty(because: "GetInboundAsync must preserve the raw 3xui JSON");
        dto.RawInboundJson.Should()
            .Contain("reality", because: "raw JSON must include streamSettings with reality");
        dto.RawInboundJson.Should()
            .Contain("sniffing", because: "raw JSON must include sniffing blob");
        dto.RawInboundJson.Should().Contain("1000", because: "raw JSON must include port value");
    }

    // ─── Per-inbound mutex ──────────────────────────────────────────────

    [Fact]
    public async Task AddClient_SameInbound_ParallelCalls_AreSerialized()
    {
        // Two concurrent AddClient calls on the SAME inbound must NOT run
        // their (Get + mutate + Update) sequence interleaved. We model this by
        // counting "in-flight mutations" inside the stub: if the lock works,
        // the maximum observed concurrency is 1.
        var ct = TestContext.Current.CancellationToken;
        int inFlight = 0;
        int peak = 0;
        var lockObj = new object();
        var handler = new StubHandler(async req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith(XuiClient.LoginPath, StringComparison.Ordinal))
                return LoginOk();
            if (path.Contains("/panel/api/inbounds/get/", StringComparison.Ordinal))
            {
                lock (lockObj)
                {
                    inFlight++;
                    if (inFlight > peak)
                        peak = inFlight;
                }
                // Hold the slot open while caller does mutate+Update.
                await Task.Delay(50, ct);
                return JsonResponse(
                    HttpStatusCode.OK,
                    "{\"success\":true,\"msg\":\"\",\"obj\":{"
                        + "\"id\":42,\"port\":1000,\"protocol\":\"vless\","
                        + "\"remark\":\"EU-prod\",\"enable\":true,\"listen\":\"\","
                        + "\"settings\":\"{\\\"clients\\\":[]}\","
                        + $"\"streamSettings\":\"{RealStreamSettings}\","
                        + $"\"sniffing\":\"{RealSniffing}\""
                        + "}}"
                );
            }
            if (path.Contains("/panel/api/inbounds/update/", StringComparison.Ordinal))
            {
                await Task.Delay(50, ct);
                lock (lockObj)
                {
                    inFlight--;
                }
                return JsonResponse(HttpStatusCode.OK, "{\"success\":true,\"msg\":\"\"}");
            }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        var client = BuildClient(handler);

        var task1 = client.AddClientAsync(
            "42",
            new AddClientRequest("a", "a-vless-00000000000000000000000000000001", "vless", 2, null),
            ct
        );
        var task2 = client.AddClientAsync(
            "42",
            new AddClientRequest("b", "b-vless-00000000000000000000000000000002", "vless", 2, null),
            ct
        );
        await Task.WhenAll(task1, task2);

        peak.Should().Be(1, "per-inbound mutex must serialize Get+Update sequences");
        // Both succeeded with distinct uuids.
        (await task1)
            .ExternalClientId.Should()
            .NotBe((await task2).ExternalClientId);
    }

    [Fact]
    public async Task AddClient_DifferentInbounds_AreNotSerialized()
    {
        // The mutex is per-inbound — concurrent AddClient on DIFFERENT inbound
        // ids should run in parallel. We model this by hitting >=2 in-flight
        // Get calls at once. With the per-inbound mutex held per-key (not
        // global) the test must observe peak >= 2.
        var ct = TestContext.Current.CancellationToken;
        int inFlight = 0;
        int peak = 0;
        var lockObj = new object();
        var perInbound = new ConcurrentDictionary<string, byte>();
        var handler = new StubHandler(async req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith(XuiClient.LoginPath, StringComparison.Ordinal))
                return LoginOk();
            if (path.Contains("/panel/api/inbounds/get/", StringComparison.Ordinal))
            {
                var inboundId = path.Split('/').Last();
                perInbound.TryAdd(inboundId, 1);
                lock (lockObj)
                {
                    inFlight++;
                    if (inFlight > peak)
                        peak = inFlight;
                }
                await Task.Delay(100, ct);
                return JsonResponse(
                    HttpStatusCode.OK,
                    "{\"success\":true,\"msg\":\"\",\"obj\":{"
                        + $"\"id\":{inboundId},\"port\":1000,\"protocol\":\"vless\","
                        + "\"remark\":\"EU-prod\",\"enable\":true,\"listen\":\"\","
                        + "\"settings\":\"{\\\"clients\\\":[]}\","
                        + $"\"streamSettings\":\"{RealStreamSettings}\","
                        + $"\"sniffing\":\"{RealSniffing}\""
                        + "}}"
                );
            }
            if (path.Contains("/panel/api/inbounds/update/", StringComparison.Ordinal))
            {
                await Task.Delay(10, ct);
                lock (lockObj)
                {
                    inFlight--;
                }
                return JsonResponse(HttpStatusCode.OK, "{\"success\":true,\"msg\":\"\"}");
            }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        var client = BuildClient(handler);

        var task1 = client.AddClientAsync(
            "42",
            new AddClientRequest("a", "a-vless-00000000000000000000000000000042", "vless", 2, null),
            ct
        );
        var task2 = client.AddClientAsync(
            "99",
            new AddClientRequest("b", "b-vless-00000000000000000000000000000099", "vless", 2, null),
            ct
        );
        await Task.WhenAll(task1, task2);

        peak.Should()
            .BeGreaterThan(
                1,
                "per-inbound mutex MUST allow parallel work on different inbound ids"
            );
        perInbound.Keys.Should().HaveCount(2);
    }

    /// <summary>
    /// HttpMessageHandler that delegates each request to a caller-supplied
    /// async <c>Func</c>. Mirrors the local pattern in
    /// <c>XuiClientHealthCheckTests</c>.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = req => Task.FromResult(respond(req));
        }

        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        {
            _respond = respond;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return await _respond(request).ConfigureAwait(false);
        }
    }
}
