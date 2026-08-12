using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Transfor;

// 浏览器 Cookie 服务（Task 3.4）：读取指定 URI 的 Cookie、清除 Cookie、清除全部浏览器数据；
// CoreWebView2 成员只能在创建它的 UI 线程访问——本服务所有实例方法
// 统一经所属控件（WebView2）调度到 UI 线程执行（ConfigureAwait(true) 保持线程亲和）；
// 后续 Phase 4 允许转换为 HttpClient CookieContainer
internal sealed class BrowserCookieService
{
    private readonly WebView2 owner;
    private readonly CoreWebView2? core;

    public BrowserCookieService(WebView2 owner, CoreWebView2? core)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
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

        return await RunOnUiAsync(() => ReadCookiesAsync(core, uri)).ConfigureAwait(true);
    }

    // 从任意 CoreWebView2 的 Cookie 管理器读取指定 URI 域下的 Cookie（必须在 UI 线程调用）
    internal static async Task<IReadOnlyList<BrowserCookie>> ReadCookiesAsync(CoreWebView2 core, Uri uri)
    {
        var cookies = await core.CookieManager.GetCookiesAsync(uri.ToString()).ConfigureAwait(true);
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
    public Task ClearCookiesAsync() => RunOnUiAsync(() =>
    {
        core?.CookieManager.DeleteAllCookies();
        return Task.CompletedTask;
    });

    // 仅清除磁盘缓存（Cookie/登录状态保留；DiskCache 为 WebView2 实际磁盘 HTTP/资源缓存）
    public Task ClearCacheAsync() => RunOnUiAsync(async () =>
    {
        if (core?.Profile is not null)
        {
            await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache).ConfigureAwait(true);
        }
    });

    // 清除全部浏览器数据：Cookie + 磁盘缓存 + DOM/本地存储（登录、缓存、LocalStorage 全部重置）
    public Task ClearAllBrowserDataAsync() => RunOnUiAsync(async () =>
    {
        if (core?.Profile is not null)
        {
            await core.Profile.ClearBrowsingDataAsync(
                CoreWebView2BrowsingDataKinds.Cookies
                | CoreWebView2BrowsingDataKinds.DiskCache
                | CoreWebView2BrowsingDataKinds.AllDomStorage).ConfigureAwait(true);
        }
    });

    // 统一调度到所属控件的 UI 线程（已在 UI 线程则直接执行）；
    // CoreWebView2 成员跨线程访问会抛 COM 异常；
    // 句柄未创建时 BeginInvoke 会抛 InvalidOperationException——先检查再调度
    private async Task<T> RunOnUiAsync<T>(Func<Task<T>> action)
    {
        if (!owner.InvokeRequired)
        {
            return await action().ConfigureAwait(true);
        }

        if (!owner.IsHandleCreated || owner.IsDisposed)
        {
            // 控件句柄不可用：Cookie 操作无法安全调度，返回空结果
            return await Task.FromResult(default(T)!).ConfigureAwait(true);
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.BeginInvoke(async () =>
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
        return await tcs.Task.ConfigureAwait(true);
    }

    private Task RunOnUiAsync(Func<Task> action)
    {
        if (!owner.InvokeRequired)
        {
            return action();
        }

        if (!owner.IsHandleCreated || owner.IsDisposed)
        {
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.BeginInvoke(async () =>
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
        return tcs.Task;
    }
}
