using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Transfor;

internal sealed class GlobalHotKeyManager : IDisposable
{
    private const int FirstHotKeyId = 0x4E01;
    private readonly HotKeyWindow window;
    private int activeHotKeyId;
    private HotKeyBinding? activeBinding;
    private bool disposed;

    public GlobalHotKeyManager()
    {
        window = new HotKeyWindow();
        window.HotKeyPressed += Window_HotKeyPressed;
    }

    public event EventHandler? HotKeyPressed;

    public HotKeyBinding? ActiveBinding => activeBinding;

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

    public bool TryReplace(HotKeyBinding binding, out string error)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (activeBinding == binding)
        {
            error = string.Empty;
            return true;
        }

        var candidateId = activeHotKeyId == FirstHotKeyId ? FirstHotKeyId + 1 : FirstHotKeyId;
        if (!WindowsNative.RegisterHotKey(window.Handle, candidateId, (uint)binding.ToNativeModifiers(), (uint)binding.Key))
        {
            error = FormatRegistrationError();
            return false;
        }

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

    private void Window_HotKeyPressed(object? sender, EventArgs e)
    {
        HotKeyPressed?.Invoke(this, EventArgs.Empty);
    }

    private static string FormatRegistrationError()
    {
        var code = Marshal.GetLastWin32Error();
        return $"注册全局快捷键失败：{new Win32Exception(code).Message}（错误码 {code}）。";
    }

    private sealed class HotKeyWindow : NativeWindow, IDisposable
    {
        public HotKeyWindow()
        {
            CreateHandle(new CreateParams());
        }

        public event EventHandler? HotKeyPressed;

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

