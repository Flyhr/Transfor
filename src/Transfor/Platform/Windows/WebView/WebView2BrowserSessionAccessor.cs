using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.IO;

namespace Transfor;

// WebView2 浏览器会话访问器：实现 IBrowserSessionAccessor；
// 整个会话只使用一个 DouyinBrowserForm 宿主（单个 WebView2），
// 隐藏解析与可见登录共用同一实例，登录后在同一实例上重新导航并提取；
// WebView2 控件只能在创建它的 STA UI 线程访问，
// 后台线程的 Cookie/Capture 调用经 IUiDispatcher 切回 UI 线程；
// 会话 ID 用于下载时获取 Cookie，本类不把 Cookie 写入日志或 JSON
internal sealed class WebView2BrowserSessionAccessor : IBrowserSessionAccessor
{
    private const int NavigationTimeoutMilliseconds = 20_000;

    private readonly Control uiOwner;
    private readonly AppPaths paths;
    private readonly string sessionId = Guid.NewGuid().ToString("N");
    private readonly WinFormsUiDispatcher dispatcher;
    private readonly SemaphoreSlim browserDownloadGate = new(1, 1);
    private DouyinBrowserForm? browserForm;
    private CoreWebView2Environment? environment;
    private CoreWebView2? coreWebView;
    private volatile bool initialized;
    private volatile bool headerMaskAttached;

    public WebView2BrowserSessionAccessor(Control uiOwner, AppPaths paths)
    {
        this.uiOwner = uiOwner ?? throw new ArgumentNullException(nameof(uiOwner));
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        dispatcher = new WinFormsUiDispatcher(uiOwner);
    }

    public bool IsAvailable
    {
        get
        {
            lock (dispatcher)
            {
                return initialized && coreWebView is not null && !uiOwner.IsDisposed;
            }
        }
    }

    // 捕获页面：单实例导航 → 等待加载 → 收集图片/视频网络候选与结构化状态；
    // 交互模式显示同一窗口供用户登录，登录完成后再导航并提取；
    // 登录/验证码时以交互模式显示浏览器窗口由用户操作
    public async Task<BrowserCaptureResult> CaptureAsync(
        Uri pageUri,
        bool interactive,
        CancellationToken cancellationToken)
    {
        try
        {
            var captured = await dispatcher.InvokeAsync(async token =>
            {
                if (!await EnsureInitializedAsync(token))
                {
                    return new BrowserCaptureResult(null, null, null, Array.Empty<BrowserCapturedCandidate>(), BrowserCaptureStatus.Unavailable, "未检测到 WebView2 Runtime。");
                }

                var candidates = new List<BrowserCapturedCandidate>();
                void OnResourceReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
                {
                    // 事件处理器内任何 COM 异常都必须就地消化：
                    // WebView2 在响应头枚举/读取时偶发 COMException，冒泡会击穿整个解析流程
                    try
                    {
                        var contentType = args.Response?.Headers?.GetHeader("Content-Type") ?? string.Empty;
                        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                            || contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                        {
                            long? contentLength = null;
                            var lengthHeader = args.Response?.Headers?.GetHeader("Content-Length");
                            if (long.TryParse(lengthHeader, out var parsed))
                            {
                                contentLength = parsed;
                            }

                            candidates.Add(new BrowserCapturedCandidate(
                                new Uri(args.Request.Uri),
                                contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? MediaKind.Image : MediaKind.Video,
                                null,
                                null,
                                null,
                                contentType,
                                contentLength,
                                BrowserCandidateSource.Network));
                        }
                    }
                    catch
                    {
                        // 单个资源事件失败不影响整体捕获
                    }
                }

                coreWebView!.WebResourceResponseReceived += OnResourceReceived;
                try
                {
                    if (!await browserForm!.NavigateAsync(pageUri, TimeSpan.FromMilliseconds(NavigationTimeoutMilliseconds), token))
                    {
                        return new BrowserCaptureResult(null, null, null, Array.Empty<BrowserCapturedCandidate>(), BrowserCaptureStatus.Failed, "页面加载失败：抖音服务端可能拒绝了浏览器连接（TLS 拦截或风控验证），请改用可访问的网络或稍后再试。");
                    }

                    if (interactive)
                    {
                        var completed = await browserForm.ShowForLoginAsync(uiOwner, token);
                        if (!completed)
                        {
                            return new BrowserCaptureResult(null, null, null, Array.Empty<BrowserCapturedCandidate>(), BrowserCaptureStatus.RequiresUserInteraction, "用户未完成登录。");
                        }

                        // 登录完成后在同一实例上重新导航来源页，再提取数据
                        if (!await browserForm.NavigateAsync(pageUri, TimeSpan.FromMilliseconds(NavigationTimeoutMilliseconds), token))
                        {
                            return new BrowserCaptureResult(null, null, null, Array.Empty<BrowserCapturedCandidate>(), BrowserCaptureStatus.Failed, "登录后页面加载失败或超时。");
                        }
                    }

                    // 取结构化状态与轮播顺序
                    var structuredJson = await ExtractStructuredDataAsync(token);
                    return new BrowserCaptureResult(sessionId, structuredJson, null, candidates.ToArray(), BrowserCaptureStatus.Succeeded, null);
                }
                finally
                {
                    coreWebView.WebResourceResponseReceived -= OnResourceReceived;
                }
            }, cancellationToken);

            return captured;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new BrowserCaptureResult(null, null, null, Array.Empty<BrowserCapturedCandidate>(), BrowserCaptureStatus.Failed, "捕获已取消。");
        }
        catch
        {
            return new BrowserCaptureResult(null, null, null, Array.Empty<BrowserCapturedCandidate>(), BrowserCaptureStatus.Unavailable, "浏览器会话不可用。");
        }
    }

