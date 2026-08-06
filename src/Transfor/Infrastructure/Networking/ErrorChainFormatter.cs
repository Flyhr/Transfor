using System.Text;

namespace Transfor;

// 错误链格式化：拼接外层与内层异常（类型 + 消息），
// 避免 UI 只显示 HttpRequestException 的外层壳而丢失真正原因；
// 限制层级与长度，防止把海量/敏感信息刷进界面
internal static class ErrorChainFormatter
{
    private const int MaxLevels = 3;
    private const int MaxLength = 400;

    public static string Format(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var builder = new StringBuilder();
        var current = exception;
        for (var level = 0; current is not null && level < MaxLevels; level++)
        {
            if (level > 0)
            {
                builder.Append(" → ");
                builder.Append('[').Append(current.GetType().Name).Append("] ");
            }
            builder.Append(current.Message);
            current = current.InnerException;
        }

        var text = builder.Length == 0 ? exception.Message : builder.ToString();
        return text.Length > MaxLength ? text[..MaxLength] + "…" : text;
    }
}
