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

internal static class WindowsNative
{
    internal const int WmHotKey = 0x0312;
    internal const uint InputKeyboard = 1;
    internal const uint KeyEventKeyUp = 0x0002;
    internal const uint ModAlt = 0x0001;
    internal const uint ModControl = 0x0002;
    internal const uint ModShift = 0x0004;
    internal const uint ModWin = 0x0008;

    internal enum VirtualKey : ushort
    {
        Control = 0x11,
        V = 0x56,
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint numberOfInputs, Input[] inputs, int sizeOfInput);

    internal static Input CreateKeyInput(VirtualKey key, bool keyUp)
    {
        return new Input
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = (ushort)key,
                    Flags = keyUp ? KeyEventKeyUp : 0,
                },
            },
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }
}


