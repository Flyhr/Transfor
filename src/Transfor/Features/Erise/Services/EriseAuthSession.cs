namespace Transfor;

// Erise 认证生命周期（Phase 6.8）：
// Access Token 仅存内存；Refresh Token 仅经 IEriseCredentialStore 持久化；
// 受保护请求遇 401 触发单飞刷新（并发只刷一次，其余等待同一任务）；
// 刷新成功后原请求最多重试一次；刷新失败清理当前 Origin 凭据与内存 Access Token。
internal sealed class EriseAuthSession : IEriseClient
{
    private readonly IEriseClient inner;
    private readonly IEriseCredentialStore credentials;
    private readonly EriseSettingsStore settings;
    private readonly object gate = new();
    private string? accessToken;
    private Task<bool>? refreshInFlight;

    public EriseAuthSession(
        IEriseClient inner,
        IEriseCredentialStore credentials,
        EriseSettingsStore settings)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    // 内存 Access Token（仅内存，不落盘）
    public string? CurrentAccessToken => accessToken;

    public Task<EriseCaptchaResponse> GetCaptchaAsync(CancellationToken cancellationToken) =>
        inner.GetCaptchaAsync(cancellationToken);

    public Task<EriseAuthTokens> LoginAsync(
        string username,
        string password,
        string captchaId,
        string captchaCode,
        CancellationToken cancellationToken) =>
        inner.LoginAsync(username, password, captchaId, captchaCode, cancellationToken);

    public Task<EriseAuthTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        inner.RefreshAsync(refreshToken, cancellationToken);

    public Task LogoutAsync(string refreshToken, CancellationToken cancellationToken) =>
        inner.LogoutAsync(refreshToken, cancellationToken);

    public Task<EriseUser> GetCurrentUserAsync(CancellationToken cancellationToken) =>
        SendWithRefreshAsync(() => inner.GetCurrentUserAsync(cancellationToken));

    public Task<ErisePageResponse> GetProjectsAsync(long pageNum, long pageSize, CancellationToken cancellationToken) =>
        SendWithRefreshAsync(() => inner.GetProjectsAsync(pageNum, pageSize, cancellationToken));

    // 测试辅助：直接注入内存 Access Token（模拟已登录状态）
    internal void SetAccessTokenForTest(string token) => accessToken = token;

    // 切换 Server 前调用：清空内存 Access Token 与在途刷新任务
    public void ResetSession()
    {
        lock (gate)
        {
            accessToken = null;
            refreshInFlight = null;
        }
    }

    // 401 单飞刷新 + 单次重试
    private async Task<T> SendWithRefreshAsync<T>(Func<Task<T>> call)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await call().ConfigureAwait(false);
            }
            catch (EriseUnauthorizedException)
            {
                if (attempt > 0 || !await TryRefreshAsync().ConfigureAwait(false))
                {
                    throw;
                }
            }
        }
    }

    // 单飞刷新：并发 401 共享同一刷新任务；失败清理凭据与内存 Token
    private Task<bool> TryRefreshAsync()
    {
        lock (gate)
        {
            return refreshInFlight ??= RefreshCoreAsync();
        }
    }

    private async Task<bool> RefreshCoreAsync()
    {
        try
        {
            var origin = settings.Current.ServerOrigin;
            var refreshToken = string.IsNullOrEmpty(origin) ? null : await credentials.ReadRefreshTokenAsync(origin).ConfigureAwait(false);
            if (string.IsNullOrEmpty(refreshToken))
            {
                return false;
            }

            var tokens = await inner.RefreshAsync(refreshToken, CancellationToken.None).ConfigureAwait(false);
            accessToken = tokens.AccessToken;
            if (origin is not null)
            {
                await credentials.SaveRefreshTokenAsync(origin, tokens.RefreshToken).ConfigureAwait(false);
            }
            return true;
        }
        catch (Exception)
        {
            // 刷新失败：清理当前 Origin 凭据与内存 Access Token
            accessToken = null;
            if (settings.Current.ServerOrigin is { } origin)
            {
                try
                {
                    await credentials.DeleteCredentialAsync(origin).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 清理失败不影响降级结果
                }
            }
            return false;
        }
        finally
        {
            lock (gate)
            {
                refreshInFlight = null;
            }
        }
    }
}
