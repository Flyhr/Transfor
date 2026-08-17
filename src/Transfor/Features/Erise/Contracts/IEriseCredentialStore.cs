namespace Transfor;

// Erise 凭据存储契约（Phase 6.7）：
// 仅持久化 Refresh Token；Access Token 只允许存内存，Password 永不保存；
// 凭据按规范化 Server Origin 隔离；缺失/读取失败/删除不存在一律安全降级。
internal interface IEriseCredentialStore
{
    Task<bool> HasCredentialAsync(string origin);

    Task<string?> ReadRefreshTokenAsync(string origin);

    Task SaveRefreshTokenAsync(string origin, string refreshToken);

    Task DeleteCredentialAsync(string origin);
}
