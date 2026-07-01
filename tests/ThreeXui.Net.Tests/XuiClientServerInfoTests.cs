using System.Net;
using System.Text;
using FluentAssertions;
using ThreeXui;
using Xunit;

namespace ThreeXui.Net.Tests;

/// <summary>
/// Covers <see cref="XuiClient.GetServerInfoAsync"/> across the fork shapes it
/// must tolerate: the inbound count comes from the universally-available
/// <c>/panel/api/inbounds/list</c>, while the version is best-effort from
/// <c>/panel/api/server/status</c> — GET on 3x-ui v2.9+/v3.x, POST on older
/// x-ui/3x-ui, and entirely absent on the oldest forks (x-ui v2.4.11).
/// </summary>
public class XuiClientServerInfoTests
{
    private static XuiClient BuildClient(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://xui.test") },
            username: "admin",
            password: "pw"
        );

    [Fact]
    public async Task GetServerInfo_NewFork_ReturnsPanelVersionAndInboundCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new RoutingHandler(
            statusGet: _ =>
                Json(
                    HttpStatusCode.OK,
                    "{\"success\":true,\"msg\":\"\",\"obj\":{\"panelVersion\":\"3.4.2\",\"xray\":{\"version\":\"25.1.1\"}}}"
                ),
            inboundsList: _ =>
                Json(
                    HttpStatusCode.OK,
                    "{\"success\":true,\"msg\":\"\",\"obj\":[{\"id\":1,\"port\":1},{\"id\":2,\"port\":2}]}"
                )
        );

        var info = await BuildClient(handler).GetServerInfoAsync(ct);

        info.Version.Should().Be("3.4.2");
        info.InboundCount.Should().Be(2);
    }

    [Fact]
    public async Task GetServerInfo_XrayVersionOnly_FallsBackToXrayVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new RoutingHandler(
            // No panelVersion field — the extractor must fall through to xray.version.
            statusGet: _ =>
                Json(
                    HttpStatusCode.OK,
                    "{\"success\":true,\"msg\":\"\",\"obj\":{\"xray\":{\"version\":\"1.8.24\"}}}"
                ),
            inboundsList: _ => Json(HttpStatusCode.OK, "{\"success\":true,\"msg\":\"\",\"obj\":[]}")
        );

        var info = await BuildClient(handler).GetServerInfoAsync(ct);

        info.Version.Should().Be("1.8.24");
        info.InboundCount.Should().Be(0);
    }

    [Fact]
    public async Task GetServerInfo_StatusPostOnly_UsesPostFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new RoutingHandler(
            // GET rejected with 405 (registered as POST on older forks).
            statusGet: _ => Json(HttpStatusCode.MethodNotAllowed, "method not allowed"),
            statusPost: _ =>
                Json(
                    HttpStatusCode.OK,
                    "{\"success\":true,\"msg\":\"\",\"obj\":{\"panelVersion\":\"2.9.4\"}}"
                ),
            inboundsList: _ =>
                Json(HttpStatusCode.OK, "{\"success\":true,\"msg\":\"\",\"obj\":[{\"id\":1}]}")
        );

        var info = await BuildClient(handler).GetServerInfoAsync(ct);

        info.Version.Should().Be("2.9.4");
        info.InboundCount.Should().Be(1);
    }

    [Fact]
    public async Task GetServerInfo_OldFork_NoServerApi_VersionUnknown()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new RoutingHandler(
            // No /panel/api/server group at all (x-ui v2.4.11): both verbs 404.
            statusGet: _ => Json(HttpStatusCode.NotFound, "not found"),
            statusPost: _ => Json(HttpStatusCode.NotFound, "not found"),
            inboundsList: _ =>
                Json(
                    HttpStatusCode.OK,
                    "{\"success\":true,\"msg\":\"\",\"obj\":[{\"id\":1},{\"id\":2},{\"id\":3}]}"
                )
        );

        var info = await BuildClient(handler).GetServerInfoAsync(ct);

        info.Version.Should().Be("unknown");
        info.InboundCount.Should().Be(3);
    }

    [Fact]
    public async Task GetServerInfo_InboundsListTransportError_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new RoutingHandler(
            statusGet: _ => Json(HttpStatusCode.OK, "{\"success\":true,\"obj\":{}}"),
            inboundsList: _ => Json(HttpStatusCode.InternalServerError, "boom")
        );

        var act = () => BuildClient(handler).GetServerInfoAsync(ct);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>
    /// Routes each request by verb + path to a caller-supplied responder. Always
    /// answers <c>/login</c> with a success envelope. A responder left null means
    /// "endpoint absent" → 404.
    /// </summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage>? _statusGet;
        private readonly Func<HttpRequestMessage, HttpResponseMessage>? _statusPost;
        private readonly Func<HttpRequestMessage, HttpResponseMessage>? _inboundsList;

        public RoutingHandler(
            Func<HttpRequestMessage, HttpResponseMessage>? statusGet = null,
            Func<HttpRequestMessage, HttpResponseMessage>? statusPost = null,
            Func<HttpRequestMessage, HttpResponseMessage>? inboundsList = null
        )
        {
            _statusGet = statusGet;
            _statusPost = statusPost;
            _inboundsList = inboundsList;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith(XuiClient.LoginPath, StringComparison.Ordinal))
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"success\":true,\"msg\":\"\"}"));

            if (path.EndsWith("/panel/api/server/status", StringComparison.Ordinal))
            {
                var responder = request.Method == HttpMethod.Get ? _statusGet : _statusPost;
                return Task.FromResult(
                    responder?.Invoke(request) ?? Json(HttpStatusCode.NotFound, "not found")
                );
            }

            if (path.EndsWith("/panel/api/inbounds/list", StringComparison.Ordinal))
                return Task.FromResult(
                    _inboundsList?.Invoke(request) ?? Json(HttpStatusCode.NotFound, "not found")
                );

            return Task.FromResult(Json(HttpStatusCode.InternalServerError, $"unexpected: {path}"));
        }
    }
}
