using System.Runtime.InteropServices;

namespace Transfor;

// Windows 剪贴板服务：把文本写入系统剪贴板，失败时返回可读错误
internal sealed class WindowsClipboardService : IClipboardService
{
    public bool TrySetText(string text, out string error)
    {
        try
        {
            Clipboard.SetText(text);
            error = string.Empty;
            return true;
        }
        // 剪贴板被其他进程占用、线程状态异常等场景统一转换为失败结果
        catch (Exception ex) when (ex is ExternalException or ThreadStateException or InvalidOperationException)
        {
            error = $"写入系统剪贴板失败：{ex.Message}";
            return false;
        }
    }
}
