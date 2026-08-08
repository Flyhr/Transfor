using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Transfor;

// 隐藏浏览器宿主（Phase 4A）：UI 线程常驻的不可见窗体，
// 承载「解析」与「下载」两个 WebView2 控件（与「浏览器」页共享同一环境/Profile）；
// 后台线程的解析/下载请求经 RunOnUiAsync 调度到本窗体所在 UI 线程执行
internal sealed class BrowserHostForm : Form
{
    private readonly CoreWebView2Environment environment;
    private WebView2? captureControl;
    private WebView2? downloadControl;

    public BrowserHostForm(CoreWebView2Environment environment)
    {
        this.environment = environment ?? throw new ArgumentNullException(nameof(environment));

        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        Opacity = 0;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Size = new Size(1, 1);
    }

    // 在指定 UI 线程上执行 action（本窗体所在线程）；已在 UI 线程则直接执行；
    // 带超时避免后台任务等待 UI 线程卡死
    internal async Task<T> RunOnUiAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        // 句柄未创建时 InvokeRequired 恒为 false：后台线程会被误判直接执行 → COM 跨线程崩溃；
        // 宿主已显示化（Show）句柄必然存在，此处仅作最后防线
        if (!IsHandleCreated)
        {
            if (!Application.MessageLoop)
            {
                throw new InvalidOperationException("浏览器宿主尚未就绪（句柄未创建）。");
            }
            _ = Handle;
        }

        if (InvokeRequired)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            BeginInvoke(async () =>
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

    // 捕获页面数据：导航 → 等待加载 → 读取 CDP 详情接口响应体（登录态最可靠）→
    // 轮询提取结构化数据与 DOM 候选；导航全程记录真实网络请求；
    // 可跨线程调用（内部调度 UI 线程）
    public async Task<(string? StructuredJson, IReadOnlyList<BrowserCapturedCandidate> Candidates, IReadOnlyList<NetworkResourceRecord> NetworkRecords)> CapturePageAsync(
        Uri pageUri,
        CancellationToken cancellationToken)
    {
        return await RunOnUiAsync(async () =>
        {
            var core = await GetCaptureControlAsync().ConfigureAwait(true);
            using var networkCapture = new NetworkCaptureService(core);
            networkCapture.Enable();
            using var cdpCapture = new CdpNetworkCaptureService(core);
            await cdpCapture.EnableAsync().ConfigureAwait(true);

            // 导航带重试：ConnectionReset / 网络波动 / 抖音风控等瞬态错误可经重试恢复；
            // 快速失败毫秒级返回，重试开销小；超时慢导航罕见
            const int MaxNavigationAttempts = 3;
            Exception? navigationError = null;
            for (var attempt = 1; attempt <= MaxNavigationAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var navigationTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
                {
                    // 只认成功导航；失败（DNS/HTTP/证书/连接重置等）交给重试逻辑
                    if (e.IsSuccess)
                    {
                        navigationTcs.TrySetResult();
                    }
                    else
                    {
                        navigationTcs.TrySetException(new InvalidOperationException($"页面导航失败：{e.WebErrorStatus}"));
                    }
                }

                core.NavigationCompleted += OnNavigationCompleted;
                try
                {
                    core.Navigate(pageUri.ToString());
                    await navigationTcs.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(true);
                    navigationError = null;
                    break;
                }
                catch (Exception ex)
                {
                    navigationError = ex;
                    if (attempt < MaxNavigationAttempts)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(true);
                    }
                }
                finally
                {
                    core.NavigationCompleted -= OnNavigationCompleted;
                }
            }

            if (navigationError is not null)
            {
                throw new InvalidOperationException(
                    $"{navigationError.Message}（已重试 {MaxNavigationAttempts} 次），可能是网络波动或抖音风控，请稍后重试。");
            }

            // CDP：登录态详情接口响应体（完整作品数据，优先于页面脚本）
            string? structuredJson = await cdpCapture
                .WaitForDetailResponseAsync(TimeSpan.FromSeconds(8), cancellationToken)
                .ConfigureAwait(true);
            if (structuredJson is not null && !BrowserCaptureSession.HasWorkData(structuredJson))
            {
                structuredJson = null;
            }

            // 轮询提取结构化数据与 DOM 候选（SPA 渲染/懒加载窗口）
            IReadOnlyList<BrowserCapturedCandidate> candidates = Array.Empty<BrowserCapturedCandidate>();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                structuredJson ??= await BrowserCaptureSession.ExtractStructuredDataAsync(core, cancellationToken).ConfigureAwait(true);
                candidates = await BrowserCaptureSession.ExtractDomCandidatesAsync(core, cancellationToken).ConfigureAwait(true);
                if (BrowserCaptureSession.HasWorkData(structuredJson) || candidates.Count > 0)
                {
                    break;
                }

                await Task.Delay(1000, cancellationToken).ConfigureAwait(true);
            }

            return (structuredJson, candidates, networkCapture.DisableAndSnapshot());
        }, cancellationToken).ConfigureAwait(false);
    }

    // 经浏览器 fetch（携带 Cookie 与真实浏览器指纹）直取详情接口；可跨线程调用
    public async Task<string?> FetchDetailAsync(Uri detailUri, CancellationToken cancellationToken)
    {
        return await RunOnUiAsync(async () =>
        {
            var core = await GetCaptureControlAsync().ConfigureAwait(true);
            return await BrowserCaptureSession.FetchDetailAsync(core, detailUri, cancellationToken).ConfigureAwait(true);
        }, cancellationToken).ConfigureAwait(false);
    }

    // 经浏览器网络栈下载媒体到 partPath（分块流式写入）；返回 null 表示成功，否则错误信息
    public async Task<string?> DownloadMediaAsync(
        Uri mediaUri,
        string partPath,
        CancellationToken cancellationToken,
        Action<long, long?>? progress,
        long? maxBytes)
    {
        return await RunOnUiAsync(async () =>
        {
            var core = await GetDownloadControlAsync().ConfigureAwait(true);
            return await BrowserDownloadController.DownloadAsync(core, mediaUri, partPath, cancellationToken, progress, maxBytes)
                .ConfigureAwait(true);
        }, cancellationToken).ConfigureAwait(false);
    }

    // 读取指定 URI 域下的 Cookie（解析/下载会话的登录态）；可跨线程调用
    public async Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(Uri uri, CancellationToken cancellationToken)
    {
        return await RunOnUiAsync(async () =>
        {
            var core = await GetDownloadControlAsync().ConfigureAwait(true);
            return await BrowserCookieService.ReadCookiesAsync(core, uri).ConfigureAwait(true);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CoreWebView2> GetCaptureControlAsync()
    {
        var control = captureControl;
        if (control is null)
        {
            control = await CreateControlAsync().ConfigureAwait(true);
            captureControl = control;
        }

        return control.CoreWebView2!;
    }

    private async Task<CoreWebView2> GetDownloadControlAsync()
    {
        var control = downloadControl;
        if (control is null)
        {
            control = await CreateControlAsync().ConfigureAwait(true);
            downloadControl = control;
        }

        return control.CoreWebView2!;
    }

    private async Task<WebView2> CreateControlAsync()
    {
        var webView = new WebView2 { Dock = DockStyle.Fill, Visible = false };
        Controls.Add(webView);
        await webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
        return webView;
    }
}
