using System.Reflection;

namespace Transfor;

// 本地 webui 资源读取（Phase 5）：HTML/CSS/JS 嵌入程序集，
// 经 NavigateToString 加载（与互联网浏览器 Profile 严格隔离）
internal static class WebUiResources
{
    private const string IndexResourceName = "Transfor.WebUi.index.html";

    // 加载主页面 HTML（嵌入资源缺失时返回 null，调用方降级提示）
    public static string? LoadIndexHtml()
    {
        var assembly = typeof(WebUiResources).Assembly;
        using var stream = assembly.GetManifestResourceStream(IndexResourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // 资源是否已嵌入（测试用）
    internal static bool ContainsIndex() =>
        typeof(WebUiResources).Assembly.GetManifestResourceNames().Contains(IndexResourceName, StringComparer.Ordinal);
}
