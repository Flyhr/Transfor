namespace Transfor;

// 更新检查结果：状态 + 涉及版本 + 策略原文；
// CheckFailed 时 Error 携带用户可读信息（技术细节由调用方记录）
internal sealed record UpdateCheckResult(
    UpdateStatus Status,
    UpdateVersion? CurrentVersion,
    UpdateVersion? LatestVersion,
    UpdateVersion? MinimumVersion,
    UpdatePolicy? Policy,
    string? Error = null);
