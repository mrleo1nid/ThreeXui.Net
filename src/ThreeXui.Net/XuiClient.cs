using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ThreeXui.Http;

namespace ThreeXui;

/// <summary>
/// <see cref="IXuiClient"/> implementation talking to the real 3x-ui REST
/// surface. Constructor takes an already-built <c>HttpClient</c> (configure it
/// with the backend's BaseUrl, a <c>CookieContainer</c>, and a TLS policy — see
/// <c>ThreeXui.Http.XuiHttpClientFactory</c>) plus the admin username +
/// password (plaintext password, decrypted upstream).
///
/// <para>
/// The client-side mutations (AddClient / RemoveClient / UpdateClient) go
/// through a Get→mutate <c>settings.clients[]</c>→UpdateInbound fallback because
/// the addClient / delClient endpoints aren't reliably available on the 3x-ui
/// forks targeted (notably x-ui v2.4.11). A per-inbound <see cref="SemaphoreSlim"/>
/// serializes these mutate sequences so two concurrent Create commands on the
/// same inbound can't clobber each other.
/// </para>
///
/// <para>
/// 3x-ui authenticates via cookie sessions. <see cref="SendAsync"/> wraps each
/// CRUD call with lazy login + re-auth on any expired-session response (401,
/// 403, or a 301/302/307/308 redirect back to <c>/login</c> — see
/// <see cref="IndicatesExpiredSession"/>); the login POSTs <c>username</c> /
/// <c>password</c> as form-data to <c>{base}/login</c> and the server replies
/// with a <c>Set-Cookie: 3x-ui=...</c> that the HttpClient's
/// <c>CookieContainer</c> attaches to every subsequent request.
/// </para>
/// </summary>
public sealed class XuiClient : IXuiClient, IDisposable
{
    // The server-status endpoint doubles as the health probe and the version
    // source. Its verb + availability differ across forks: confirmed GET
    // /panel/api/server/status on 3x-ui v2.8.11 through v2.9+/v3.x (checked
    // against MHSanaei/3x-ui source at those tags); some older x-ui/3x-ui
    // builds are reported to have registered it as POST instead — kept as a
    // fallback since it costs nothing when GET already succeeds. The oldest
    // forks (e.g. x-ui v2.4.11) don't expose a /panel/api/server group at all
    // (only /panel/api/inbounds). GetServerInfo and the health check both
    // tolerate every one of those shapes. Also confirmed on v2.8.11: the
    // status response has no top-level "panelVersion" field (added in a
    // later rewrite) — only "xray.version" — so ExtractVersion's fallback
    // chain correctly reports the Xray-core version there, not the panel
    // version; this is the documented degradation, not a bug.
    internal const string StatusPath = "/panel/api/server/status";
    internal const string HealthPath = StatusPath;
    internal const string InboundsListPath = "/panel/api/inbounds/list";
    internal const string InboundGetPathPrefix = "/panel/api/inbounds/get/";
    internal const string InboundUpdatePathPrefix = "/panel/api/inbounds/update/";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Max characters of any classified error message surfaced upstream
    /// (health-check + exception message).
    /// </summary>
    private const int MaxSurfacedMessageLength = 250;

    private const int MaxBodySnapshotBytes = 2048;
    private const int MaxLoggedSnippetLength = 500;

    private static readonly Regex HtmlTagRegex = new(
        "<[^>]+>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    internal const string LoginPath = "/login";

    private readonly HttpClient _http;
    private readonly string _username;
    private readonly string _password;
    private readonly ILogger<XuiClient> _logger;
    private readonly SemaphoreSlim _loginGate = new(1, 1);

    // Session state. A single XuiClient may be cached and shared across
    // concurrent CRUD calls, so every read/write of the session flag has to be
    // safe under contention:
    //   * _loggedIn is volatile only for the cheap lazy fast-path read in
    //     SendAsync — the authoritative check + flip always happens inside
    //     _loginGate so we never burn duplicate logins or race a reset.
    //   * _sessionGeneration is bumped (under the gate) on every successful
    //     login. SendAsync snapshots it before issuing a request; on a 401 it
    //     hands the snapshot to InvalidateAndReLoginAsync, which only drops +
    //     rebuilds the session if no sibling request already refreshed it
    //     (generation unchanged).
    private volatile bool _loggedIn;
    private int _sessionGeneration;

    /// <summary>
    /// Per-inbound mutex registry. Keyed by the inbound's 3x-ui ExternalId.
    /// Lifetime = this client's lifetime. Process-local — multi-instance
    /// deployments need an external lock (e.g. a DB advisory lock).
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _inboundMutexes = new(
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>
    /// Wraps an already-built <paramref name="http"/> for one 3x-ui backend. The
    /// client takes ownership of <paramref name="http"/> for its lifetime:
    /// <see cref="Dispose"/> disposes it along with the internal synchronization
    /// primitives. When registered via <c>AddXuiClient</c> the DI container drives
    /// that disposal; for manual construction wrap the client in a <c>using</c>.
    /// </summary>
    public XuiClient(
        HttpClient http,
        string username,
        string password,
        ILogger<XuiClient>? logger = null
    )
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _username = username ?? throw new ArgumentNullException(nameof(username));
        _password = password ?? throw new ArgumentNullException(nameof(password));
        _logger = logger ?? NullLogger<XuiClient>.Instance;
    }

    private bool _disposed;

    /// <summary>
    /// Disposes the login gate, every per-inbound mutex, and the owned
    /// <c>HttpClient</c>. Idempotent. Not thread-safe against in-flight calls —
    /// dispose only once the client is quiesced (the DI singleton path guarantees
    /// this at container teardown).
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _loginGate.Dispose();
        foreach (var sem in _inboundMutexes.Values)
            sem.Dispose();
        _inboundMutexes.Clear();
        _http.Dispose();
    }

