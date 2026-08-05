namespace Transfor;

internal static class AppBootstrapper
{
    public static AppServices Create()
    {
        var paths = AppPaths.Default;
        StateMigrationService.EnsureMigrated(paths);
        var state = TextStateStore.Load(paths);
        return new AppServices
        {
            State = state,
            HotKeys = new GlobalHotKeyManager(),
            PasteCoordinator = new PasteCoordinator(new WindowsClipboardService(), new WindowsWindowInputService()),
        };
    }
}