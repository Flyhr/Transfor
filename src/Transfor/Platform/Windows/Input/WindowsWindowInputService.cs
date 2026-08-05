using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Transfor;

// Windows 窗口输入服务：把目标窗口带到前台，并模拟 Ctrl+V 完成粘贴
internal sealed class WindowsWindowInputService : IWindowInputService
{
    // 把目标窗口置为前台窗口
    public bool TryRestoreWindow(nint handle, out string error)
    {
        // 句柄无效
        if (handle == nint.Zero)
        {
            error = "目标窗口句柄无效。";
            return false;
        }

        // Windows 会限制前台窗口切换（例如另一进程的窗口刚被激活），失败时返回错误
        if (!WindowsNative.SetForegroundWindow(handle))
        {
            error = $"恢复目标窗口失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    // 模拟按下并释放 Ctrl+V（按下/抬起各一次），触发目标窗口执行粘贴
    public bool TrySendPaste(out string error)
    {
        var inputs = new[]
        {
            WindowsNative.CreateKeyInput(WindowsNative.VirtualKey.Control, keyUp: false),
            WindowsNative.CreateKeyInput(WindowsNative.VirtualKey.V, keyUp: false),
            WindowsNative.CreateKeyInput(WindowsNative.VirtualKey.V, keyUp: true),
            WindowsNative.CreateKeyInput(WindowsNative.VirtualKey.Control, keyUp: true),
        };

        // SendInput 返回实际发送的输入条数，少于请求数说明注入失败
        var sent = WindowsNative.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<WindowsNative.Input>());
        if (sent != inputs.Length)
        {
            error = $"模拟粘贴失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
