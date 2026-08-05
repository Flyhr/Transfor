namespace Transfor;

// 分享链接提取器：从分享文本中提取第一个有效 HTTP/HTTPS 链接，不访问网络
internal static class ShareLinkParser
{
    // 需要清理的尾部中文/英文标点
    private static readonly char[] TrailingPunctuation =
        "，。、；：！？（）【】《》“”‘’…,.!?;:)]}>'\"".ToCharArray();

    public static Uri? TryExtractFirstLink(string? text, out string? error)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "未在文本中找到链接。";
            return null;
        }

        // 从每个 http(s):// 起点依次扫描候选片段；候选无效时继续寻找下一个
        var index = 0;
        while (index < text.Length)
        {
            var httpsStart = text.IndexOf("https://", index, StringComparison.OrdinalIgnoreCase);
            var httpStart = text.IndexOf("http://", index, StringComparison.OrdinalIgnoreCase);
            var start = PickEarlier(httpsStart, httpStart);
            if (start < 0)
            {
                break;
            }

            var end = start;
            while (end < text.Length && !char.IsWhiteSpace(text[end]))
            {
                end++;
            }

            var candidate = text[start..end].TrimEnd(TrailingPunctuation);
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                error = null;
                return uri;
            }

            index = end;
        }

        error = "未在文本中找到链接。";
        return null;
    }

    private static int PickEarlier(int first, int second)
    {
        if (first < 0) return second;
        if (second < 0) return first;
        return Math.Min(first, second);
    }
}
