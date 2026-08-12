namespace Transfor;

// 错误分类（Phase 7 Task 7.2）：统一错误模型——
// UI 显示用户可理解信息（Message），日志保存技术细节（Details/异常链）
internal enum ErrorCategory
{
    Network,
    Parse,
    Browser,
    Download,
    Update,
    Permission,
    Unknown,
}

// 统一错误：分类 + 用户可读信息 + 技术细节（可选）
internal sealed record TransforError(ErrorCategory Category, string Message, string? Details = null)
{
    public static TransforError From(Exception exception, ErrorCategory? category = null)
    {
        var resolved = ErrorClassifier.Classify(exception, category);
        return new TransforError(resolved, exception.Message, ErrorChainFormatter.Format(exception));
    }
}

// 异常 → 分类（纯函数，可离线测试）：
// 优先使用调用方上下文分类（浏览器/更新边界按语义标注），其次按异常类型判定
internal static class ErrorClassifier
{
    public static ErrorCategory Classify(Exception exception, ErrorCategory? context = null)
    {
        if (context is not null)
        {
            return context.Value;
        }

        return exception switch
        {
            HttpRequestException or System.Net.Sockets.SocketException or System.IO.IOException or TimeoutException
                => ErrorCategory.Network,
            System.Text.Json.JsonException or FormatException
                => ErrorCategory.Parse,
            InvalidDataException
                => ErrorCategory.Download,
            UriValidationException
                => ErrorCategory.Permission,
            UnauthorizedAccessException
                => ErrorCategory.Permission,
            _ => ErrorCategory.Unknown,
        };
    }
}