    // 浏览器网络栈下载：HttpClient 被服务端 TLS 指纹拦截时的兜底；
    // 导航到媒体 URL → 拦截响应 → 流式写入 .part → 统一终化（魔数/哈希/唯一移动）；
    // 单 WebView2 实例串行执行（下载互斥），进度报告使用任务 ID；
    // GetContentAsync 对超大响应可能返回 null，此时给出明确错误
    public async Task<BrowserDownloadResult> DownloadAsync(
        Uri mediaUri,
        Guid taskId,
        string targetPath,
        MediaKind kind,
        CancellationToken cancellationToken,
        IProgress<MediaDownloadProgress>? progress = null,
        long? maxBytes = null)
    {
        await browserDownloadGate.WaitAsync(cancellationToken);
        try
        {
            return await DownloadViaBrowserCoreAsync(mediaUri, taskId, targetPath, kind, cancellationToken, progress, maxBytes);
        }
        finally
        {
            browserDownloadGate.Release();
        }
    }

    // 获取与目标 URI 匹配的 Cookie（仅返回该 URI 域匹配的 Cookie，由 CookieManager 过滤）
    public async Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(
        string browserSessionId,
        Uri requestUri,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(browserSessionId, sessionId, StringComparison.Ordinal) || !IsAvailable)
        {
            return Array.Empty<BrowserCookie>();
        }

