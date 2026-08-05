namespace Transfor;

static class Program
{
    [STAThread]
    static void Main()
    {
        // 初始化 WinForms 全局配置（高 DPI 支持、主题与字体默认值等）
        ApplicationConfiguration.Initialize();
        // 组合根：组装全部应用服务；using 保证退出时释放全局热键等资源
        using var services = AppBootstrapper.Create();
        // 启动消息循环，由 TransforApplicationContext 托管主窗口、历史面板与托盘的生命周期
        Application.Run(new TransforApplicationContext(services));
    }
}
