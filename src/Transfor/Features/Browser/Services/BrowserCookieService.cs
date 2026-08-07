using Microsoft.Web.WebView2.Core;

namespace Transfor;

// 浏览器 Cookie 服务（Task 3.4）：读取指定 URI 的 Cookie、清除 Cookie、清除全部浏览器数据；
// 实例绑定初始化后的 CoreWebView2；后续 Phase 4 允许转换为 HttpClient CookieContainer
internal sealed class BrowserCookieService
{
    private readonly CoreWebView2? core;

    public BrowserCookieService(CoreWebView2? core)
    {
        this.core = core;
    }

    public bool IsAvailable => core is not null;

    // 读取指定 URI 域下的全部 Cookie（映射到统一 BrowserCookie 模型）
    public async Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (core is null)
        {
            return Array.Empty<BrowserCookie>();
        }

        return await ReadCookiesAsync(core, uri).ConfigureAwait(false);
    }

    // 从任意 CoreWebView2 的 Cookie 管理器读取指定 URI 域下的 Cookie（隐藏宿主复用）
    internal static async Task<IReadOnlyList<BrowserCookie>> ReadCookiesAsync(CoreWebView2 core, Uri uri)
    {
        var cookies = await core.CookieManager.GetCookiesAsync(uri.ToString()).ConfigureAwait(false);
        var result = new List<BrowserCookie>(cookies.Count);
        foreach (var cookie in cookies)
        {
            result.Add(new BrowserCookie(
                Domain: cookie.Domain,
                Path: cookie.Path,
                Name: cookie.Name,
                Value: cookie.Value,
                Secure: cookie.IsSecure));
        }

        return result;
    }

    // 清除全部 Cookie（登录态一并清除）
    public void ClearCookies() => core?.CookieManager.DeleteAllCookies();

    // 仅清除缓存（Cookie/登录状态保留）
    public async Task ClearCacheAsync()
    {
        if (core?.Profile is null)
        {
            return;
        }

        await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.CacheStorage).ConfigureAwait(false);
    }

    // 清除全部浏览器数据：Cookie + 缓存 + DOM/本地存储（登录、缓存、LocalStorage 全部重置）
    public async Task ClearAllBrowserDataAsync()
    {
        if (core?.Profile is null)
        {
            return;
        }

        await core.Profile.ClearBrowsingDataAsync(
            CoreWebView2BrowsingDataKinds.Cookies
            | CoreWebView2BrowsingDataKinds.CacheStorage
            | CoreWebView2BrowsingDataKinds.AllDomStorage).ConfigureAwait(false);
    }
}
