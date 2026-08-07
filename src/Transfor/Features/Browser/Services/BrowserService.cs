using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Transfor;

// 浏览器服务（Phase 3 + Phase 4A）：WebView2 环境与 Profile 生命周期管理；
// 独立 UserData 目录持久化 Cookie/登录态；初始化失败降级为错误提示，绝不影响应用启动；
// 「浏览器」页与隐藏宿主（BrowserHostForm）共享同一环境/Profile：
// 媒体解析/下载经隐藏宿主在 UI 线程执行，后台线程可安全调用
internal sealed class BrowserService : IBrowserService, IDisposable
{
    private readonly object sync = new();
    private readonly BrowserProfileService profile;
    private BrowserNavigationService? navigation;
    private BrowserCookieService? cookies;
    private CoreWebView2Environment? environment;
    private BrowserHostForm? host;
    private string? initializationError;
    private WebView2? control;
    private bool disposed;

    public BrowserService(BrowserProfileService profile)
    {
        this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    public BrowserProfileService Profile => profile;

    // 是否已成功初始化（浏览器页打开过且 WebView2 就绪）
    public bool IsInitialized => navigation is not null;

    public BrowserNavigationService Navigation => navigation ?? throw new InvalidOperationException("浏览器尚未初始化。");

    public BrowserCookieService Cookies => cookies ?? throw new InvalidOperationException("浏览器尚未初始化。");

    public string? InitializationError => initializationError;

    // 隐藏宿主（Phase 4A）：媒体解析/下载的执行载体；未初始化时调用会抛出
    public BrowserHostForm Host => host ?? throw new InvalidOperationException("浏览器宿主尚未初始化。");

    // 在 UI 线程调用：创建独立 Profile 的 WebView2 环境并初始化控件（浏览器页）；
    // 失败时记录原因并抛出，由页面降级显示提示（不崩溃）
    public async Task InitializeAsync(WebView2 webView2)
    {
        ArgumentNullException.ThrowIfNull(webView2);
        if (navigation is not null)
        {
            return;
        }

        try
        {
            var env = await GetEnvironmentAsync().ConfigureAwait(true);
            await webView2.EnsureCoreWebView2Async(env).ConfigureAwait(true);
            control = webView2;
            navigation = new BrowserNavigationService(webView2.CoreWebView2);
            cookies = new BrowserCookieService(webView2.CoreWebView2);
            initializationError = null;
        }
        catch (Exception ex)
        {
            initializationError = $"浏览器初始化失败：{ex.Message}（需要 Microsoft Edge WebView2 Runtime，Windows 11 已内置）。";
            throw;
        }
    }

    // 惰性初始化隐藏宿主（首次解析/下载时触发）：窗体与控件必须在 UI 线程创建；
    // 幂等：重复调用直接返回
    public async Task EnsureHostAsync(Control uiOwner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uiOwner);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (host is not null)
            {
                return;
            }
        }

        await RunOnUiAsync(uiOwner, async () =>
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (host is not null)
                {
                    return;
                }
            }

            var env = await GetEnvironmentAsync().ConfigureAwait(true);
            var newHost = new BrowserHostForm(env);
            newHost.CreateControl();
            lock (sync)
            {
                host = newHost;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    // 在 uiOwner 的 UI 线程上执行 action（无返回值版）；已在 UI 线程则直接执行；
    // 带超时避免后台任务等待 UI 线程卡死
    internal async Task RunOnUiAsync(
        Control uiOwner,
        Func<Task> action,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(uiOwner);
        if (uiOwner.IsDisposed)
        {
            throw new InvalidOperationException("主窗口已关闭，无法执行浏览器操作。");
        }

        if (uiOwner.InvokeRequired)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            uiOwner.BeginInvoke(async () =>
            {
                try
                {
                    await action();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
            await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(180), cancellationToken).ConfigureAwait(false);
            return;
        }

        await action().ConfigureAwait(false);
    }

    // 在 uiOwner 的 UI 线程上执行 action；已在 UI 线程则直接执行；
    // 带超时避免后台任务等待 UI 线程卡死
    internal async Task<T> RunOnUiAsync<T>(
        Control uiOwner,
        Func<Task<T>> action,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(uiOwner);
        if (uiOwner.IsDisposed)
        {
            throw new InvalidOperationException("主窗口已关闭，无法执行浏览器操作。");
        }

        if (uiOwner.InvokeRequired)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            uiOwner.BeginInvoke(async () =>
            {
                try
                {
                    tcs.TrySetResult(await action());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
            return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(180), cancellationToken).ConfigureAwait(false);
        }

        return await action().ConfigureAwait(false);
    }

    // 共享环境（浏览器页与隐藏宿主共用同一 Profile）；首次创建后缓存
    private async Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        lock (sync)
        {
            if (environment is not null)
            {
                return environment;
            }
        }

        profile.EnsureCreated();
        var env = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: profile.UserDataFolder).ConfigureAwait(true);
        lock (sync)
        {
            environment ??= env;
            return environment;
        }
    }

    // 释放浏览器资源（退出应用时由 AppServices 统一释放）；
    // 隐藏宿主与页面控件的进程回收由控件 Dispose 完成，这里仅清除引用
    public void Dispose()
    {
        BrowserHostForm? currentHost;
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            currentHost = host;
            host = null;
        }

        currentHost?.Dispose();
        navigation = null;
        cookies = null;
        control = null;
    }
}
