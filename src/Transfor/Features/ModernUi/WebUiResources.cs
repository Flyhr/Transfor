using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;

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

    // Phase 5B 加固：为虚拟主机映射创建一次性随机目录（%TEMP%\Transfor\WebUi\{guid}），
    // 仅保留当前用户 FullControl（移除继承）；进程退出/窗体关闭时调用 DeleteServingDirectory 清理
    public static string CreateServingDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "Transfor", "WebUi", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var identity = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("无法获取当前用户标识。");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(root).SetAccessControl(security);
        return root;
    }

    // Phase 5B 加固：清理临时目录（WebView2 可能仍在异步读取，删除失败静默忽略）
    public static void DeleteServingDirectory(string directory)
    {
        try
        {
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // 清理失败不影响主流程
        }
    }
}
