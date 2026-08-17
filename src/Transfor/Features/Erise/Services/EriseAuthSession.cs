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
    private long sessionGeneration;
    private long accessTokenGeneration;

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
    public string? CurrentAccessToken
    {
        get
        {
            lock (gate)
            {
                return accessToken;
            }
        }
    }

    public Task<EriseCaptchaResponse> GetCaptchaAsync(CancellationToken cancellationToken) =>
        inner.GetCaptchaAsync(cancellationToken);

    public async Task<EriseAuthTokens> LoginAsync(
        string username,
        string password,
        string captchaId,
        string captchaCode,
        CancellationToken cancellationToken)
    {
        var tokens = await inner.LoginAsync(username, password, captchaId, captchaCode, cancellationToken).ConfigureAwait(false);
        var origin = settings.Current.ServerOrigin
            ?? throw new InvalidOperationException("未配置服务器地址");
        long generation;
        lock (gate)
        {
            sessionGeneration++;
            refreshInFlight = null;
            generation = sessionGeneration;
        }

        await credentials.SaveRefreshTokenAsync(origin, tokens.RefreshToken).ConfigureAwait(false);
        lock (gate)
        {
            if (generation == sessionGeneration
                && string.Equals(settings.Current.ServerOrigin, origin, StringComparison.Ordinal))
            {
                accessToken = tokens.AccessToken;
                accessTokenGeneration++;
            }
        }
        return tokens;
    }

    public Task<EriseAuthTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        inner.RefreshAsync(refreshToken, cancellationToken);

    public async Task LogoutAsync(string refreshToken, CancellationToken cancellationToken)
    {
        await inner.LogoutAsync(refreshToken, cancellationToken).ConfigureAwait(false);
        var origin = settings.Current.ServerOrigin;
        if (origin is not null)
        {
            await credentials.DeleteCredentialAsync(origin).ConfigureAwait(false);
        }
        ResetSession();
    }

    public Task<EriseUser> GetCurrentUserAsync(CancellationToken cancellationToken) =>
        SendWithRefreshAsync(() => inner.GetCurrentUserAsync(cancellationToken));

    public Task<ErisePageResponse> GetProjectsAsync(long pageNum, long pageSize, CancellationToken cancellationToken) =>
        SendWithRefreshAsync(() => inner.GetProjectsAsync(pageNum, pageSize, cancellationToken));

    // 测试辅助：直接注入内存 Access Token（模拟已登录状态）
    internal void SetAccessTokenForTest(string token)
    {
        lock (gate)
        {
            accessToken = token;
            accessTokenGeneration++;
        }
    }

    // 切换 Server 前调用：清空内存 Access Token 与在途刷新任务
    public void ResetSession()
    {
        lock (gate)
        {
            sessionGeneration++;
            accessToken = null;
            accessTokenGeneration++;
            refreshInFlight = null;
        }
    }

    // 401 单飞刷新 + 单次重试
    private async Task<T> SendWithRefreshAsync<T>(Func<Task<T>> call)
    {
        for (var attempt = 0; ; attempt++)
        {
            var tokenGeneration = GetAccessTokenGeneration();
            try
            {
                return await call().ConfigureAwait(false);
            }
            catch (EriseUnauthorizedException)
            {
                var anotherRequestRefreshed = tokenGeneration != GetAccessTokenGeneration();
                if (attempt > 0 || (!anotherRequestRefreshed && !await TryRefreshAsync().ConfigureAwait(false)))
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
            if (refreshInFlight is not null)
            {
                return refreshInFlight;
            }

            var generation = sessionGeneration;
            var origin = settings.Current.ServerOrigin;
            var task = RefreshCoreAsync(origin, generation);
            refreshInFlight = task;
            _ = task.ContinueWith(
                _ => ClearRefreshTask(task, generation),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return task;
        }
    }

    private async Task<bool> RefreshCoreAsync(string? origin, long generation)
    {
        try
        {
            var refreshToken = string.IsNullOrEmpty(origin)
                ? null
                : await credentials.ReadRefreshTokenAsync(origin).ConfigureAwait(false);
            if (string.IsNullOrEmpty(refreshToken))
            {
                if (IsCurrentSession(origin, generation))
                {
                    lock (gate)
                    {
                        accessToken = null;
                        accessTokenGeneration++;
                    }
                }
                return false;
            }

            var tokens = await inner.RefreshAsync(refreshToken, CancellationToken.None).ConfigureAwait(false);
            if (!IsCurrentSession(origin, generation))
            {
                return false;
            }

            await credentials.SaveRefreshTokenAsync(origin!, tokens.RefreshToken).ConfigureAwait(false);
            lock (gate)
            {
                if (!IsCurrentSessionUnsafe(origin, generation))
                {
                    return false;
                }
                accessToken = tokens.AccessToken;
                accessTokenGeneration++;
            }
            return true;
        }
        catch (Exception)
        {
            // 刷新失败：清理当前 Origin 凭据与内存 Access Token
            if (!IsCurrentSession(origin, generation))
            {
                return false;
            }

            lock (gate)
            {
                accessToken = null;
                accessTokenGeneration++;
            }
            if (origin is not null)
            {
                try { await credentials.DeleteCredentialAsync(origin).ConfigureAwait(false); }
                catch (Exception) { /* 清理失败不影响降级结果 */ }
            }
            return false;
        }
    }

    private bool IsCurrentSession(string? origin, long generation)
    {
        lock (gate)
        {
            return IsCurrentSessionUnsafe(origin, generation);
        }
    }

    private bool IsCurrentSessionUnsafe(string? origin, long generation) =>
        generation == sessionGeneration
        && string.Equals(settings.Current.ServerOrigin, origin, StringComparison.Ordinal);

    private long GetAccessTokenGeneration()
    {
        lock (gate)
        {
            return accessTokenGeneration;
        }
    }

    private void ClearRefreshTask(Task<bool> task, long generation)
    {
        lock (gate)
        {
            if (sessionGeneration == generation && ReferenceEquals(refreshInFlight, task))
            {
                refreshInFlight = null;
            }
        }
    }
}
