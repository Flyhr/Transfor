using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Transfor;

internal sealed class WindowsWindowInputService : IWindowInputService
{
    public bool TryRestoreWindow(nint handle, out string error)
    {
        if (handle == nint.Zero)
        {
            error = "目标窗口句柄无效。";
            return false;
        }

        if (!WindowsNative.SetForegroundWindow(handle))
        {
            error = $"恢复目标窗口失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public bool TrySendPaste(out string error)
    {
        var inputs = new[]
        {
            WindowsNative.CreateKeyInput(WindowsNative.VirtualKey.Control, keyUp: false),
            WindowsNative.CreateKeyInput(WindowsNative.VirtualKey.V, keyUp: false),
            WindowsNative.CreateKeyInput(WindowsNative.VirtualKey.V, keyUp: true),
            WindowsNative.CreateKeyInput(WindowsNative.VirtualKey.Control, keyUp: true),
        };

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