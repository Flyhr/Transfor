namespace Transfor;

// 带类别的 URI 校验失败异常：供传输分类器区分「DNS 失败」与「安全策略拒绝」，
// 避免依赖异常消息字符串判断
internal sealed class UriValidationException : InvalidOperationException
{
    public UriValidationException(UriValidationKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    public UriValidationKind Kind { get; }
}
