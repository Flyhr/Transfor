using System.Reflection;

namespace Transfor;

// 应用当前版本：唯一来源为 csproj 的 <Version>（SemVer）；
// 优先读取 InformationalVersion，兜底程序集版本号
internal static class AppVersion
{
    public static string Current =>
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}
