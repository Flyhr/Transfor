namespace Transfor;

// 引号转换器：把英文双引号与中文双引号分别转换为对应的单引号
public static class QuoteConverter
{
    public static string Convert(string? input)
    {
        // 空输入直接返回空字符串
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        return input
            .Replace('"', '\'')               // 英文双引号 " → '
            .Replace('\u201C', '\u2018')       // 中文左双引号 “ → ‘
            .Replace('\u201D', '\u2019');      // 中文右双引号 ” → ’
    }
}
