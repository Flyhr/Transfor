namespace Transfor;

// URI 安全校验结果
internal sealed record UriValidationResult(
    bool IsAllowed,
    string? Error);
