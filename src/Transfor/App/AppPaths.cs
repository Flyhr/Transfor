namespace Transfor;

internal sealed class AppPaths
{
    public AppPaths(string applicationDirectory)
    {
        ApplicationDirectory = Path.GetFullPath(applicationDirectory);
    }

    public string ApplicationDirectory { get; }
    public string LegacyStateFile => Path.Combine(ApplicationDirectory, "state.json");
    public static AppPaths Default => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Transfor"));
}