using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Transfor;

// 浏览器页面捕获会话（Phase 4A）：在 CoreWebView2 上执行有限 JS，
// 提取抖音页面结构化数据（RENDER_DATA/__NEXT_DATA__/__INITIAL_STATE__/_SSR_DATA/JSON script）
// 与 DOM 媒体候选（滚动触发懒加载 + data-src 回退），并在页面数据缺失时经页面 fetch 直取详情接口；
// 所有方法必须在 UI 线程调用（由隐藏宿主调度）
internal static class BrowserCaptureSession
{
    // 结构化数据提取：优先 RENDER_DATA，其次 __NEXT_DATA__/__INITIAL_STATE__/_SSR_DATA，
    // 最后首个非 ld+json 的 JSON script（与 Edge CDP 实现同款脚本）
    private const string StructuredDataScript = @"(() => {
        const node = document.getElementById('RENDER_DATA');
        if (node && node.textContent) return node.textContent;
        try {
            if (window.__NEXT_DATA__ && typeof window.__NEXT_DATA__ === 'object') return JSON.stringify(window.__NEXT_DATA__);
            if (window.__INITIAL_STATE__ && typeof window.__INITIAL_STATE__ === 'object') return JSON.stringify(window.__INITIAL_STATE__);
            if (window._SSR_DATA && typeof window._SSR_DATA === 'object') return JSON.stringify(window._SSR_DATA);
        } catch (e) { /* 序列化失败忽略 */ }
        for (const s of document.querySelectorAll('script')) {
            const t = (s.textContent || '').trim();
            if (!t.startsWith('{')) continue;
            const type = (s.type || '').toLowerCase();
            if (type === 'application/ld+json') continue;
            return t;
        }
        return null;
    })()";

    // DOM 媒体候选提取（触发脚本）：同步启动异步收集任务并写入全局状态；
    // 本机 WebView2 内核（151）下 ExecuteScriptAsync 对 Promise 返回空对象 {}，
    // 因此必须「同步触发 + 全局状态轮询」（见 AGENTS.md）
    private const string DomCandidatesTriggerScript = @"(() => {
        if (window.__transforDomResult) return window.__transforDomResult;
        if (window.__transforDomPending) return null;
        window.__transforDomPending = true;
        (async () => {
            try {
                const sleep = (ms) => new Promise(r => setTimeout(r, ms));
                const body = document.body || document.documentElement;
                const height = body.scrollHeight;
                for (let y = 0; y < height; y += 600) {
                    window.scrollTo(0, y);
                    await sleep(80);
                }
                window.scrollTo(0, 0);
                await sleep(400);

                const pick = (el) => {
                    const src = el.currentSrc || el.src
                        || (el.dataset && (el.dataset.src || el.dataset.rawSrc))
                        || el.getAttribute('data-raw-src')
                        || el.getAttribute('data-original')
                        || '';
                    if (!src || !src.startsWith('http')) return null;
                    return { src, w: el.naturalWidth || el.width || 0, h: el.naturalHeight || el.height || 0 };
                };
                const imgs = [...document.querySelectorAll('img')]
                    .map(pick).filter(Boolean);
                const videos = [...document.querySelectorAll('video')]
                    .map(pick).filter(Boolean);
                const sources = [...document.querySelectorAll('video source')]
                    .map(s => s.src).filter(u => u && u.startsWith('http'))
                    .map(u => ({ src: u, w: 0, h: 0 }));
                window.__transforDomResult = JSON.stringify({ images: imgs, videos: [...videos, ...sources] });
            } catch (e) {
                window.__transforDomResult = 'null';
            }
        })();
        return null;
    })()";

    // DOM 候选读取脚本：返回全局状态中的结果（字符串或 null）
    private const string DomCandidatesReaderScript = @"(() => window.__transforDomResult || null)()";

    // 经页面 fetch 直取详情接口（触发脚本）：同步启动 fetch 并写入全局状态；
    // credentials: include 携带浏览器 Cookie 与指纹；结果字符串或 'null'
    private const string FetchDetailTriggerScript = @"(() => {
        if (window.__transforFetchResult) return window.__transforFetchResult;
        if (window.__transforFetchPending) return null;
        window.__transforFetchPending = true;
        (async () => {
            try {
                const r = await fetch(URL, { credentials: 'include' });
                window.__transforFetchResult = r.ok ? await r.text() : 'null';
            } catch (e) {
                window.__transforFetchResult = 'null';
            }
        })();
        return null;
    })()";

    // 详情接口读取脚本
    private const string FetchDetailReaderScript = @"(() => window.__transforFetchResult || null)()";

    // 重置全局捕获状态（每次捕获会话开始前调用，避免旧页面结果残留）
    public static Task ResetAsync(CoreWebView2 core)
        => core.ExecuteScriptAsync("window.__transforDomResult = null; window.__transforDomPending = false; window.__transforFetchResult = null; window.__transforFetchPending = false;");

    // 统计页面媒体候选数（触发 + 轮询，最长 10 秒）：
    // 供新界面浏览器控件检测「当前页面检测到 X 个媒体」；必须在 UI 线程调用
    public static async Task<int> CountPageMediaAsync(CoreWebView2 core, CancellationToken cancellationToken)
    {
        await ResetAsync(core).ConfigureAwait(true);
        await TriggerDomCandidatesAsync(core).ConfigureAwait(true);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = await ReadDomCandidatesAsync(core, cancellationToken).ConfigureAwait(true);
            if (candidates.Count > 0)
            {
                return candidates.Count;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(true);
        }

        return 0;
    }

    // 提取结构化数据（页面字符串）；ExecuteScriptAsync 结果按 JSON 编码反序列化；
    // ConfigureAwait(true)：本方法在 BrowserHostForm UI 线程上下文执行，
    // 统一保持线程亲和（后续若在 continuation 访问 core 不会再踩线程坑）
    public static async Task<string?> ExtractStructuredDataAsync(CoreWebView2 core, CancellationToken cancellationToken)
    {
        var raw = await core.ExecuteScriptAsync(StructuredDataScript).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(raw) || raw == "null")
        {
            return null;
        }

        try
        {
            var value = JsonSerializer.Deserialize<string>(raw);
            if (value is null)
            {
                return null;
            }

            // RENDER_DATA 可能是 URL 编码的 JSON，解码后交给解析器
            return value.StartsWith('{') ? value : Uri.UnescapeDataString(value);
        }
        catch
        {
            return null;
        }
    }

    // 触发 DOM 候选异步收集（幂等：已完成/进行中直接返回）
    public static Task TriggerDomCandidatesAsync(CoreWebView2 core)
        => core.ExecuteScriptAsync(DomCandidatesTriggerScript);

    // 读取 DOM 候选收集结果（未完成时返回空列表；由调用方轮询）
    public static async Task<IReadOnlyList<BrowserCapturedCandidate>> ReadDomCandidatesAsync(
        CoreWebView2 core,
        CancellationToken cancellationToken)
    {
        var raw = await core.ExecuteScriptAsync(DomCandidatesReaderScript).ConfigureAwait(true);
        var value = DecodeResult(raw);
        if (value is null)
        {
            return Array.Empty<BrowserCapturedCandidate>();
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            var results = new List<BrowserCapturedCandidate>();
            CollectFromArray(root, "images", MediaKind.Image, results);
            CollectFromArray(root, "videos", MediaKind.Video, results);
            return results;
        }
        catch
        {
            return Array.Empty<BrowserCapturedCandidate>();
        }
    }

    // 经页面 fetch 直取详情接口响应文本（同步触发 + 轮询读取，最长 timeout）
    public static async Task<string?> FetchDetailAsync(
        CoreWebView2 core,
        Uri detailUri,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var triggerScript = FetchDetailTriggerScript.Replace("URL", JsonSerializer.Serialize(detailUri.ToString()), StringComparison.Ordinal);
        await core.ExecuteScriptAsync(triggerScript).ConfigureAwait(true);

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = await core.ExecuteScriptAsync(FetchDetailReaderScript).ConfigureAwait(true);
            var value = DecodeResult(raw);
            if (value is not null)
            {
                return value;
            }

            await Task.Delay(300, cancellationToken).ConfigureAwait(true);
        }

        return null;
    }

    // 解码 ExecuteScriptAsync 返回：JSON 编码字符串 → 原始文本；
    // null（JS null 或 'null' 字面量）与损坏返回 null
    internal static string? DecodeResult(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "null")
        {
            return null;
        }

        try
        {
            var value = JsonSerializer.Deserialize<string>(raw);
            if (value is null || value == "null")
            {
                return null;
            }

            return value;
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    // 结构化 JSON 是否含作品数据特征
    internal static bool HasWorkData(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return false;
        }

        return json.Contains("aweme_detail", StringComparison.Ordinal)
            || json.Contains("\"aweme_id\"", StringComparison.Ordinal);
    }

    // 从页面配置 JSON 提取作品 ID：优先 pathname（/video/123 或 /note/123），再找 aweme_id 字段
    internal static string? ExtractWorkId(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        var pathname = System.Text.RegularExpressions.Regex.Match(json, "\"pathname\"\\s*:\\s*\"/[^/\"]+/(\\d+)\"");
        if (pathname.Success)
        {
            return pathname.Groups[1].Value;
        }

        var awemeId = System.Text.RegularExpressions.Regex.Match(json, "\"aweme_id\"\\s*:\\s*\"(\\d+)\"");
        return awemeId.Success ? awemeId.Groups[1].Value : null;
    }

    // 作品详情接口 URL（桌面 web 端参数形态，fetch 时携带会话 Cookie）
    internal static Uri BuildDetailApiUri(string workId)
        => new($"https://www.douyin.com/aweme/v1/web/aweme/detail/?device_platform=webapp&aid=6383&channel=channel_pc_web&aweme_id={workId}&pc_client_type=1&version_code=190400&version_name=19.4.0&cookie_enabled=true&platform=PC&priority_region=CN&browser_language=zh-CN&browser_platform=Win32&browser_name=Edge&browser_version=151.0.0.0&os=windows");

    // 解析 {src,w,h} 候选数组为 BrowserCapturedCandidate（带 DOM 顺序）
    private static void CollectFromArray(
        JsonElement root,
        string propertyName,
        MediaKind kind,
        List<BrowserCapturedCandidate> results)
    {
        if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            var url = item.TryGetProperty("src", out var src) ? src.GetString() : null;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                continue;
            }

            int? width = item.TryGetProperty("w", out var w) && w.ValueKind == JsonValueKind.Number && w.GetInt32() > 0 ? w.GetInt32() : null;
            int? height = item.TryGetProperty("h", out var h) && h.ValueKind == JsonValueKind.Number && h.GetInt32() > 0 ? h.GetInt32() : null;
            results.Add(new BrowserCapturedCandidate(uri, kind, index++, width, height, null, null, BrowserCandidateSource.Dom));
        }
    }
}
