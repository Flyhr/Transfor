namespace Transfor;

internal sealed class AppPaths
{
    public AppPaths(string applicationDirectory) => ApplicationDirectory = Path.GetFullPath(applicationDirectory);
    public string ApplicationDirectory { get; }
    public string LegacyStateFile => Path.Combine(ApplicationDirectory, "state.json");
    public string LegacyBackupFile => Path.Combine(ApplicationDirectory, "state.v1.backup.json");
    public string SettingsFile => Path.Combine(ApplicationDirectory, "settings.json");
    public string UiStateFile => Path.Combine(ApplicationDirectory, "ui-state.json");
    public string TextHistoryFile => Path.Combine(ApplicationDirectory, "text-history.json");
    public string PendingMigrationFile => Path.Combine(ApplicationDirectory, "migration.v1.pending.json");
    public static AppPaths Default => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Transfor"));
}