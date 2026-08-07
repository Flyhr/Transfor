using Velopack;

namespace Transfor;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Velopack 更新基础设施：必须在窗口创建前最先运行
        // （负责暂存更新的自动应用与首次运行钩子）；
        // 更新失败绝不允许阻断应用启动
        try
        {
            VelopackApp.Build().Run();
        }
        catch
        {
        }

        // 初始化 WinForms 全局配置（高 DPI 支持、主题与字体默认值等）
        ApplicationConfiguration.Initialize();
        // 组合根：组装全部应用服务；using 保证退出时释放全局热键等资源
        using var services = AppBootstrapper.Create();
        // 启动消息循环，由 TransforApplicationContext 托管主窗口、历史面板与托盘的生命周期
        Application.Run(new TransforApplicationContext(services));
    }
}
