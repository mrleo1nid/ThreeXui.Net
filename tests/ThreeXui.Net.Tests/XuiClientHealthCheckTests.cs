using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using ThreeXui;
using Xunit;

namespace ThreeXui.Net.Tests;

/// <summary>
/// Red-phase regression tests for XuiClient health-check error classification.
/// Documented behaviour (see docs in
/// <c>.claude/work/xui-healthcheck-diagnostics/plan.md</c>): every distinct
/// failure mode of <see cref="XuiClient.CheckHealthAsync"/> should surface a
/// short, human-readable, secret-free <see cref="XuiHealthCheckResult.ErrorMessage"/>
/// instead of the legacy
/// <c>"3xui login failed (wrong credentials or unreachable)."</c> catch-all.
///
/// <para>
/// These tests are expected to FAIL against the current implementation —
/// they encode the fix's contract and stay red until <c>XuiClient.cs</c>
/// is rewritten to classify transport/response errors. Do NOT relax the
/// assertions to make them green; the production code is what changes.
/// </para>
/// </summary>
public class XuiClientHealthCheckTests
{
    private const string LegacyCatchAll = "3xui login failed (wrong credentials or unreachable).";

    private static XuiClient BuildClient(StubHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://xui.test") },
            username: "admin",
            password: "test-pass"
        );

