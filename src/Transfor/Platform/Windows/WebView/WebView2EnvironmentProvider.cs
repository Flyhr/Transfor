using Microsoft.Web.WebView2.Core;

namespace Transfor;

// WebView2 环境提供：独立用户数据目录（Cookie/权限/缓存只存于此，不写入普通 JSON）；
// Runtime 缺失/COM 初始化失败统一转换为不可用结果
internal static class WebView2EnvironmentProvider
{
    public static async Task<CoreWebView2Environment?> CreateAsync(
        string userDataFolder,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder).ConfigureAwait(false);
        }
        catch
        {
            // Runtime 缺失、COM 初始化失败等统一视为不可用
            return null;
        }
    }
}
