using System.Runtime.InteropServices;

namespace Transfor;

// 剪贴板文本读取器（Win32 直读，非 OLE）：
// System.Windows.Forms.Clipboard（OLE）在剪贴板被其他进程占用时可能长时间挂起，
// 阻塞 UI 线程导致 Bridge 调用超时；Win32 OpenClipboard 被占用时立即返回失败，
// 配合重试与总超时可在后台线程安全执行（Win32 剪贴板 API 不要求 STA）
internal static class ClipboardTextReader
{
    // CF_UNICODETEXT：Unicode 文本格式
    private const uint CfUnicodeText = 13;

    // 在总超时内重试打开剪贴板并读取文本；
    // 返回 null 表示剪贴板无文本格式或未能在超时内获得访问权
    public static string? ReadText(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                // 剪贴板被其他进程占用：短暂等待后重试
                Thread.Sleep(100);
                continue;
            }

            try
            {
                var handle = GetClipboardData(CfUnicodeText);
                if (handle == IntPtr.Zero)
                {
                    return null;
                }

                var pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    return Marshal.PtrToStringUni(pointer);
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }

        return null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern nint GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint hMem);
}
