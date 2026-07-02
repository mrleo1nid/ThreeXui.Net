using System.Net;
using System.Text;
using FluentAssertions;
using ThreeXui;
using Xunit;

namespace ThreeXui.Net.Tests;

/// <summary>
/// Regression: some 3x-ui forks / reverse proxies don't answer a stale session
/// with a plain 401 — they return 403 (e.g. an nginx <c>auth_request</c> denial)
/// or a 301/302/307/308 redirect back to <c>/login</c>. <see cref="XuiClient"/>
/// must treat all of those as "session expired" and re-auth exactly like a 401,
/// not just the textbook case.
/// </summary>
public class XuiClientSessionExpiryStatusTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.RedirectKeepVerb)] // 307
    [InlineData((HttpStatusCode)308)] // PermanentRedirect
    public async Task ExpiredSessionStatus_TriggersReLoginAndRetrySucceeds(
        HttpStatusCode staleStatus
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StaleSessionHandler(staleStatus);
        var client = new XuiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://xui.test") },
            username: "admin",
            password: "pw"
        );

        var result = await client.GetInboundAsync("1", ct);

        result.Should().NotBeNull();
        handler.LoginCount.Should().Be(2); // initial lazy login + one re-login
    }

    /// <summary>
    /// Answers every <c>/login</c> POST with a success envelope. The first CRUD
    /// request (issued right after the lazy login) is answered with
    /// <paramref name="staleStatus"/>; every request after the re-login
    /// succeeds — exercising the expired-session retry path deterministically.
    /// </summary>
    private sealed class StaleSessionHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _staleStatus;
        private int _loginCount;

        public StaleSessionHandler(HttpStatusCode staleStatus)
        {
            _staleStatus = staleStatus;
        }

        public int LoginCount => Volatile.Read(ref _loginCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (
                request.RequestUri!.AbsolutePath.EndsWith(
                    XuiClient.LoginPath,
                    StringComparison.Ordinal
                )
            )
            {
                Interlocked.Increment(ref _loginCount);
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"success\":true,\"msg\":\"\"}"));
            }

            if (Volatile.Read(ref _loginCount) < 2)
                return Task.FromResult(new HttpResponseMessage(_staleStatus));

            return Task.FromResult(
                Json(
                    HttpStatusCode.OK,
                    "{\"success\":true,\"msg\":\"\",\"obj\":{\"id\":\"1\",\"port\":1,\"protocol\":\"vless\",\"enable\":true}}"
                )
            );
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
