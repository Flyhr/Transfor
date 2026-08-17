using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Transfor;

// Erise HTTP 客户端（Phase 6.8）：独立 HttpClient（不复用媒体请求链），
// 无认证状态——Bearer 头经委托注入（由 EriseAuthSession 提供内存 Access Token）；
// 每个请求生成同一 requestId 同时发送 X-Trace-Id/X-Request-Id；
// 手动跟随重定向（默认不自动跟随）：同 Origin 保留 Authorization，跨 Origin 一律移除；
// 错误归一化：401 → EriseUnauthorizedException，其余非 0 code → EriseApiException。
internal sealed class EriseClient : IEriseClient
{
    private const int MaxRedirects = 5;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly EriseSettingsStore settings;
    private readonly HttpClient http;
    private readonly Func<string?> accessToken;
    private readonly string userAgent;

    public EriseClient(
        EriseSettingsStore settings,
        HttpClient http,
        Func<string?> accessToken,
        string userAgent)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.accessToken = accessToken ?? throw new ArgumentNullException(nameof(accessToken));
        this.userAgent = string.IsNullOrWhiteSpace(userAgent) ? "Transfor" : userAgent;
    }

    public Task<EriseCaptchaResponse> GetCaptchaAsync(CancellationToken cancellationToken) =>
        SendAndParseAsync<EriseCaptchaResponse>(HttpMethod.Get, "/api/v1/auth/captcha", null, authenticated: false, cancellationToken);

    public Task<EriseAuthTokens> LoginAsync(
        string username,
        string password,
        string captchaId,
        string captchaCode,
        CancellationToken cancellationToken) =>
        SendAndParseAsync<EriseAuthTokens>(
            HttpMethod.Post,
            "/api/v1/auth/login",
            new { username, password, captchaId, captchaCode },
            authenticated: false,
            cancellationToken);

    public Task<EriseAuthTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        SendAndParseAsync<EriseAuthTokens>(
            HttpMethod.Post,
            "/api/v1/auth/refresh",
            new { refreshToken },
            authenticated: false,
            cancellationToken);

    public Task LogoutAsync(string refreshToken, CancellationToken cancellationToken) =>
        SendAndParseAsync<JsonElement>(
            HttpMethod.Post,
            "/api/v1/auth/logout",
            new { refreshToken },
            authenticated: false,
            cancellationToken);

    public Task<EriseUser> GetCurrentUserAsync(CancellationToken cancellationToken) =>
        SendAndParseAsync<EriseUser>(HttpMethod.Get, "/api/v1/users/me", null, authenticated: true, cancellationToken);

    public Task<ErisePageResponse> GetProjectsAsync(long pageNum, long pageSize, CancellationToken cancellationToken) =>
        SendAndParseAsync<ErisePageResponse>(
            HttpMethod.Get,
            $"/api/v1/projects?pageNum={pageNum}&pageSize={pageSize}",
            null,
            authenticated: true,
            cancellationToken);

    private async Task<T> SendAndParseAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        bool authenticated,
        CancellationToken cancellationToken)
    {
        var data = await SendEnvelopeAsync(method, path, body, authenticated, cancellationToken).ConfigureAwait(false);
        return data.Deserialize<T>(JsonOptions)
            ?? throw new EriseApiException(500000, "响应数据格式无效");
    }

    private async Task<JsonElement> SendEnvelopeAsync(
        HttpMethod method,
        string path,
        object? body,
        bool authenticated,
        CancellationToken cancellationToken)
    {
        var origin = ResolveOrigin();
        var request = BuildRequest(method, new Uri(origin, path.TrimStart('/')), body, authenticated);
        using var response = await SendWithRedirectsAsync(request, cancellationToken).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseEnvelope(raw);
    }

    private Uri ResolveOrigin()
    {
        if (!Uri.TryCreate(settings.Current.ServerOrigin, UriKind.Absolute, out var origin))
        {
            throw new InvalidOperationException("未配置服务器地址");
        }
        return origin;
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, Uri uri, object? body, bool authenticated)
    {
        var request = new HttpRequestMessage(method, uri);
        var requestId = Guid.NewGuid().ToString("N");
        request.Headers.TryAddWithoutValidation("X-Trace-Id", requestId);
        request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        if (authenticated && accessToken() is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        return request;
    }

    // 手动跟随重定向：同 Origin 保留 Authorization，跨 Origin 移除；最多 5 跳
    private async Task<HttpResponseMessage> SendWithRedirectsAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var hop = 0; hop < MaxRedirects; hop++)
        {
            var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!IsRedirect(response, out var location) || location is null)
            {
                return response;
            }

            var original = request;
            var redirectUri = new Uri(original.RequestUri!, location);
            var sameOrigin = IsSameOrigin(original.RequestUri!, redirectUri);
            if (!sameOrigin && original.Method != HttpMethod.Get && original.Method != HttpMethod.Head)
            {
                original.Dispose();
                response.Dispose();
                throw new EriseApiException(500000, "跨 Origin 重定向已拒绝");
            }

            request = await BuildRedirectRequestAsync(original, redirectUri, sameOrigin, cancellationToken)
                .ConfigureAwait(false);
            original.Dispose();
            response.Dispose();
        }

        throw new EriseApiException(500000, "重定向次数过多");
    }

    private static async Task<HttpRequestMessage> BuildRedirectRequestAsync(
        HttpRequestMessage original,
        Uri location,
        bool sameOrigin,
        CancellationToken cancellationToken)
    {
        var next = new HttpRequestMessage(original.Method, location);
        foreach (var (key, values) in original.Headers)
        {
            if (string.Equals(key, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            next.Headers.TryAddWithoutValidation(key, values);
        }

        // 同 Origin 才保留 Authorization（跨 Origin 重定向一律移除）
        if (original.Headers.Authorization is not null && sameOrigin)
        {
            next.Headers.Authorization = original.Headers.Authorization;
        }

        // 同 Origin POST/PUT 等请求复制 body 与 Content headers；跨 Origin 敏感方法已在调用方拒绝
        if (sameOrigin && original.Content is not null)
        {
            var bytes = await original.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var content = new ByteArrayContent(bytes);
            foreach (var (key, values) in original.Content.Headers)
            {
                content.Headers.TryAddWithoutValidation(key, values);
            }
            next.Content = content;
        }
        return next;
    }

    private static bool IsRedirect(HttpResponseMessage response, out Uri? location)
    {
        location = response.Headers.Location;
        return location is not null
            && response.StatusCode is System.Net.HttpStatusCode.MovedPermanently
                or System.Net.HttpStatusCode.Found
                or System.Net.HttpStatusCode.SeeOther
                or System.Net.HttpStatusCode.TemporaryRedirect
                or System.Net.HttpStatusCode.PermanentRedirect;
    }

    private static bool IsSameOrigin(Uri first, Uri second) =>
        string.Equals(first.Scheme, second.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(first.Host, second.Host, StringComparison.OrdinalIgnoreCase)
        && first.Port == second.Port;

    private static JsonElement ParseEnvelope(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var codeElement))
            {
                throw new EriseApiException(500000, "响应格式无效");
            }

            var code = codeElement.GetInt32();
            var message = root.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()
                : string.Empty;
            if (code != 0)
            {
                if (code == 401000)
                {
                    throw new EriseUnauthorizedException(code, message ?? "未授权");
                }
                throw new EriseApiException(code, message ?? "请求失败");
            }

            return root.TryGetProperty("data", out var dataElement) ? dataElement.Clone() : default;
        }
        catch (JsonException)
        {
            throw new EriseApiException(500000, "响应格式无效");
        }
    }
}
