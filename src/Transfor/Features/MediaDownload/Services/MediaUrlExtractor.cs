using System.Text.Json;

namespace Transfor;

// 媒体 URL 提取器（Phase 4B）：从结构化 JSON（aweme_detail 等）递归收集全部
// http(s) URL 作为「作品媒体白名单」——网络捕获的媒体候选必须命中白名单才被认定为作品媒体，
// 广告/头像/预加载等页面杂项请求即使被网络捕获也不会误抓
internal static class MediaUrlExtractor
{
    // 提取结构化 JSON 中的全部 http(s) URL（规范化 key 去重）
    public static IReadOnlyCollection<string> ExtractUrls(string? json)
    {
        var urls = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return urls;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            Collect(document.RootElement, urls);
        }
        catch (JsonException)
        {
            // JSON 损坏：返回空白名单（严格模式不产出候选）
        }

        return urls;
    }

    // URL 规范化 key（用于白名单匹配：忽略大小写 host 与 fragment）
    public static string NormalizeUrl(Uri uri) =>
        $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}{uri.AbsolutePath}{uri.Query}";

    private static void Collect(JsonElement element, HashSet<string> urls)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Collect(property.Value, urls);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, urls);
                }
                break;

            case JsonValueKind.String:
                var value = element.GetString();
                if (value is not null
                    && Uri.TryCreate(value, UriKind.Absolute, out var uri)
                    && uri.Scheme is "http" or "https"
                    && !string.IsNullOrWhiteSpace(uri.Host))
                {
                    urls.Add(NormalizeUrl(uri));
                }
                break;
        }
    }
}
