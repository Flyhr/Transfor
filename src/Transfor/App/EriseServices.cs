namespace Transfor;

// Erise 模块服务组合（Phase 6.8）：设置 + 凭据 + HTTP 客户端 + 认证会话
internal sealed class EriseServices
{
    // 服务器设置（规范化 Origin；凭据按 Origin 隔离）
    public required EriseSettingsStore Settings { get; init; }

    // Windows 安全凭据存储（仅持久化 Refresh Token）
    public required IEriseCredentialStore Credentials { get; init; }

    // 独立 HTTP 客户端（不复用媒体请求链；30 秒超时由 HttpClient 承载）
    public required EriseClient Client { get; init; }

    // 认证会话（内存 Access Token + 401 单飞刷新）
    public required EriseAuthSession Auth { get; init; }
}
