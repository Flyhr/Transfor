namespace Transfor;

// AppShell 的关闭与启动展示决策保持纯函数，便于在无 WebView2 Runtime 的环境回归验证。
internal enum AppShellCloseDecision
{
    AllowClose,
    HideToTray,
}

internal static class AppShellLifecyclePolicy
{
    public static AppShellCloseDecision DecideClose(
        bool initializationFailureClose,
        bool exiting,
        bool userClosing)
    {
        return initializationFailureClose || exiting || !userClosing
            ? AppShellCloseDecision.AllowClose
            : AppShellCloseDecision.HideToTray;
    }

    public static bool ShouldShowStartupInterface(
        UpdateStatus status,
        bool timedOut,
        bool requiredUpdateResolved,
        bool isDisposed,
        bool exiting)
    {
        return !isDisposed
            && !exiting
            && (timedOut || status != UpdateStatus.RequiredUpdate || requiredUpdateResolved);
    }
}
