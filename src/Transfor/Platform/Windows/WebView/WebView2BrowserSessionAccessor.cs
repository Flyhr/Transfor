using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Transfor;

// WebView2 浏览器会话访问器：实现 IBrowserSessionAccessor；
// WebView2 控件/CookieManager 只能在创建它们的 STA UI 线程访问，
// 后台线程的 Cookie/Capture 调用经 IUiDispatcher 切回 UI 线程；
// 会话 ID 用于下载时获取 Cookie，本类不把 Cookie 写入日志或 JSON
internal sealed class WebView2BrowserSessionAccessor : IBrowserSessionAccessor
{
    private readonly Control uiOwner;
    private readonly AppPaths paths;
    private readonly string sessionId = Guid.NewGuid().ToString("N");
    private readonly WinFormsUiDispatcher dispatcher;
    private WebView2? host;
    private CoreWebView2? coreWebView;
    private CoreWebView2Environment? environment;
    private volatile bool initialized;

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

    // 捕获页面：导航 → 等待加载 → 收集图片/视频网络候选与结构化状态；
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

                coreWebView!.WebResourceResponseReceived += OnResourceReceived;
                try
                {
                    var form = new DouyinBrowserForm(environment!, pageUri.ToString());
                    coreWebView!.Navigate(pageUri.ToString());
                    // 等待页面完成首次加载；交互模式显示窗口供用户登录
                    if (interactive)
                    {
                        form.Show(uiOwner);
                        var closed = await form.CompletionTask.WaitAsync(token);
                        if (!closed)
                        {
                            return new BrowserCaptureResult(sessionId, null, null, candidates, BrowserCaptureStatus.RequiresUserInteraction, "用户未完成登录。");
                        }
                    }
                    else
                    {
                        await Task.Delay(3000, token);
                        form.CloseWithResult(true);
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

    public async ValueTask DisposeAsync()
    {
        await dispatcher.InvokeAsync(token =>
        {
            try
            {
                host?.Dispose();
            }
            catch
            {
                // 释放失败不掩盖退出流程
            }
            host = null;
            coreWebView = null;
            initialized = false;
            return Task.FromResult(true);
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<bool> EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (initialized && coreWebView is not null)
        {
            return true;
        }

        if (uiOwner.IsDisposed || uiOwner.Disposing || !uiOwner.IsHandleCreated)
        {
            return false;
        }

        // 首次初始化：创建环境与隐藏宿主（WebView2 必须由 UI 线程创建）
        if (uiOwner is Form form && !form.IsHandleCreated)
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

            host = new WebView2 { Dock = DockStyle.Fill, Visible = false };
            uiOwner.Controls.Add(host);
            await host.EnsureCoreWebView2Async(environment);
            coreWebView = host.CoreWebView2;
            uiOwner.Controls.Remove(host);
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
