namespace Transfor;

// 空格移除器：移除半角与全角空格，保留换行与制表符
public static class SpaceRemover
{
    public static string Remove(string? input)
    {
        // 空输入直接返回空字符串
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        return input
            .Replace(" ", string.Empty)        // 移除半角空格
            .Replace("\u3000", string.Empty);  // 移除全角空格（U+3000）
    }
}
