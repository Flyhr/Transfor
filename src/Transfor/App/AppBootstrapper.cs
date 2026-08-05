namespace Transfor;

internal static class AppBootstrapper
{
    public static AppServices Create()
    {
        var paths = AppPaths.Default;
        var state = LegacyHistoryStore.Load(paths.LegacyStateFile);
        return new AppServices
        {
            State = state,
            HotKeys = new GlobalHotKeyManager(),
            PasteCoordinator = new PasteCoordinator(new WindowsClipboardService(), new WindowsWindowInputService()),
        };
    }
}