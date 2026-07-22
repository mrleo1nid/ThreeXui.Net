using System.Net;
using System.Text;
using FluentAssertions;
using ThreeXui;
using Xunit;

namespace ThreeXui.Net.Tests;

/// <summary>
/// Tests for <see cref="IXuiClient.GetInboundClientTrafficAsync"/>.
///
/// <para>
/// This method exists specifically because 3x-ui's single-inbound endpoint
/// (<c>/panel/api/inbounds/get/{id}</c>, used by <see cref="IXuiClient.GetInboundAsync"/>)
/// does not reliably preload <c>clientStats</c> on every panel version —
/// confirmed against 3x-ui v2.8.11 source: <c>InboundService.GetInbound</c>
/// (single) queries with a plain <c>db.Model(...).First(...)</c>, no
/// <c>Preload("ClientStats")</c>, so that endpoint's <c>clientStats</c> is
/// always <c>null</c> even for clients with real traffic. The list endpoint
/// (<c>/panel/api/inbounds/list</c>) always preloads it. A caller building
/// traffic sync on <c>GetInboundAsync</c> alone would silently see zero
/// traffic for every client on that panel version — these tests pin the
/// list-endpoint-based fix.
/// </para>
/// </summary>
public class XuiClientTrafficTests
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

    [Fact]
    public async Task GetInboundClientTraffic_ReturnsEmailUpDown_FromListEndpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        var payload =
            "{\"success\":true,\"msg\":\"\",\"obj\":["
            + "{\"id\":7,\"port\":443,\"protocol\":\"vless\",\"remark\":\"EU\",\"enable\":true,"
            + "\"settings\":\"{}\","
            + "\"clientStats\":["
            + "{\"email\":\"pnv_alice\",\"up\":100,\"down\":200},"
            + "{\"email\":\"pnv_bob\",\"up\":300,\"down\":400}"
            + "]}"
            + "]}";
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith(XuiClient.LoginPath, StringComparison.Ordinal))
                return LoginOk();
            if (req.RequestUri.AbsolutePath.EndsWith("/panel/api/inbounds/list", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, payload);
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var result = await BuildClient(handler).GetInboundClientTrafficAsync("7", ct);

        result.Should().HaveCount(2);
        result.Should().ContainSingle(r => r.Email == "pnv_alice" && r.UpBytes == 100 && r.DownBytes == 200);
        result.Should().ContainSingle(r => r.Email == "pnv_bob" && r.UpBytes == 300 && r.DownBytes == 400);
    }

    [Fact]
    public async Task GetInboundClientTraffic_InboundNotFound_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        var payload =
            "{\"success\":true,\"msg\":\"\",\"obj\":["
            + "{\"id\":7,\"port\":443,\"protocol\":\"vless\",\"remark\":\"EU\",\"enable\":true,"
            + "\"settings\":\"{}\",\"clientStats\":[{\"email\":\"pnv_alice\",\"up\":100,\"down\":200}]}"
            + "]}";
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith(XuiClient.LoginPath, StringComparison.Ordinal))
                return LoginOk();
            if (req.RequestUri.AbsolutePath.EndsWith("/panel/api/inbounds/list", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, payload);
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var result = await BuildClient(handler).GetInboundClientTrafficAsync("999", ct);

        result.Should().BeEmpty();
    }

    /// <summary>
    /// Reproduces the exact 3x-ui v2.8.11 single-inbound-endpoint shape (via
    /// the list endpoint's json, which is what this method actually reads):
    /// <c>clientStats: null</c> for an inbound whose clients genuinely have no
    /// traffic yet — must not throw, just return empty.
    /// </summary>
    [Fact]
    public async Task GetInboundClientTraffic_NullClientStats_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        var payload =
            "{\"success\":true,\"msg\":\"\",\"obj\":["
            + "{\"id\":7,\"port\":443,\"protocol\":\"vless\",\"remark\":\"EU\",\"enable\":true,"
            + "\"settings\":\"{}\",\"clientStats\":null}"
            + "]}";
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith(XuiClient.LoginPath, StringComparison.Ordinal))
                return LoginOk();
            if (req.RequestUri.AbsolutePath.EndsWith("/panel/api/inbounds/list", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, payload);
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var result = await BuildClient(handler).GetInboundClientTrafficAsync("7", ct);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetInboundClientTraffic_SkipsEntriesWithoutEmail()
    {
        var ct = TestContext.Current.CancellationToken;
        var payload =
            "{\"success\":true,\"msg\":\"\",\"obj\":["
            + "{\"id\":7,\"port\":443,\"protocol\":\"vless\",\"remark\":\"EU\",\"enable\":true,"
            + "\"settings\":\"{}\","
            + "\"clientStats\":["
            + "{\"email\":\"\",\"up\":100,\"down\":200},"
            + "{\"up\":300,\"down\":400},"
            + "{\"email\":\"pnv_alice\",\"up\":500,\"down\":600}"
            + "]}"
            + "]}";
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith(XuiClient.LoginPath, StringComparison.Ordinal))
                return LoginOk();
            if (req.RequestUri.AbsolutePath.EndsWith("/panel/api/inbounds/list", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, payload);
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var result = await BuildClient(handler).GetInboundClientTrafficAsync("7", ct);

        result.Should().ContainSingle();
        result[0].Email.Should().Be("pnv_alice");
    }

    /// <summary>
    /// Delegates each request to a caller-supplied responder — same pattern as
    /// <see cref="XuiClientClientCrudTests"/>.
    /// </summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(respond(request));
    }
}
