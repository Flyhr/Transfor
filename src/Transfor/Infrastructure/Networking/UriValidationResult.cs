namespace Transfor;

// URI 校验失败类别：策略拒绝（协议/私网/回环等）与 DNS 解析失败
internal enum UriValidationKind
{
    BlockedByPolicy,
    DnsFailed,
}

// URI 安全校验结果
internal sealed record UriValidationResult(
    bool IsAllowed,
    string? Error,
    UriValidationKind Kind = UriValidationKind.BlockedByPolicy);
