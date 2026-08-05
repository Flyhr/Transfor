using System.Runtime.InteropServices;

namespace Transfor;

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
        catch (Exception ex) when (ex is ExternalException or ThreadStateException or InvalidOperationException)
        {
            error = $"写入系统剪贴板失败：{ex.Message}";
            return false;
        }
    }
}

