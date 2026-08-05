using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Transfor;

// 全局快捷键管理器：基于 RegisterHotKey 注册系统级热键，并通过隐藏窗口转发 WM_HOTKEY 消息
internal sealed class GlobalHotKeyManager : IDisposable
{
    // 热键 ID 起点（同一窗口注册多个热键时在两个 ID 间轮换，便于安全替换）
    private const int FirstHotKeyId = 0x4E01;

    // 用于接收 WM_HOTKEY 消息的隐藏窗口
    private readonly HotKeyWindow window;

    private int activeHotKeyId;

    // 当前生效的快捷键绑定
    private HotKeyBinding? activeBinding;
    private bool disposed;

    public GlobalHotKeyManager()
    {
        window = new HotKeyWindow();
        window.HotKeyPressed += Window_HotKeyPressed;
    }

    // 触发条件：用户按下已注册的全局快捷键
    public event EventHandler? HotKeyPressed;

    // 当前生效的快捷键
    public HotKeyBinding? ActiveBinding => activeBinding;

    // 注册全局快捷键；若已有生效的热键则改为替换
    public bool TryRegister(HotKeyBinding binding, out string error)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (activeBinding is not null)
        {
            return TryReplace(binding, out error);
        }

        var id = FirstHotKeyId;
        if (!WindowsNative.RegisterHotKey(window.Handle, id, (uint)binding.ToNativeModifiers(), (uint)binding.Key))
        {
            error = FormatRegistrationError();
            return false;
        }

        activeHotKeyId = id;
        activeBinding = binding;
        error = string.Empty;
        return true;
    }

    // 用新快捷键替换当前生效的快捷键：新键注册成功后才释放旧键，避免中间失去热键；
    // 释放旧键失败时回滚新键，保持原热键继续生效
    public bool TryReplace(HotKeyBinding binding, out string error)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        // 绑定未变化则直接视为成功
        if (activeBinding == binding)
        {
            error = string.Empty;
            return true;
        }

        // 用与当前相反的 ID 注册新键，防止与原键的 ID 冲突
        var candidateId = activeHotKeyId == FirstHotKeyId ? FirstHotKeyId + 1 : FirstHotKeyId;
        if (!WindowsNative.RegisterHotKey(window.Handle, candidateId, (uint)binding.ToNativeModifiers(), (uint)binding.Key))
        {
            error = FormatRegistrationError();
            return false;
        }

        // 释放旧键失败时回滚：注销新键并保留旧绑定
        if (activeBinding is not null && !WindowsNative.UnregisterHotKey(window.Handle, activeHotKeyId))
        {
            WindowsNative.UnregisterHotKey(window.Handle, candidateId);
            error = $"释放旧快捷键失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            return false;
        }

        activeHotKeyId = candidateId;
        activeBinding = binding;
        error = string.Empty;
        return true;
    }

    // 释放全局热键与隐藏窗口
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (activeBinding is not null)
        {
            WindowsNative.UnregisterHotKey(window.Handle, activeHotKeyId);
            activeBinding = null;
        }

        window.Dispose();
    }

    // 隐藏窗口收到 WM_HOTKEY 时转发给订阅者
    private void Window_HotKeyPressed(object? sender, EventArgs e)
    {
        HotKeyPressed?.Invoke(this, EventArgs.Empty);
    }

    // 把 Win32 错误码格式化为可读信息
    private static string FormatRegistrationError()
    {
        var code = Marshal.GetLastWin32Error();
        return $"注册全局快捷键失败：{new Win32Exception(code).Message}（错误码 {code}）。";
    }

    // 仅用于接收 WM_HOTKEY 消息的隐藏窗口
    private sealed class HotKeyWindow : NativeWindow, IDisposable
    {
        public HotKeyWindow()
        {
            CreateHandle(new CreateParams());
        }

        public event EventHandler? HotKeyPressed;

        // 拦截 WM_HOTKEY 消息并触发事件
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WindowsNative.WmHotKey)
            {
                HotKeyPressed?.Invoke(this, EventArgs.Empty);
            }

            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != nint.Zero)
            {
                DestroyHandle();
            }
        }
    }
}