        try
        {
            return await dispatcher.InvokeAsync(async token =>
            {
                if (!await EnsureInitializedAsync(token) || coreWebView is null)
                {
                    return Array.Empty<BrowserCookie>();
                }

                var cookies = await coreWebView.CookieManager.GetCookiesAsync(requestUri.ToString());
                return cookies.Select(c => new BrowserCookie(c.Domain, c.Path, c.Name, c.Value, c.IsSecure)).ToArray();
            }, cancellationToken);
        }
        catch
        {
            return Array.Empty<BrowserCookie>();
        }
    }

    private async Task<BrowserDownloadResult> DownloadViaBrowserCoreAsync(
        Uri mediaUri,
        Guid taskId,
        string targetPath,
        MediaKind kind,
        CancellationToken cancellationToken,
        IProgress<MediaDownloadProgress>? progress,
        long? maxBytes)
    {
        try
        {
            return await dispatcher.InvokeAsync(async token =>
            {
                if (!await EnsureInitializedAsync(token))
                {
                    return BrowserDownloadResult.Failed("未检测到 WebView2 Runtime。");
                }

                var partPath = $"{targetPath}.part.{taskId:N}";
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                // 上次异常中断可能遗留 .part，先清理再下载
                if (File.Exists(partPath))
                {
                    MediaFileFinalizer.TryDelete(partPath);
                }

                // 拦截与导航 URL 完全一致的响应（导航即媒体本体，无子资源）
                var responseTcs = new TaskCompletionSource<CoreWebView2WebResourceResponseView?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                void OnResourceReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
                {
                    try
                    {
                        if (string.Equals(args.Request.Uri, mediaUri.ToString(), StringComparison.OrdinalIgnoreCase))
                        {
                            responseTcs.TrySetResult(args.Response);
                        }
                    }
                    catch
                    {
                        // 事件处理器内的 COM 异常不影响下载流程（等待将超时并给出明确错误）
                    }
                }

                coreWebView!.WebResourceResponseReceived += OnResourceReceived;
                try
                {
                    var navigated = await browserForm!.NavigateAsync(mediaUri, TimeSpan.FromMilliseconds(NavigationTimeoutMilliseconds), token);
                    // 导航失败但可能有响应（断流/异常页）；等待响应或超时
                    Task<CoreWebView2WebResourceResponseView?> responseTask;
                    try
                    {
                        responseTask = responseTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(NavigationTimeoutMilliseconds), token);
                    }
                    catch (TimeoutException)
                    {
                        // 超时（导航失败且无任何响应）：抖音服务端拒绝浏览器网络栈的连接时即此表现
                        responseTask = Task.FromResult<CoreWebView2WebResourceResponseView?>(null);
                    }
                    var response = await responseTask;
                    if (!navigated && response is null)
                    {
                        return BrowserDownloadResult.Failed("浏览器媒体加载失败：抖音服务端拒绝了连接（TLS 拦截），请改用可访问的网络或稍后再试。");
                    }
                    if (response is null)
                    {
                        return BrowserDownloadResult.Failed("浏览器未收到媒体响应：抖音服务端可能拒绝了连接（TLS 拦截），请改用可访问的网络或稍后再试。");
                    }
                    if (!(response.StatusCode >= 200 && response.StatusCode < 300))
                    {
                        return BrowserDownloadResult.Failed($"媒体请求失败：{(int)response.StatusCode}。");
                    }

                    return await SaveResponseContentAsync(response, partPath, targetPath, taskId, kind, maxBytes, token, progress);
                }
                finally
                {
                    coreWebView.WebResourceResponseReceived -= OnResourceReceived;
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return BrowserDownloadResult.CancelledResult();
        }
        catch (Exception ex)
        {
            return BrowserDownloadResult.Failed($"浏览器下载不可用：{ErrorChainFormatter.Format(ex)}");
        }
    }

    // 把浏览器收到的媒体响应流式写入 .part 并统一终化（魔数/哈希/唯一移动）
    private static async Task<BrowserDownloadResult> SaveResponseContentAsync(
        CoreWebView2WebResourceResponseView response,
        string partPath,
        string targetPath,
        Guid taskId,
        MediaKind kind,
        long? maxBytes,
        CancellationToken cancellationToken,
        IProgress<MediaDownloadProgress>? progress)
    {
        // 超大响应（超过 WebView2 内容视图上限）返回 null，明确提示而非静默失败
        var content = await response.GetContentAsync();
        if (content is null)
        {
            return BrowserDownloadResult.Failed("媒体响应过大，浏览器传输暂不支持。");
        }

        long declaredLength = 0;
        try
        {
            long.TryParse(response.Headers.GetHeader("Content-Length"), out declaredLength);
        }
        catch
        {
            // 缺省时仅按实际字节报告进度
        }

        try
        {
            await using var destination = new FileStream(
                partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32 * 1024, useAsync: true);
            var buffer = new byte[32 * 1024];
            long total = 0;
            while (true)
            {
                var read = await content.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (maxBytes is not null && total > maxBytes)
                {
                    return BrowserDownloadResult.Failed("媒体内容超过大小限制。");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                progress?.Report(MediaDownloadProgress.Create(taskId, total, declaredLength));
            }

            await destination.FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            MediaFileFinalizer.TryDelete(partPath);
            return BrowserDownloadResult.CancelledResult();
        }
        catch (Exception ex)
        {
            MediaFileFinalizer.TryDelete(partPath);
            return BrowserDownloadResult.Failed(ErrorChainFormatter.Format(ex));
        }

        var (savedPath, error) = await MediaFileFinalizer.TryFinalizeAsync(
            partPath, targetPath, kind, cancellationToken);
        if (savedPath is null)
        {
            MediaFileFinalizer.TryDelete(partPath);
            return BrowserDownloadResult.Failed(error ?? "下载内容无效。");
        }
        return BrowserDownloadResult.Succeeded(savedPath);
    }

    public async ValueTask DisposeAsync()
    {
        await dispatcher.InvokeAsync(token =>
        {
            try
            {
                browserForm?.Dispose();
            }
            catch
            {
                // 释放失败不掩盖退出流程
            }
            browserForm = null;
            coreWebView = null;
            initialized = false;
            return Task.FromResult(true);
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<bool> EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (uiOwner.IsDisposed || uiOwner.Disposing || !uiOwner.IsHandleCreated)
        {
            return false;
        }

        try
        {
            environment ??= await WebView2EnvironmentProvider.CreateAsync(paths.WebView2Directory, cancellationToken);
            if (environment is null)
            {
                return false;
            }

            // 交互登录关闭窗口后宿主已释放，重新创建；环境（含 Cookie 目录）保持不变
            if (browserForm is null || browserForm.IsDisposed)
            {
                browserForm = new DouyinBrowserForm();
            }

            if (!await browserForm.InitializeAsync(environment, cancellationToken))
            {
                return false;
            }

            coreWebView = browserForm.CoreWebView2;
            if (!headerMaskAttached)
            {
                // 移除 sec-ch-ua 中的 "Microsoft Edge WebView2" 品牌：
                // 抖音风控依据 client hints 识别嵌入式 WebView 并返回验证页
                coreWebView.WebResourceRequested += (_, args) =>
                {
                    try
                    {
                        args.Request.Headers.RemoveHeader("sec-ch-ua");
                    }
                    catch
                    {
                        // 请求已发送等场景忽略
                    }
                };
                coreWebView.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                headerMaskAttached = true;
            }
            initialized = true;
            return true;
        }
        catch
        {
            initialized = false;
            return false;
        }
    }

    // 通过 ExecuteScriptAsync 获取页面内结构化状态（RENDER_DATA 型 JSON）
    private async Task<string?> ExtractStructuredDataAsync(CancellationToken cancellationToken)
    {
        if (coreWebView is null)
        {
            return null;
        }

        const string script = @"(() => {
            const node = document.getElementById('RENDER_DATA');
            if (node && node.textContent) return node.textContent;
            for (const s of document.querySelectorAll('script')) {
                const t = (s.textContent || '').trim();
                if (t.startsWith('{')) return t;
            }
            return null;
        })()";

        var result = await coreWebView.ExecuteScriptAsync(script);
        if (string.IsNullOrWhiteSpace(result) || result == "null")
        {
            return null;
        }

        try
        {
            // ExecuteScriptAsync 返回 JSON 编码的字符串；取字符串值后可能是 URL 编码的 JSON
            using var doc = System.Text.Json.JsonDocument.Parse(result);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var raw = doc.RootElement.GetString();
                if (raw is null)
                {
                    return null;
                }
                return raw.StartsWith('{') ? raw : Uri.UnescapeDataString(raw);
            }
            return result;
        }
        catch
        {
            return null;
        }
    }
}
