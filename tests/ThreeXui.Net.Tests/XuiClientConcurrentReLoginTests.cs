using System.Net;
using System.Text;
using FluentAssertions;
using ThreeXui;
using Xunit;

namespace ThreeXui.Net.Tests;

/// <summary>
/// Regression for fix #4 (A) — the session flag (<c>_loggedIn</c>) and the
/// 401 re-login path used to be touched outside <c>_loginGate</c>. Under
/// concurrency that allowed two failure modes: (1) duplicate logins when many
/// parallel requests raced the lazy first login, and (2) a stale
/// <c>_loggedIn = false</c> reset clobbering a session a sibling request had
/// just re-established after its own 401.
///
/// <para>
/// These tests drive many concurrent CRUD calls through a single shared
/// <see cref="XuiClient"/> (the production cache shares one instance per
/// backend) and assert the login count stays bounded — the gate must collapse
/// the parallel logins / re-logins into the minimum needed.
/// </para>
/// </summary>
public class XuiClientConcurrentReLoginTests
{
    [Fact]
    public async Task ConcurrentFirstCall_LogsInExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new CountingHandler(unauthorizedUntilReLogin: false);
        var client = new XuiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://xui.test") },
            username: "admin",
            password: "pw"
        );

        // 20 parallel first-time calls — they all race the lazy login.
        var tasks = Enumerable
            .Range(0, 20)
            .Select(_ => client.GetInboundAsync("1", ct))
            .ToArray();
        await Task.WhenAll(tasks);

        // The gate + double-check must funnel them through a single login.
        handler.LoginCount.Should().Be(1);
    }

    [Fact]
    public async Task Concurrent401_ReLogsInExactlyOnce_NoStaleResetClobber()
    {
        var ct = TestContext.Current.CancellationToken;

        // CRUD requests issued before any re-login return 401 so a whole batch
        // of concurrent requests hits the re-login path simultaneously; once a
        // second login (the re-login) has landed, CRUD succeeds — so each
        // task's single retry is guaranteed to pass.
        var handler = new CountingHandler(unauthorizedUntilReLogin: true);
        var client = new XuiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://xui.test") },
            username: "admin",
            password: "pw"
        );

        var tasks = Enumerable
            .Range(0, 20)
            .Select(_ => client.GetInboundAsync("1", ct))
            .ToArray();
        await Task.WhenAll(tasks);

        // Expected logins: 1 initial + exactly 1 re-login shared by the whole
        // 401 batch. Without the gate-guarded generation check this would be
        // 1 + N (one re-login per 401'd request), or a clobbered session
        // forcing further logins on the retries.
        handler.LoginCount.Should().Be(2);
    }

    /// <summary>
    /// Answers every <c>/login</c> POST with a success envelope (counting the
    /// hits). For CRUD requests: when <c>unauthorizedUntilReLogin</c> is set,
    /// returns 401 until a second login (the re-login) has occurred, then a
    /// success envelope — exercising the lazy-login and 401-retry paths
    /// deterministically under concurrency.
    /// </summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly bool _unauthorizedUntilReLogin;
        private int _loginCount;

        public CountingHandler(bool unauthorizedUntilReLogin)
        {
            _unauthorizedUntilReLogin = unauthorizedUntilReLogin;
        }

        public int LoginCount => Volatile.Read(ref _loginCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(XuiClient.LoginPath, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _loginCount);
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"success\":true,\"msg\":\"\"}"));
            }

            // CRUD request. 401 until the re-login (2nd login) has landed.
            if (_unauthorizedUntilReLogin && Volatile.Read(ref _loginCount) < 2)
                return Task.FromResult(Json(HttpStatusCode.Unauthorized, "{\"success\":false}"));

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
