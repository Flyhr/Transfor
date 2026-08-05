namespace Transfor;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var services = AppBootstrapper.Create();
        Application.Run(new TransforApplicationContext(services));
    }
}
