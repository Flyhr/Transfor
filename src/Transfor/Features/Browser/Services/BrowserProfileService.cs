namespace Transfor;

// 浏览器 Profile 管理（Task 3.3）：独立持久化目录
// （%LOCALAPPDATA%\Transfor\Browser\UserData），Cookie/LocalStorage/缓存/登录状态
// 只存于此目录；登录一次抖音后后续保持登录态
internal sealed class BrowserProfileService
{
    public BrowserProfileService(string userDataFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataFolder);
        UserDataFolder = Path.GetFullPath(userDataFolder);
    }

    // WebView2 用户数据目录（BrowserEnvironmentFolder）
    public string UserDataFolder { get; }

    // 确保目录存在（WebView2 环境创建前调用）
    public void EnsureCreated() => Directory.CreateDirectory(UserDataFolder);
}