    // ─── 1 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TryLogin_401_SurfacesAuthFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = StubHandler.ForLogin(_ => JsonResponse(
            HttpStatusCode.Unauthorized,
            "{\"success\":false,\"msg\":\"bad credentials\"}"
        ));

        var result = await BuildClient(handler).CheckHealthAsync(ct);

        result.Ok.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNull();
        var msg = result.ErrorMessage!;
        (msg.Contains("401", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("auth", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue($"expected '401' or 'auth' marker in '{msg}'");
        msg.Should().Contain("bad credentials");
        msg.Should().NotContain("wrong credentials or unreachable");
    }

    // ─── 2 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TryLogin_200_SuccessFalse_SurfacesMsg()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = StubHandler.ForLogin(_ => JsonResponse(
            HttpStatusCode.OK,
            "{\"success\":false,\"msg\":\"too many attempts\"}"
        ));

        var result = await BuildClient(handler).CheckHealthAsync(ct);

        result.Ok.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNull();
        result.ErrorMessage!.Should().Contain("too many attempts");
        result.ErrorMessage.Should().NotBe(LegacyCatchAll);
    }

    // ─── 3 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TryLogin_HtmlResponse_SignalsBaseUrlIssue()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = StubHandler.ForLogin(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<html><body>Login Page</body></html>",
                    Encoding.UTF8,
                    "text/html"
                ),
            };
            return resp;
        });

        var result = await BuildClient(handler).CheckHealthAsync(ct);

        result.Ok.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNull();
        var msg = result.ErrorMessage!;
        (msg.Contains("HTML", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Base URL", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue($"expected 'HTML' or 'Base URL' marker in '{msg}'");
        msg.Should().NotContain("<html>");
        msg.Should().NotBe(LegacyCatchAll);
    }

    // ─── 4 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TryLogin_HttpRequestException_SignalsTransport()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = StubHandler.ForLogin(_ =>
            throw new HttpRequestException(
                "Connection refused",
                new SocketException((int)SocketError.ConnectionRefused)
            )
        );

        var result = await BuildClient(handler).CheckHealthAsync(ct);

        result.Ok.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNull();
        var msg = result.ErrorMessage!;
        // The legacy implementation either bubbles `ex.Message` (here:
        // "Connection refused") verbatim, or returns the catch-all string —
        // both contain the literal "Connection" / "unreachable". We require
        // a *classification* marker the fix introduces and legacy does not:
        // the SocketErrorCode enum name (PascalCase, no space) OR a
        // dedicated "transport" / "SocketException" tag.
        var hasTransportMarker =
            msg.Contains("ConnectionRefused", StringComparison.Ordinal)
            || msg.Contains("transport", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("SocketException", StringComparison.OrdinalIgnoreCase);
        hasTransportMarker.Should().BeTrue(
            $"expected transport-classification marker (ConnectionRefused / transport / SocketException) in '{msg}'"
        );
        msg.Should().NotBe(LegacyCatchAll);
        // Plain `ex.Message` from the stub is "Connection refused" — the fix
        // wraps that into a structured message; we shouldn't see the raw
        // exception message alone.
        msg.Should().NotBe("Connection refused");
    }

    // ─── 5 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TryLogin_TaskCanceledException_SignalsTimeout()
    {
        // Caller CT not cancelled — a TaskCanceledException from the handler
        // therefore represents the HttpClient timeout, not user cancellation.
        var handler = StubHandler.ForLogin(_ => throw new TaskCanceledException());

        var result = await BuildClient(handler).CheckHealthAsync(CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNull();
        var msg = result.ErrorMessage!;
        (msg.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue($"expected 'timed out' / 'timeout' marker in '{msg}'");
        msg.Should().NotBe(LegacyCatchAll);
    }

    // ─── 6 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TryLogin_Returns500_TruncatesLongBody()
    {
        var ct = TestContext.Current.CancellationToken;
        var longBody = string.Concat(Enumerable.Repeat("X", 5000));
        var handler = StubHandler.ForLogin(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(longBody, Encoding.UTF8, "text/plain"),
            };
            return resp;
        });

        var result = await BuildClient(handler).CheckHealthAsync(ct);

        result.Ok.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNull();
        result.ErrorMessage!.Length.Should().BeLessThanOrEqualTo(300);
        result.ErrorMessage.Should().Contain("500");
        result.ErrorMessage.Should().NotBe(LegacyCatchAll);
    }

    // ─── 7 ────────────────────────────────────────────────────────────────
    [Fact]
    public async Task TryLogin_Response_DoesNotLeakCookie()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = StubHandler.ForLogin(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    "{\"success\":false,\"msg\":\"x\"}",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
            // Set-Cookie should NEVER find its way into the surfaced
            // error message — it carries the session token.
            resp.Headers.TryAddWithoutValidation(
                "Set-Cookie",
                "3x-ui=secret-session-token; Path=/"
            );
            return resp;
        });

        var result = await BuildClient(handler).CheckHealthAsync(ct);

        result.Ok.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNull();
        var msg = result.ErrorMessage!;
        msg.Should().NotContain("Set-Cookie");
        msg.Should().NotContain("3x-ui");
        msg.Should().NotContain("secret-session-token");
        // Also enforce the fix actually surfaces some detail (HTTP 401, the
        // msg body, etc.) — otherwise the legacy catch-all would
        // accidentally satisfy the no-leak assertions.
        msg.Should().NotBe(LegacyCatchAll);
        var hasDetailMarker =
            msg.Contains("401", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("auth", StringComparison.OrdinalIgnoreCase);
        hasDetailMarker.Should().BeTrue(
            $"expected '401' or 'auth' detail in '{msg}'"
        );
    }

    // ─── 8 (optional) ─────────────────────────────────────────────────────
    [Fact]
    public async Task HealthEndpoint_500_SurfacesStatusAndSnippet()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith(XuiClient.LoginPath, StringComparison.Ordinal))
            {
                var ok = JsonResponse(HttpStatusCode.OK, "{\"success\":true,\"msg\":\"\"}");
                ok.Headers.TryAddWithoutValidation(
                    "Set-Cookie",
                    "3x-ui=session; Path=/"
                );
                return ok;
            }
            // /panel/api/server/status — 503 + HTML
            var resp = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(
                    "<html><body>upstream down</body></html>",
                    Encoding.UTF8,
                    "text/html"
                ),
            };
            return resp;
        });

        var result = await BuildClient(handler).CheckHealthAsync(ct);

        result.Ok.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNull();
        var msg = result.ErrorMessage!;
        msg.Should().Contain("503");
        msg.Should().Contain("upstream down");
        msg.Should().NotContain("<html>");
    }

    // ─── 9 (optional) ─────────────────────────────────────────────────────
    [Fact]
    public async Task SendAsync_Throws_InvalidOperation_WithDetailedMessage_OnLoginFail()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = StubHandler.ForLogin(_ => JsonResponse(
            HttpStatusCode.Unauthorized,
            "{\"success\":false,\"msg\":\"unauthorized\"}"
        ));
        var client = BuildClient(handler);

        var act = async () => await client.GetInboundAsync("anything", ct);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        var message = ex.Which.Message;
        var hasMarker =
            message.Contains("401", StringComparison.OrdinalIgnoreCase)
            || message.Contains("auth", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase);
        hasMarker.Should().BeTrue(
            $"expected '401' / 'auth' / 'unauthorized' marker in '{message}'"
        );
        message.Should().NotBe("3xui login failed.");
    }

    // ─── 10 ───────────────────────────────────────────────────────────────
    // Red-phase regression: when /panel/api/server/status returns 404 (fork
    // x-ui v2.4.11 doesn't expose it), the health-check must fall back to
    // GET /panel/api/inbounds/list and report Ok=true on its success.
    [Fact]
    public async Task HealthCheck_StatusEndpoint404_FallsBackToInboundsList_Ok()
    {
        var ct = TestContext.Current.CancellationToken;
        int listHits = 0;
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith(XuiClient.LoginPath, StringComparison.Ordinal))
            {
                var ok = JsonResponse(HttpStatusCode.OK, "{\"success\":true,\"msg\":\"\"}");
                ok.Headers.TryAddWithoutValidation(
                    "Set-Cookie",
                    "3x-ui=session; Path=/"
                );
                return ok;
            }
            if (path.EndsWith("/panel/api/server/status", StringComparison.Ordinal))
            {
                return JsonResponse(
                    HttpStatusCode.NotFound,
                    "{\"success\":false,\"msg\":\"not found\"}"
                );
            }
            if (path.EndsWith("/panel/api/inbounds/list", StringComparison.Ordinal))
            {
                listHits++;
                return JsonResponse(
                    HttpStatusCode.OK,
                    "{\"success\":true,\"msg\":\"\",\"obj\":[]}"
                );
            }
            // Unexpected path — fail loudly.
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent($"unexpected path: {path}"),
            };
        });

        var result = await BuildClient(handler).CheckHealthAsync(ct);

        result.Ok.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        listHits.Should().Be(1, "fallback должен был вызваться один раз");
    }

    // ─── 11 ───────────────────────────────────────────────────────────────
    // Red-phase regression: when status returns 404 and the fallback list
    // probe itself fails with 5xx, we surface the list-probe classification
    // (status + snippet), not the legacy catch-all.
    [Fact]
    public async Task HealthCheck_StatusEndpoint404_AndInboundsList500_ReturnsListClassification()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith(XuiClient.LoginPath, StringComparison.Ordinal))
            {
                var ok = JsonResponse(HttpStatusCode.OK, "{\"success\":true,\"msg\":\"\"}");
                ok.Headers.TryAddWithoutValidation(
                    "Set-Cookie",
                    "3x-ui=session; Path=/"
                );
                return ok;
            }
            if (path.EndsWith("/panel/api/server/status", StringComparison.Ordinal))
            {
                return JsonResponse(
                    HttpStatusCode.NotFound,
                    "{\"success\":false,\"msg\":\"not found\"}"
                );
            }
            if (path.EndsWith("/panel/api/inbounds/list", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent(
                        "upstream broken",
                        Encoding.UTF8,
                        "text/plain"
                    ),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent($"unexpected path: {path}"),
            };
        });

        var result = await BuildClient(handler).CheckHealthAsync(ct);

        result.Ok.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNull();
        var msg = result.ErrorMessage!;
        msg.Should().Contain("500");
        msg.Should()
            .Match<string>(s => s.Contains("upstream broken", StringComparison.Ordinal)
                || s.Contains("server error", StringComparison.OrdinalIgnoreCase),
                "expected the list-probe snippet ('upstream broken') or its classification ('server error') to surface");
        msg.Should().NotBe(LegacyCatchAll);
    }

    // ─── 12 ───────────────────────────────────────────────────────────────
    // Red-phase regression: when status returns 200 OK we must NOT probe the
    // fallback endpoint. The list-probe stub returns 500 specifically so an
    // erroneously triggered fallback would flip Ok to false.
    [Fact]
    public async Task HealthCheck_StatusEndpoint200_DoesNotProbeFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        int listHits = 0;
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith(XuiClient.LoginPath, StringComparison.Ordinal))
            {
                var ok = JsonResponse(HttpStatusCode.OK, "{\"success\":true,\"msg\":\"\"}");
                ok.Headers.TryAddWithoutValidation(
                    "Set-Cookie",
                    "3x-ui=session; Path=/"
                );
                return ok;
            }
            if (path.EndsWith("/panel/api/server/status", StringComparison.Ordinal))
            {
                return JsonResponse(
                    HttpStatusCode.OK,
                    "{\"success\":true,\"msg\":\"\",\"obj\":{}}"
                );
            }
            if (path.EndsWith("/panel/api/inbounds/list", StringComparison.Ordinal))
            {
                listHits++;
                // Intentionally 500 — any accidental fallback call must fail the test.
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent(
                        "fallback should not have been called",
                        Encoding.UTF8,
                        "text/plain"
                    ),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent($"unexpected path: {path}"),
            };
        });

        var result = await BuildClient(handler).CheckHealthAsync(ct);

        result.Ok.Should().BeTrue();
        listHits.Should().Be(0, "при success-status fallback не должен вызываться");
    }

    // ─── 13 ───────────────────────────────────────────────────────────────
    // Red-phase regression: 503 is a real upstream failure, NOT a missing
    // endpoint — fallback must NOT kick in and mask the 5xx. This is the
    // critical regression that proves the fallback policy fires only on 404.
    [Fact]
    public async Task HealthCheck_StatusEndpoint503_DoesNotProbeFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        int listHits = 0;
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith(XuiClient.LoginPath, StringComparison.Ordinal))
            {
                var ok = JsonResponse(HttpStatusCode.OK, "{\"success\":true,\"msg\":\"\"}");
                ok.Headers.TryAddWithoutValidation(
                    "Set-Cookie",
                    "3x-ui=session; Path=/"
                );
                return ok;
            }
            if (path.EndsWith("/panel/api/server/status", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent(
                        "upstream down",
                        Encoding.UTF8,
                        "text/plain"
                    ),
                };
            }
            if (path.EndsWith("/panel/api/inbounds/list", StringComparison.Ordinal))
            {
                listHits++;
                // Intentionally 200 — if fallback erroneously runs we'd flip
                // Ok=true and hide the 503; the listHits assertion catches it.
                return JsonResponse(
                    HttpStatusCode.OK,
                    "{\"success\":true,\"msg\":\"\",\"obj\":[]}"
                );
            }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent($"unexpected path: {path}"),
            };
        });

        var result = await BuildClient(handler).CheckHealthAsync(ct);

        result.Ok.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNull();
        var msg = result.ErrorMessage!;
        msg.Should().Contain("503");
        msg.Should().Contain("upstream down");
        listHits.Should().Be(0, "fallback должен сработать ТОЛЬКО на 404, не на 5xx");
    }

    // ─── 14 ───────────────────────────────────────────────────────────────
    // Some forks register /server/status under a different verb, so a GET yields
    // 405. Like 404, that means "probe path not here" — the fallback to
    // inbounds/list must fire and report the healthy panel.
    [Fact]
    public async Task HealthCheck_StatusEndpoint405_ProbesFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        int listHits = 0;
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith(XuiClient.LoginPath, StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, "{\"success\":true,\"msg\":\"\"}");
            if (path.EndsWith("/panel/api/server/status", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
                {
                    Content = new StringContent("method not allowed", Encoding.UTF8, "text/plain"),
                };
            if (path.EndsWith("/panel/api/inbounds/list", StringComparison.Ordinal))
            {
                listHits++;
                return JsonResponse(HttpStatusCode.OK, "{\"success\":true,\"msg\":\"\",\"obj\":[]}");
            }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent($"unexpected path: {path}"),
            };
        });

        var result = await BuildClient(handler).CheckHealthAsync(ct);

        result.Ok.Should().BeTrue();
        listHits.Should().Be(1, "405 на status-эндпоинте должен запускать fallback на inbounds/list");
    }

    // ─── 15 ───────────────────────────────────────────────────────────────
    // Regression for the "node flaps offline, a manual re-probe fixes it"
    // report: the server-side session silently expires (some forks/reverse
    // proxies answer every panel API call with a plain 404 once the session
    // is gone, instead of the 401/403/redirect SendAsync already retries on
    // its own — see IndicatesExpiredSession). The cached _loggedIn=true fast
    // path must not let that 404 be mistaken for "this fork has no server API"
    // (the genuine x-ui v2.4.11 case the 404-fallback exists for) — it should
    // force one real re-login and retry before giving up.
    [Fact]
    public async Task HealthCheck_SilentSessionExpiry404_ForcesReloginAndRecovers()
    {
        var ct = TestContext.Current.CancellationToken;
        var sessionValid = false;
        var loginHits = 0;

        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith(XuiClient.LoginPath, StringComparison.Ordinal))
            {
                loginHits++;
                sessionValid = true;
                var ok = JsonResponse(HttpStatusCode.OK, "{\"success\":true,\"msg\":\"\"}");
                ok.Headers.TryAddWithoutValidation("Set-Cookie", "3x-ui=session; Path=/");
                return ok;
            }
            if (path.EndsWith("/panel/api/server/status", StringComparison.Ordinal)
                || path.EndsWith("/panel/api/inbounds/list", StringComparison.Ordinal))
            {
                return sessionValid
                    ? JsonResponse(HttpStatusCode.OK, "{\"success\":true,\"msg\":\"\",\"obj\":[]}")
                    : JsonResponse(HttpStatusCode.NotFound, "{\"success\":false,\"msg\":\"not found\"}");
            }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent($"unexpected path: {path}"),
            };
        });

        var client = BuildClient(handler);

        var first = await client.CheckHealthAsync(ct);
        first.Ok.Should().BeTrue();
        loginHits.Should().Be(1);

        // Server-side session silently dies — no 401/403/redirect, just 404s.
        sessionValid = false;

        var second = await client.CheckHealthAsync(ct);

        second.Ok.Should().BeTrue(
            "a forced re-login should restore the session and the retried probe should succeed"
        );
        loginHits.Should().Be(
            2,
            "the cached fast-path must not mask a session that silently died server-side"
        );
    }

    // ─── 16 ───────────────────────────────────────────────────────────────
    // A fresh login immediately followed by 404 on both probe paths means the
    // endpoint genuinely doesn't exist (e.g. old x-ui) — must NOT trigger a
    // second re-login attempt (that would just loop pointlessly).
    [Fact]
    public async Task HealthCheck_FreshLoginThen404Both_DoesNotForceExtraRelogin()
    {
        var ct = TestContext.Current.CancellationToken;
        var loginHits = 0;

        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith(XuiClient.LoginPath, StringComparison.Ordinal))
            {
                loginHits++;
                var ok = JsonResponse(HttpStatusCode.OK, "{\"success\":true,\"msg\":\"\"}");
                ok.Headers.TryAddWithoutValidation("Set-Cookie", "3x-ui=session; Path=/");
                return ok;
            }
            if (path.EndsWith("/panel/api/server/status", StringComparison.Ordinal)
                || path.EndsWith("/panel/api/inbounds/list", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.NotFound, "{\"success\":false,\"msg\":\"not found\"}");
            }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent($"unexpected path: {path}"),
            };
        });

        var result = await BuildClient(handler).CheckHealthAsync(ct);

        result.Ok.Should().BeFalse();
        loginHits.Should().Be(1, "a 404 right after a fresh login is a genuinely-missing endpoint, not a stale session");
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    /// <summary>
    /// Flexible stub that delegates each request to a caller-supplied
    /// <see cref="Func{HttpRequestMessage, HttpResponseMessage}"/>. Exceptions
    /// thrown by the func propagate (mimics network errors). The
    /// <see cref="ForLogin"/> helper short-circuits non-/login paths to a
    /// generic 200 — these tests only care about the /login leg.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public HttpRequestMessage? LastRequest { get; private set; }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        /// <summary>
        /// Returns a handler that uses <paramref name="loginResponder"/> for
        /// the /login leg and a generic 200 envelope for everything else.
        /// All seven primary tests target login behaviour and need only this
        /// shape.
        /// </summary>
        public static StubHandler ForLogin(
            Func<HttpRequestMessage, HttpResponseMessage> loginResponder
        ) =>
            new(req =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith(XuiClient.LoginPath, StringComparison.Ordinal))
                    return loginResponder(req);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"success\":true,\"msg\":\"\",\"obj\":null}",
                        Encoding.UTF8,
                        "application/json"
                    ),
                };
            });

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            LastRequest = request;
            try
            {
                return Task.FromResult(_respond(request));
            }
            catch (Exception ex)
            {
                return Task.FromException<HttpResponseMessage>(ex);
            }
        }
    }
}