    private async Task<XuiLoginOutcome> TryLoginAsync(CancellationToken cancellationToken)
    {
        await _loginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check inside the gate: a sibling request may have logged in
            // while we were queued on the semaphore.
            if (_loggedIn)
                return new XuiLoginOutcome(true, null);

            return await PerformLoginAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _loginGate.Release();
        }
    }

    /// <summary>
    /// Issues the actual POST <c>/login</c> and, on success, bumps the session
    /// generation + flips <see cref="_loggedIn"/>. <b>Caller must hold
    /// <see cref="_loginGate"/></b> — this method does not acquire it and
    /// mutates session state without further synchronization.
    /// </summary>
    private async Task<XuiLoginOutcome> PerformLoginAsync(CancellationToken cancellationToken)
    {
        HttpResponseMessage? resp = null;
        try
        {
            using var req = BuildRequest(HttpMethod.Post, LoginPath, skipLogin: true);
            req.Content = new FormUrlEncodedContent(
                new[]
                {
                    new KeyValuePair<string, string>("username", _username),
                    new KeyValuePair<string, string>("password", _password),
                }
            );
            resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);

            // Even a 200 response can carry success=false (rate limit / disabled
            // account / HTML login page); ClassifyResponseAsync teases all those
            // apart from a real success.
            var classified = await ClassifyResponseAsync(
                    resp,
                    isLoginEndpoint: true,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (classified is null)
            {
                // Bump the generation before flipping the flag so a concurrent
                // 401-retry that snapshotted the old generation sees a fresh
                // session and skips its own re-login.
                _sessionGeneration++;
                _loggedIn = true;
                return new XuiLoginOutcome(true, null);
            }

            _logger.LogWarning(
                "3xui login failure: status={Status} contentType={ContentType} message={Message}",
                (int)resp.StatusCode,
                resp.Content.Headers.ContentType?.ToString(),
                classified
            );
            return new XuiLoginOutcome(false, classified);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = ClassifyTransportException(ex, _http.Timeout);
            _logger.LogWarning(ex, "3xui login transport failure: {Message}", message);
            return new XuiLoginOutcome(false, message);
        }
        finally
        {
            resp?.Dispose();
        }
    }

    private sealed record LoginEnvelope(bool Success, string? Msg);

    private sealed record XuiLoginOutcome(bool Success, string? ErrorMessage);

    public async Task<XuiHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var loginOutcome = await TryLoginAsync(cancellationToken).ConfigureAwait(false);
            if (!loginOutcome.Success)
            {
                sw.Stop();
                return new XuiHealthCheckResult(
                    Ok: false,
                    ErrorMessage: loginOutcome.ErrorMessage,
                    Latency: sw.Elapsed
                );
            }

            var statusOutcome = await TryHealthProbeAsync(HealthPath, cancellationToken)
                .ConfigureAwait(false);
            if (statusOutcome.Success)
            {
                sw.Stop();
                return new XuiHealthCheckResult(Ok: true, ErrorMessage: null, Latency: sw.Elapsed);
            }

            // 404 (endpoint absent — e.g. x-ui v2.4.11 has no server API) and 405
            // (registered under a different verb on some forks) both mean "this
            // probe path isn't here", so fall back to inbounds/list. Any other
            // failure (401/403/5xx) is a real fault and must surface, not be
            // masked by the fallback.
            if (statusOutcome.StatusCode != 404 && statusOutcome.StatusCode != 405)
            {
                sw.Stop();
                return new XuiHealthCheckResult(
                    Ok: false,
                    ErrorMessage: statusOutcome.ErrorMessage,
                    Latency: sw.Elapsed
                );
            }

            _logger.LogInformation(
                "3xui {HealthPath} returned {Status} — trying fallback probe {FallbackPath}.",
                HealthPath,
                statusOutcome.StatusCode,
                InboundsListPath
            );

            var listOutcome = await TryHealthProbeAsync(InboundsListPath, cancellationToken)
                .ConfigureAwait(false);
            sw.Stop();
            return listOutcome.Success
                ? new XuiHealthCheckResult(Ok: true, ErrorMessage: null, Latency: sw.Elapsed)
                : new XuiHealthCheckResult(
                    Ok: false,
                    ErrorMessage: listOutcome.ErrorMessage,
                    Latency: sw.Elapsed
                );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            var message = ClassifyTransportException(ex, _http.Timeout);
            _logger.LogWarning(
                ex,
                "Unexpected exception during 3xui health-check: {Message}",
                message
            );
            return new XuiHealthCheckResult(Ok: false, ErrorMessage: message, Latency: sw.Elapsed);
        }
    }

    private sealed record HealthProbeOutcome(bool Success, int? StatusCode, string? ErrorMessage);

    private async Task<HealthProbeOutcome> TryHealthProbeAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        try
        {
            using var response = await SendAsync(
                    HttpMethod.Get,
                    path,
                    content: null,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return new HealthProbeOutcome(true, (int)response.StatusCode, null);

            var classified = await ClassifyResponseAsync(
                    response,
                    isLoginEndpoint: false,
                    cancellationToken
                )
                .ConfigureAwait(false);
            _logger.LogWarning(
                "3xui health probe {Path} failure: status={Status} contentType={ContentType} message={Message}",
                path,
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.ToString(),
                classified
            );
            return new HealthProbeOutcome(
                false,
                (int)response.StatusCode,
                classified ?? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = ClassifyTransportException(ex, _http.Timeout);
            _logger.LogWarning(
                ex,
                "3xui health probe {Path} transport failure: {Message}",
                path,
                message
            );
            return new HealthProbeOutcome(false, null, message);
        }
    }

    private static string ClassifyTransportException(Exception ex, TimeSpan? httpTimeout)
    {
        if (ex is TaskCanceledException || ex is OperationCanceledException)
        {
            var seconds =
                httpTimeout is { } t && t > TimeSpan.Zero
                    ? ((int)t.TotalSeconds).ToString()
                    : XuiHttpClientFactory.DefaultTimeoutSeconds.ToString();
            return $"3xui request timed out after {seconds}s.";
        }

        if (ex is HttpRequestException httpEx)
        {
            for (
                var current = httpEx.InnerException;
                current is not null;
                current = current.InnerException
            )
            {
                if (current is SocketException sockEx)
                {
                    return Truncate(
                        $"3xui unreachable ({sockEx.SocketErrorCode}): {sockEx.Message}",
                        MaxSurfacedMessageLength
                    );
                }
                if (current is AuthenticationException authEx)
                {
                    return Truncate(
                        $"3xui TLS handshake failed: {authEx.Message}",
                        MaxSurfacedMessageLength
                    );
                }
            }

            var rawMsg = httpEx.Message ?? string.Empty;
            if (
                rawMsg.Contains("SSL", StringComparison.OrdinalIgnoreCase)
                || rawMsg.Contains("TLS", StringComparison.OrdinalIgnoreCase)
                || rawMsg.Contains("certificate", StringComparison.OrdinalIgnoreCase)
            )
            {
                return Truncate($"3xui TLS handshake failed: {rawMsg}", MaxSurfacedMessageLength);
            }

            return Truncate($"3xui transport error: {rawMsg}", MaxSurfacedMessageLength);
        }

        return Truncate(
            $"3xui health-check failed ({ex.GetType().Name}): {ex.Message}",
            MaxSurfacedMessageLength
        );
    }

    private async Task<string?> ClassifyResponseAsync(
        HttpResponseMessage resp,
        bool isLoginEndpoint,
        CancellationToken cancellationToken
    )
    {
        var contentType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var isHtml = contentType.Contains("html", StringComparison.OrdinalIgnoreCase);
        var status = (int)resp.StatusCode;
        var reason = resp.ReasonPhrase ?? string.Empty;

        string body;
        try
        {
            var raw = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            body = raw.Length > MaxBodySnapshotBytes ? raw[..MaxBodySnapshotBytes] : raw;
        }
        catch
        {
            body = string.Empty;
        }

        if (isHtml || LooksLikeHtml(body))
        {
            if (isLoginEndpoint && resp.IsSuccessStatusCode)
                return $"3xui returned HTML (status {status}) — check Base URL points to the 3xui API root.";
            if (!resp.IsSuccessStatusCode)
            {
                var snippet = StripHtml(body);
                return Truncate(
                    $"3xui server error (HTTP {status} {reason}): {snippet}",
                    MaxSurfacedMessageLength
                );
            }
            return $"3xui returned HTML (status {status}) — check Base URL points to the 3xui API root.";
        }

        LoginEnvelope? envelope = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                envelope = JsonSerializer.Deserialize<LoginEnvelope>(body, JsonOptions);
            }
            catch (JsonException)
            {
                envelope = null;
            }
        }

        if (status == 401 || status == 403)
        {
            var msgFromBody = envelope?.Msg;
            if (!string.IsNullOrWhiteSpace(msgFromBody))
            {
                return Truncate(
                    $"3xui auth failed (HTTP {status} {reason}): {msgFromBody}. Check Login/Password.",
                    MaxSurfacedMessageLength
                );
            }
            return Truncate(
                $"3xui auth failed (HTTP {status} {reason}). Check Login/Password.",
                MaxSurfacedMessageLength
            );
        }

        if (status >= 500)
        {
            var snippet = StripHtml(body);
            return Truncate(
                $"3xui server error (HTTP {status} {reason}): {snippet}",
                MaxSurfacedMessageLength
            );
        }

        if (!resp.IsSuccessStatusCode)
        {
            return Truncate(
                $"3xui rejected request (HTTP {status} {reason}).",
                MaxSurfacedMessageLength
            );
        }

        if (envelope is { Success: false })
        {
            var msg = envelope.Msg;
            if (!string.IsNullOrWhiteSpace(msg))
                return Truncate($"3xui auth rejected: {msg}", MaxSurfacedMessageLength);
            return "3xui auth rejected (no message).";
        }

        if (isLoginEndpoint && envelope is null)
        {
            if (!string.IsNullOrWhiteSpace(body))
                return $"3xui returned unexpected payload (HTTP {status}, non-JSON body).";
        }

        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string StripHtml(string body)
    {
        if (string.IsNullOrEmpty(body))
            return string.Empty;
        var noTags = HtmlTagRegex.Replace(body, " ");
        var collapsed = WhitespaceRegex.Replace(noTags, " ").Trim();
        return collapsed.Length > MaxLoggedSnippetLength
            ? collapsed[..MaxLoggedSnippetLength]
            : collapsed;
    }

    private static bool LooksLikeHtml(string body)
    {
        if (string.IsNullOrEmpty(body))
            return false;
        var trimmed = body.TrimStart();
        return trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        CancellationToken cancellationToken
    )
    {
        // Lazy login on the first call. The volatile read is just a cheap
        // fast-path; TryLoginAsync re-checks under the gate so concurrent callers
        // never duplicate the POST /login.
        if (!_loggedIn)
        {
            var outcome = await TryLoginAsync(cancellationToken).ConfigureAwait(false);
            if (!outcome.Success)
                throw new InvalidOperationException(outcome.ErrorMessage ?? "3xui login failed.");
        }

        // Snapshot the session generation we're about to ride. If the request
        // 401s, this lets InvalidateAndReLoginAsync tell "my session went stale"
        // apart from "a sibling already refreshed the session after I read it" —
        // only the former triggers a real re-login.
        var generationAtSend = Volatile.Read(ref _sessionGeneration);

        var request = BuildRequest(method, path);
        if (content is not null)
            request.Content = content;
        var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!IndicatesExpiredSession(response.StatusCode))
            return response;

        // Session expired — drop it, re-login once (serialized + deduplicated
        // under the gate), retry. If the retry also fails the same way we let
        // the caller see it (likely real credential rotation).
        //
        // Besides the textbook 401, some forks/reverse-proxies answer a stale
        // session with 403 (nginx auth_request denial) or with a 301/302/307/308
        // redirect back to the /login page instead of a JSON 401 body —
        // AllowAutoRedirect is off (see XuiHttpClientFactory) specifically so
        // those redirects surface here as a status code rather than being
        // silently followed.
        response.Dispose();
        await InvalidateAndReLoginAsync(generationAtSend, cancellationToken).ConfigureAwait(false);

        var retry = BuildRequest(method, path);
        if (content is not null)
            retry.Content = content;
        return await _http
            .SendAsync(retry, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// True for any status a 3x-ui backend (or a proxy in front of it) uses to
    /// signal "your session is no longer valid": the standard 401, the 403 some
    /// forks/proxies substitute for it, and the 301/302/307/308 redirects back
    /// to <c>/login</c> that others emit instead of a JSON error body.
    /// </summary>
    private static bool IndicatesExpiredSession(HttpStatusCode status) =>
        status switch
        {
            HttpStatusCode.Unauthorized => true,
            HttpStatusCode.Forbidden => true,
            HttpStatusCode.MovedPermanently => true,
            HttpStatusCode.Found => true,
            HttpStatusCode.RedirectKeepVerb => true, // 307
            (HttpStatusCode)308 => true, // PermanentRedirect — absent from netstandard2.0's enum
            _ => false,
        };

    /// <summary>
    /// Handles an expired-session response (see <see cref="IndicatesExpiredSession"/>)
    /// from <see cref="SendAsync"/> under <see cref="_loginGate"/>: if the session
    /// generation hasn't already advanced past <paramref name="staleGeneration"/>
    /// (i.e. no sibling request re-logged in since we issued our request), drop
    /// the cached session and log in once. Serializing this through the same
    /// gate as <see cref="TryLoginAsync"/> guarantees that N concurrent
    /// expired-session responses collapse into a single re-login and that a
    /// stale reset can never clobber a freshly established session.
    /// </summary>
    private async Task InvalidateAndReLoginAsync(
        int staleGeneration,
        CancellationToken cancellationToken
    )
    {
        await _loginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A sibling already refreshed the session after we sent our request
            // (generation moved on) — ride their session, no reset.
            if (_loggedIn && _sessionGeneration != staleGeneration)
                return;

            // Our session really is the stale one: drop it and re-login. The
            // reset lives inside the gate so the inline login below can't race a
            // concurrent flip, and PerformLoginAsync re-establishes the flag +
            // generation on success.
            _loggedIn = false;

            var outcome = await PerformLoginAsync(cancellationToken).ConfigureAwait(false);
            if (!outcome.Success)
                throw new InvalidOperationException(
                    outcome.ErrorMessage ?? "3xui re-login after 401 failed."
                );
        }
        finally
        {
            _loginGate.Release();
        }
    }

    public async Task<XuiServerInfo> GetServerInfoAsync(CancellationToken cancellationToken)
    {
        // InboundCount is the one universally-available signal: every API-enabled
        // fork exposes /panel/api/inbounds/list (x-ui v2.4.11 through 3x-ui v3.x),
        // whereas the server-status API group is absent on the oldest forks. This
        // throws on transport faults — matching the interface contract.
        var inbounds = await ListInboundsAsync(cancellationToken).ConfigureAwait(false);

        // Version is best-effort across fork shapes; degrades to "unknown" rather
        // than throwing so a status card still renders the inbound count.
        var version = await TryResolveVersionAsync(cancellationToken).ConfigureAwait(false);

        return new XuiServerInfo(Version: version, InboundCount: inbounds.Count);
    }

    /// <summary>
    /// Resolves the panel/xray version tolerantly across fork shapes: tries GET
    /// /panel/api/server/status (confirmed on 3x-ui v2.8.11 through v2.9+/v3.x)
    /// then POST (reported on some older builds), skipping a verb that answers
    /// 404/405. Returns <c>"unknown"</c> when the endpoint is absent (e.g. x-ui
    /// v2.4.11 has no server API) or the version field can't be found — this
    /// includes 3x-ui v2.8.11, whose status response has no top-level
    /// "panelVersion" (only "xray.version"), so the result there is the
    /// Xray-core version, not the panel's own "2.8.11". Never throws for a
    /// version miss.
    /// </summary>
    private async Task<string> TryResolveVersionAsync(CancellationToken cancellationToken)
    {
        foreach (var method in VersionProbeVerbs)
        {
            try
            {
                using var response = await SendAsync(
                        method,
                        StatusPath,
                        content: null,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                // Endpoint not present under this verb — try the next one.
                if (
                    response.StatusCode == HttpStatusCode.NotFound
                    || response.StatusCode == HttpStatusCode.MethodNotAllowed
                )
                    continue;
                if (!response.IsSuccessStatusCode)
                    continue;

                var envelope = await ReadEnvelopeAsync<JsonObject>(response, cancellationToken)
                    .ConfigureAwait(false);
                var version = ExtractVersion(envelope.Obj);
                if (!string.IsNullOrWhiteSpace(version))
                    return version!;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "3xui server-status probe via {Method} failed; version stays unknown.",
                    method
                );
            }
        }

        return "unknown";
    }

    private static readonly HttpMethod[] VersionProbeVerbs = { HttpMethod.Get, HttpMethod.Post };

    /// <summary>
    /// Pulls a version string out of the server-status <c>obj</c>. Prefers the
    /// panel version (most meaningful for a status card), then the nested xray
    /// core version, then a flat top-level <c>version</c> some forks emit.
    /// </summary>
    private static string? ExtractVersion(JsonObject? statusObj)
    {
        if (statusObj is null)
            return null;

        if (ReadString(statusObj, "panelVersion") is { Length: > 0 } panel)
            return panel;
        if (
            statusObj["xray"] is JsonObject xray
            && ReadString(xray, "version") is { Length: > 0 } xrayVersion
        )
            return xrayVersion;
        if (ReadString(statusObj, "version") is { Length: > 0 } flat)
            return flat;
        return null;

        static string? ReadString(JsonObject obj, string name) =>
            obj[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
                ? s
                : null;
    }

    public async Task<IReadOnlyList<XuiInboundSummaryDto>> ListInboundsAsync(
        CancellationToken cancellationToken
    )
    {
        var payloads = await FetchInboundPayloadsAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<XuiInboundSummaryDto>(payloads.Length);
        foreach (var payload in payloads)
        {
            result.Add(
                new XuiInboundSummaryDto(
                    ExternalId: payload.Id?.ToString() ?? string.Empty,
                    Port: payload.Port,
                    Protocol: payload.Protocol ?? string.Empty,
                    Remark: payload.Remark ?? string.Empty,
                    Enable: payload.Enable,
                    SettingsJson: payload.Settings ?? "{}"
                )
            );
        }
        return result;
    }

    /// <summary>
    /// Per-client traffic (email → up/down bytes) for a single inbound, sourced
    /// from GET /panel/api/inbounds/list rather than the single-inbound
    /// /panel/api/inbounds/get/{id} used by <see cref="GetInboundAsync"/>.
    /// Confirmed against 3x-ui v2.8.11 source: <c>InboundService.GetInbound</c>
    /// (single) queries with plain <c>db.Model(...).First(...)</c> — no
    /// <c>.Preload("ClientStats")</c> — so that endpoint's <c>clientStats</c> is
    /// always <c>null</c>, while <c>InboundService.GetInbounds</c> (list) does
    /// preload it (the panel's own UI list view depends on it). A caller that
    /// built client-traffic sync on top of <see cref="GetInboundAsync"/> would
    /// silently see zero traffic for every client on every inbound on that
    /// panel version — this method exists specifically to avoid that trap.
    /// </summary>
    public async Task<IReadOnlyList<XuiClientTrafficInfo>> GetInboundClientTrafficAsync(
        string externalId,
        CancellationToken cancellationToken
    )
    {
        Throw.IfNullOrWhiteSpace(externalId, nameof(externalId));

        var payloads = await FetchInboundPayloadsAsync(cancellationToken).ConfigureAwait(false);

        InboundPayload? match = null;
        foreach (var payload in payloads)
        {
            if (string.Equals(payload.Id?.ToString(), externalId, StringComparison.Ordinal))
            {
                match = payload;
                break;
            }
        }

        if (match?.ClientStats is not { Length: > 0 } stats)
            return Array.Empty<XuiClientTrafficInfo>();

        var result = new List<XuiClientTrafficInfo>(stats.Length);
        foreach (var stat in stats)
        {
            if (stat.Email is not { Length: > 0 } email)
                continue;
            result.Add(new XuiClientTrafficInfo(email, stat.Up, stat.Down));
        }
        return result;
    }

    private async Task<InboundPayload[]> FetchInboundPayloadsAsync(
        CancellationToken cancellationToken
    )
    {
        using var response = await SendAsync(
                HttpMethod.Get,
                InboundsListPath,
                content: null,
                cancellationToken
            )
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var envelope = JsonSerializer.Deserialize<EnvelopeOf<InboundPayload[]>>(raw, JsonOptions);
        if (envelope is null || !envelope.Success)
            throw new InvalidOperationException(
                $"3xui rejected inbounds/list request: {envelope?.Msg ?? "unknown error"}."
            );
        return envelope.Obj ?? Array.Empty<InboundPayload>();
    }

    public async Task<XuiInboundDto?> GetInboundAsync(
        string externalId,
        CancellationToken cancellationToken
    )
    {
        Throw.IfNullOrWhiteSpace(externalId, nameof(externalId));

        using var response = await SendAsync(
                HttpMethod.Get,
                InboundGetPathPrefix + Uri.EscapeDataString(externalId),
                content: null,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        // Read `obj` as a raw JsonObject so we can preserve EVERY field 3xui
        // returns — including unknown fork-specific ones — verbatim. Client-CRUD
        // echoes this back as the full update body (3xui's update/{id} overwrites
        // the whole inbound, not a partial merge).
        var envelope = await ReadEnvelopeAsync<JsonObject>(response, cancellationToken)
            .ConfigureAwait(false);
        if (envelope.Obj is null)
            return null;

        return ToDto(envelope.Obj);
    }

    public async Task<XuiAddClientResult> AddClientAsync(
        string inboundExternalId,
        AddClientRequest request,
        CancellationToken cancellationToken
    )
    {
        Throw.IfNullOrWhiteSpace(inboundExternalId, nameof(inboundExternalId));
        return await WithInboundLockAsync(
                inboundExternalId,
                async ct =>
                {
                    var current =
                        await GetInboundAsync(inboundExternalId, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException(
                            $"3xui inbound {inboundExternalId} not found while adding a client."
                        );

                    var settings = ParseSettings(current.Settings);
                    var protocol = (request.Protocol ?? string.Empty).ToLowerInvariant();
                    var newClientId = AppendClient(settings, protocol, request);
                    await PushSettingsAsync(inboundExternalId, current.RawInboundJson, settings, ct)
                        .ConfigureAwait(false);
                    return new XuiAddClientResult(newClientId);
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task RemoveClientAsync(
        string inboundExternalId,
        string externalClientId,
        string protocol,
        CancellationToken cancellationToken
    )
    {
        Throw.IfNullOrWhiteSpace(inboundExternalId, nameof(inboundExternalId));
        if (string.IsNullOrEmpty(externalClientId))
        {
            // Nothing to remove on the upstream — idempotent.
            return;
        }

        await WithInboundLockAsync(
                inboundExternalId,
                async ct =>
                {
                    var current = await GetInboundAsync(inboundExternalId, ct)
                        .ConfigureAwait(false);
                    if (current is null)
                        return 0; // Inbound gone — idempotent.

                    var settings = ParseSettings(current.Settings);
                    if (
                        !RemoveClient(
                            settings,
                            (protocol ?? string.Empty).ToLowerInvariant(),
                            externalClientId
                        )
                    )
                    {
                        _logger.LogDebug(
                            "RemoveClient: client {ClientId} not found in inbound {InboundId}; treating as already-removed.",
                            externalClientId,
                            inboundExternalId
                        );
                        return 0;
                    }
                    await PushSettingsAsync(inboundExternalId, current.RawInboundJson, settings, ct)
                        .ConfigureAwait(false);
                    return 0;
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task UpdateClientAsync(
        string inboundExternalId,
        string externalClientId,
        string protocol,
        UpdateClientRequest request,
        CancellationToken cancellationToken
    )
    {
        Throw.IfNullOrWhiteSpace(inboundExternalId, nameof(inboundExternalId));
        if (string.IsNullOrEmpty(externalClientId))
            return;

        await WithInboundLockAsync(
                inboundExternalId,
                async ct =>
                {
                    var current = await GetInboundAsync(inboundExternalId, ct)
                        .ConfigureAwait(false);
                    if (current is null)
                        return 0;

                    var settings = ParseSettings(current.Settings);
                    if (
                        !UpdateClient(
                            settings,
                            (protocol ?? string.Empty).ToLowerInvariant(),
                            externalClientId,
                            request
                        )
                    )
                    {
                        _logger.LogDebug(
                            "UpdateClient: client {ClientId} not found in inbound {InboundId}.",
                            externalClientId,
                            inboundExternalId
                        );
                        return 0;
                    }
                    await PushSettingsAsync(inboundExternalId, current.RawInboundJson, settings, ct)
                        .ConfigureAwait(false);
                    return 0;
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async Task<T> WithInboundLockAsync<T>(
        string inboundExternalId,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken
    )
    {
        var sem = _inboundMutexes.GetOrAdd(inboundExternalId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }

    private static JsonObject ParseSettings(string settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
            return new JsonObject();
        var node = JsonNode.Parse(settingsJson);
        return node as JsonObject ?? new JsonObject();
    }

    /// <summary>
    /// Appends a freshly-generated client to <c>settings.clients[]</c> and
    /// returns the <c>ExternalClientId</c> the caller should persist. The
    /// <c>email</c> label is the deterministic <see cref="AddClientRequest.Email"/>
    /// the caller composed. Per-protocol shape:
    /// <list type="bullet">
    ///   <item>vless / vmess — generates a UUID; clients[*].id is the secret.</item>
    ///   <item>trojan — generates a 32-hex-char password; clients[*].password is the secret.</item>
    ///   <item>shadowsocks — returns the caller's <c>email</c> as the match-field;
    ///         refuses the second client (top-level password is the single secret).</item>
    /// </list>
    /// </summary>
    private static string AppendClient(
        JsonObject settings,
        string protocol,
        AddClientRequest request
    )
    {
        var expiryMs = request.ExpiresAt is { } at ? at.ToUnixTimeMilliseconds() : 0L;
        switch (protocol)
        {
            case "vless":
            {
                var id = Guid.NewGuid().ToString();
                AppendClientObject(
                    settings,
                    new JsonObject
                    {
                        ["id"] = id,
                        ["flow"] = string.Empty,
                        ["email"] = request.Email,
                        ["limitIp"] = request.LimitIp,
                        ["enable"] = true,
                        ["expiryTime"] = expiryMs,
                    }
                );
                return id;
            }
            case "vmess":
            {
                var id = Guid.NewGuid().ToString();
                AppendClientObject(
                    settings,
                    new JsonObject
                    {
                        ["id"] = id,
                        ["alterId"] = 0,
                        ["email"] = request.Email,
                        ["limitIp"] = request.LimitIp,
                        ["enable"] = true,
                        ["expiryTime"] = expiryMs,
                    }
                );
                return id;
            }
            case "trojan":
            {
                var password = Guid.NewGuid().ToString("N");
                AppendClientObject(
                    settings,
                    new JsonObject
                    {
                        ["password"] = password,
                        ["email"] = request.Email,
                        ["limitIp"] = request.LimitIp,
                        ["enable"] = true,
                        ["expiryTime"] = expiryMs,
                    }
                );
                return password;
            }
            case "shadowsocks":
            {
                // SS uses a top-level password/method shape (single client). If
                // the inbound is already configured (non-empty top-level
                // password) we treat a second add as unsupported.
                if (
                    settings["password"] is JsonValue existing
                    && !string.IsNullOrEmpty(existing.GetValue<string>())
                )
                {
                    throw new InvalidOperationException(
                        "Shadowsocks inbound already has a client — multi-client SS isn't supported by this 3xui shape."
                    );
                }
                var password = Guid.NewGuid().ToString("N");
                settings["password"] = password;
                if (settings["method"] is null)
                    settings["method"] = "chacha20-ietf-poly1305";
                return request.Email;
            }
            default:
                throw new InvalidOperationException(
                    $"Unsupported protocol for AddClient: '{protocol}'."
                );
        }
    }

    private static void AppendClientObject(JsonObject settings, JsonObject client)
    {
        if (settings["clients"] is not JsonArray clients)
        {
            clients = new JsonArray();
            settings["clients"] = clients;
        }
        clients.Add(client);
    }

    private static bool RemoveClient(JsonObject settings, string protocol, string externalClientId)
    {
        if (protocol == "shadowsocks")
        {
            // SS top-level shape: blank password to "remove the client".
            if (settings["password"] is null)
                return false;
            settings["password"] = string.Empty;
            return true;
        }

        if (settings["clients"] is not JsonArray clients)
            return false;
        var matchField = MatchFieldFor(protocol);
        for (var i = 0; i < clients.Count; i++)
        {
            if (clients[i] is not JsonObject c)
                continue;
            if (
                c[matchField] is JsonValue v
                && v.TryGetValue<string>(out var s)
                && string.Equals(s, externalClientId, StringComparison.Ordinal)
            )
            {
                clients.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    private static bool UpdateClient(
        JsonObject settings,
        string protocol,
        string externalClientId,
        UpdateClientRequest request
    )
    {
        if (protocol == "shadowsocks")
        {
            // SS update is essentially "rotate password / cipher" — out of scope
            // for per-client update; treat as no-op + report failure so caller
            // can log.
            return false;
        }

        if (settings["clients"] is not JsonArray clients)
            return false;
        var matchField = MatchFieldFor(protocol);
        foreach (var node in clients)
        {
            if (node is not JsonObject c)
                continue;
            if (
                c[matchField] is JsonValue v
                && v.TryGetValue<string>(out var s)
                && string.Equals(s, externalClientId, StringComparison.Ordinal)
            )
            {
                if (request.LimitIp is { } limit)
                    c["limitIp"] = limit;
                if (request.ExpiresAt is { } expires)
                    c["expiryTime"] = expires.ToUnixTimeMilliseconds();
                if (request.Enable is { } enable)
                    c["enable"] = enable;
                if (request.Name is { } name)
                    c["email"] = name;
                return true;
            }
        }
        return false;
    }

    private static string MatchFieldFor(string protocol) =>
        protocol switch
        {
            "trojan" => "password",
            "shadowsocks" => "email",
            _ => "id",
        };

    private async Task PushSettingsAsync(
        string inboundExternalId,
        string rawInboundJson,
        JsonObject settings,
        CancellationToken cancellationToken
    )
    {
        // 3xui's update/{id} OVERWRITES the whole inbound row — a partial body
        // would null out port/protocol/streamSettings/sniffing/enable/listen. So
        // we echo the full inbound model (the raw obj from GetInbound) and swap
        // only `settings`. Note: 3xui stores `settings`/`streamSettings`/
        // `sniffing` as STRINGIFIED JSON inside the inbound object — `settings`
        // must therefore be assigned as a JSON string, not a nested object.
        //
        // Fail loud on an empty/unparseable raw model: silently falling back to a
        // settings-only body would re-introduce the very inbound-wiping bug this
        // method exists to prevent. All callers feed RawInboundJson from a fresh
        // GetInboundAsync, so an empty value signals a programming error.
        if (
            string.IsNullOrWhiteSpace(rawInboundJson)
            || JsonNode.Parse(rawInboundJson) is not JsonObject fullInbound
        )
        {
            throw new InvalidOperationException(
                $"Cannot rebuild the update body for inbound {inboundExternalId}: "
                    + "raw inbound JSON is empty or unparseable. Refusing to send a "
                    + "settings-only body that would overwrite the inbound."
            );
        }
        fullInbound["settings"] = settings.ToJsonString(JsonOptions);

        var json = fullInbound.ToJsonString(JsonOptions);

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await SendAsync(
                HttpMethod.Post,
                InboundUpdatePathPrefix + Uri.EscapeDataString(inboundExternalId),
                content,
                cancellationToken
            )
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        // ReadEnvelopeAsync throws on success:false — caught by the caller as a
        // generic transport-style exception.
        await ReadEnvelopeAsync<InboundPayload>(response, cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string path, bool skipLogin = false)
    {
        Uri absolute;
        if (_http.BaseAddress is { } baseAddr)
        {
            var basePart = baseAddr.GetLeftPart(UriPartial.Path).TrimEnd('/');
            var trimmed = path.TrimStart('/');
            absolute = new Uri($"{basePart}/{trimmed}", UriKind.Absolute);
        }
        else
        {
            absolute = new Uri(path, UriKind.RelativeOrAbsolute);
        }

        var req = new HttpRequestMessage(method, absolute);
        req.Headers.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json")
        );
        return req;
    }

    private static async Task<EnvelopeOf<T>> ReadEnvelopeAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken
    )
        where T : class
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
            return new EnvelopeOf<T>(true, string.Empty, null);

        var envelope = JsonSerializer.Deserialize<EnvelopeOf<T>>(raw, JsonOptions);
        if (envelope is null)
            throw new InvalidOperationException("3xui returned an empty JSON envelope.");
        if (!envelope.Success)
            throw new InvalidOperationException(
                $"3xui rejected the request: {envelope.Msg ?? "unknown error"}."
            );
        return envelope;
    }

    private static XuiInboundDto ToDto(JsonObject raw)
    {
        // Keep the verbatim JSON so client-CRUD can echo the full inbound model
        // back to update/{id} with only `settings` swapped.
        var rawJson = raw.ToJsonString(JsonOptions);
        var obj = raw.Deserialize<InboundPayload>(JsonOptions) ?? new InboundPayload();

        DateTime? lastActivityAt = null;
        var stats = obj.ClientStats;
        if (stats is not null && stats.Length > 0)
        {
            long maxLogin = 0;
            for (var i = 0; i < stats.Length; i++)
            {
                var ll = stats[i].LastLogin ?? 0;
                if (ll > maxLogin)
                    maxLogin = ll;
            }
            if (maxLogin > 0)
                lastActivityAt = DateTimeOffset.FromUnixTimeSeconds(maxLogin).UtcDateTime;
        }

        return new XuiInboundDto(
            Id: obj.Id?.ToString() ?? string.Empty,
            Port: obj.Port,
            Protocol: obj.Protocol ?? string.Empty,
            Settings: obj.Settings ?? "{}",
            Remark: obj.Remark ?? string.Empty,
            Up: obj.Up,
            Down: obj.Down,
            Total: obj.Total,
            Enable: obj.Enable,
            RawInboundJson: rawJson,
            LastActivityAt: lastActivityAt,
            StreamSettings: obj.StreamSettings
        );
    }

    private sealed record EnvelopeOf<T>(bool Success, string? Msg, T? Obj)
        where T : class;

    internal sealed class InboundPayload
    {
        [JsonPropertyName("id")]
        public JsonElement? Id { get; init; }

        [JsonPropertyName("port")]
        public int Port { get; init; }

        [JsonPropertyName("protocol")]
        public string? Protocol { get; init; }

        [JsonPropertyName("settings")]
        public string? Settings { get; init; }

        // 3xui stores streamSettings/sniffing as STRINGIFIED JSON inside the
        // inbound object — kept as raw strings, never parsed here.
        [JsonPropertyName("streamSettings")]
        public string? StreamSettings { get; init; }

        [JsonPropertyName("sniffing")]
        public string? Sniffing { get; init; }

        [JsonPropertyName("listen")]
        public string? Listen { get; init; }

        [JsonPropertyName("expiryTime")]
        public long ExpiryTime { get; init; }

        [JsonPropertyName("tag")]
        public string? Tag { get; init; }

        [JsonPropertyName("remark")]
        public string? Remark { get; init; }

        [JsonPropertyName("up")]
        public long Up { get; init; }

        [JsonPropertyName("down")]
        public long Down { get; init; }

        [JsonPropertyName("total")]
        public long Total { get; init; }

        [JsonPropertyName("enable")]
        public bool Enable { get; init; }

        [JsonPropertyName("clientStats")]
        public ClientStatPayload[]? ClientStats { get; init; }
    }

    internal sealed class ClientStatPayload
    {
        [JsonPropertyName("email")]
        public string? Email { get; init; }

        [JsonPropertyName("up")]
        public long Up { get; init; }

        [JsonPropertyName("down")]
        public long Down { get; init; }

        [JsonPropertyName("last_login")]
        public long? LastLogin { get; init; }
    }
}
