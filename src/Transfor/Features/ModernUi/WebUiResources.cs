using System.Reflection;

namespace Transfor;

// 本地 webui 资源读取（Phase 5）：HTML/CSS/JS 嵌入程序集，
// 经 NavigateToString 加载（与互联网浏览器 Profile 严格隔离）
internal static class WebUiResources
{
    private const string IndexResourceName = "Transfor.WebUi.index.html";
    private const string StylesResourceName = "Transfor.WebUi.styles.css";
    private const string AppScriptResourceName = "Transfor.WebUi.app.js";

    // 加载主页面 HTML（嵌入资源缺失时返回 null，调用方降级提示）
    public static string? LoadIndexHtml()
    {
        return LoadResource(IndexResourceName);
    }

    public static string? LoadStylesCss() => LoadResource(StylesResourceName);

    public static string? LoadAppScript() => LoadResource(AppScriptResourceName);

    private static string? LoadResource(string resourceName)
    {
        var assembly = typeof(WebUiResources).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName);
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

    internal static bool ContainsStyles() =>
        typeof(WebUiResources).Assembly.GetManifestResourceNames().Contains(StylesResourceName, StringComparer.Ordinal);

    internal static bool ContainsAppScript() =>
        typeof(WebUiResources).Assembly.GetManifestResourceNames().Contains(AppScriptResourceName, StringComparer.Ordinal);
}
