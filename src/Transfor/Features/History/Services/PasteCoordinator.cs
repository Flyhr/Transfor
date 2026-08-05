using System.Runtime.InteropServices;

namespace Transfor;

internal interface IClipboardService
{
    bool TrySetText(string text, out string error);
}

internal interface IWindowInputService
{
    bool TryRestoreWindow(nint handle, out string error);

    bool TrySendPaste(out string error);
}

internal sealed record PasteAttemptResult(bool Succeeded, string? Error)
{
    public static PasteAttemptResult Success { get; } = new(true, null);

    public static PasteAttemptResult Failure(string error) => new(false, error);
}

internal sealed class PasteCoordinator
{
    private readonly IClipboardService clipboard;
    private readonly IWindowInputService windowInput;

    public PasteCoordinator(IClipboardService clipboard, IWindowInputService windowInput)
    {
        this.clipboard = clipboard;
        this.windowInput = windowInput;
    }

    public PasteAttemptResult TryPaste(HistoryEntry entry, nint targetWindow)
    {
        if (targetWindow == nint.Zero)
        {
            return PasteAttemptResult.Failure("无法恢复历史面板打开前的窗口。");
        }

        if (!clipboard.TrySetText(entry.ConvertedOutput, out var clipboardError))
        {
            return PasteAttemptResult.Failure(clipboardError);
        }

        if (!windowInput.TryRestoreWindow(targetWindow, out var restoreError))
        {
            return PasteAttemptResult.Failure(restoreError);
        }

        if (!windowInput.TrySendPaste(out var pasteError))
        {
            return PasteAttemptResult.Failure(pasteError);
        }

        return PasteAttemptResult.Success;
    }
}

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

