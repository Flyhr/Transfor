using System.Runtime.InteropServices;

namespace Transfor;

// 剪贴板服务抽象：把文本写入系统剪贴板
internal interface IClipboardService
{
    bool TrySetText(string text, out string error);
}

// 窗口输入服务抽象：恢复目标窗口到前台并模拟粘贴
internal interface IWindowInputService
{
    // 将目标窗口带到前台
    bool TryRestoreWindow(nint handle, out string error);

    // 模拟 Ctrl+V 粘贴
    bool TrySendPaste(out string error);
}

// 一次粘贴尝试的结果：是否成功及失败原因
internal sealed record PasteAttemptResult(bool Succeeded, string? Error)
{
    public static PasteAttemptResult Success { get; } = new(true, null);

    public static PasteAttemptResult Failure(string error) => new(false, error);
}

// 粘贴协调器：按「写剪贴板 → 恢复目标窗口 → 模拟粘贴」的顺序，把历史结果粘贴回原窗口
internal sealed class PasteCoordinator
{
    private readonly IClipboardService clipboard;
    private readonly IWindowInputService windowInput;

    public PasteCoordinator(IClipboardService clipboard, IWindowInputService windowInput)
    {
        this.clipboard = clipboard;
        this.windowInput = windowInput;
    }

    // 执行粘贴；任一步失败立即中止并返回错误信息
    public PasteAttemptResult TryPaste(HistoryEntry entry, nint targetWindow)
    {
        // 目标窗口无效（例如呼出历史面板时前台窗口已被销毁）
        if (targetWindow == nint.Zero)
        {
            return PasteAttemptResult.Failure("无法恢复历史面板打开前的窗口。");
        }

        // 第 1 步：把转换结果写入系统剪贴板
        if (!clipboard.TrySetText(entry.ConvertedOutput, out var clipboardError))
        {
            return PasteAttemptResult.Failure(clipboardError);
        }

        // 第 2 步：把用户呼出面板前的窗口恢复到前台
        if (!windowInput.TryRestoreWindow(targetWindow, out var restoreError))
        {
            return PasteAttemptResult.Failure(restoreError);
        }

        // 第 3 步：模拟 Ctrl+V 完成粘贴
        if (!windowInput.TrySendPaste(out var pasteError))
        {
            return PasteAttemptResult.Failure(pasteError);
        }

        return PasteAttemptResult.Success;
    }
}
