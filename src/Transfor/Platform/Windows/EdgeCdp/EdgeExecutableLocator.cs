using System.Diagnostics;
using Microsoft.Win32;

namespace Transfor;

// Edge 可执行文件定位：注册表 App Paths 优先，其次常见安装路径
internal static class EdgeExecutableLocator
{
    private const string RegistryAppPath = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe";

    public static string? TryLocate()
    {
        try
        {
            var fromRegistry = Registry.GetValue(RegistryAppPath, "", null) as string;
            if (!string.IsNullOrEmpty(fromRegistry) && File.Exists(fromRegistry))
            {
                return fromRegistry;
            }
        }
        catch
        {
            // 注册表不可读时回退到常见路径
        }

        foreach (var candidate in new[]
                 {
                     @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                     @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static bool IsAvailable => TryLocate() is not null;
}
