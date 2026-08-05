using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Transfor;

// Win32 平台互操作层：集中封装本项目用到的 user32.dll 声明、常量与输入事件结构
internal static class WindowsNative
{
    // WM_HOTKEY 消息：注册的全局快捷键被按下时系统向窗口发送
    internal const int WmHotKey = 0x0312;

    // SendInput 输入类型：键盘输入
    internal const uint InputKeyboard = 1;

    // 键盘事件标志：按键释放
    internal const uint KeyEventKeyUp = 0x0002;

    // RegisterHotKey 的修饰键标志
    internal const uint ModAlt = 0x0001;
    internal const uint ModControl = 0x0002;
    internal const uint ModShift = 0x0004;
    internal const uint ModWin = 0x0008;

    // 本工具用到的虚拟键码
    internal enum VirtualKey : ushort
    {
        Control = 0x11, // Ctrl
        V = 0x56,       // V（用于模拟粘贴）
    }

    // 注册全局快捷键：系统级监听指定组合键，按下时向 hWnd 窗口投递 WM_HOTKEY
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    // 注销已注册的全局快捷键
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(nint hWnd, int id);

    // 获取当前前台窗口句柄（历史面板呼出前记录，作为粘贴目标）
    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    // 将窗口设置为前台窗口（粘贴前恢复目标窗口）
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);

    // 向系统注入合成键盘/鼠标输入
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint numberOfInputs, Input[] inputs, int sizeOfInput);

    // 构造一个键盘输入事件（keyUp=false 为按下，true 为释放）
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

    // SendInput 的输入结构：类型 + 联合体（键盘/鼠标等，目前仅用键盘）
    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    // 输入联合体：按最大成员大小（32 字节）布局，目前只使用键盘部分
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    // 键盘输入事件参数
    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort VirtualKey; // 虚拟键码
        public ushort ScanCode;   // 硬件扫描码（未使用，传 0）
        public uint Flags;        // KEYEVENTF_* 事件标志
        public uint Time;         // 时间戳（0 表示由系统处理）
        public nint ExtraInfo;    // 附加信息
    }
}
