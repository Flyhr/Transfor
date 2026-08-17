namespace Transfor;

// Erise API 数据模型与客户端契约（Phase 6.8，A 阶段接口）。
// 字段与后端契约一致（erise-desktop-v1.yaml / INT-A-003）。

internal sealed record EriseCaptchaResponse(string CaptchaId, string CaptchaImage);

internal sealed record EriseUser(
    long Id,
    string Username,
    string DisplayName,
    string? Email,
    string RoleCode,
    string? AvatarUrl,
    string? Bio);

internal sealed record EriseAuthTokens(string AccessToken, string RefreshToken, EriseUser User);

internal sealed record EriseProject(
    long Id,
    long OwnerUserId,
    string Name,
    string? Description,
    string ProjectStatus,
    int Archived,
    long FileCount,
    long DocumentCount,
    string CreatedAt,
    string UpdatedAt);

internal sealed record ErisePageResponse(
    IReadOnlyList<EriseProject> Records,
    long PageNum,
    long PageSize,
    long Total,
    long TotalPages);

// 服务端业务/协议错误（message 为服务端返回文案，不含 Token）
internal class EriseApiException : Exception
{
    public int Code { get; }

    public EriseApiException(int code, string message)
        : base(message) => Code = code;
}

// 401 未授权（Bearer 无效/会话失效），触发认证生命周期刷新
internal sealed class EriseUnauthorizedException : EriseApiException
{
    public EriseUnauthorizedException(int code, string message)
        : base(code, message)
    {
    }
}

// Erise API 客户端（A 阶段接口：captcha/login/refresh/logout/users/me/projects）
internal interface IEriseClient
{
    Task<EriseCaptchaResponse> GetCaptchaAsync(CancellationToken cancellationToken);

    Task<EriseAuthTokens> LoginAsync(
        string username,
        string password,
        string captchaId,
        string captchaCode,
        CancellationToken cancellationToken);

    Task<EriseAuthTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken);

    Task LogoutAsync(string refreshToken, CancellationToken cancellationToken);

    Task<EriseUser> GetCurrentUserAsync(CancellationToken cancellationToken);

    Task<ErisePageResponse> GetProjectsAsync(long pageNum, long pageSize, CancellationToken cancellationToken);
}
