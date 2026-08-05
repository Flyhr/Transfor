using System.Text.Json;

namespace Transfor;

// 抖音页面解析器：仅解析已知的结构化 JSON 容器（RENDER_DATA、内嵌状态、JSON-LD），
// 使用字符串扫描定位脚本块 + System.Text.Json 解析，不用正则实现完整 HTML DOM；
// 找不到作品数据 → EmptyShell；检测登录 → LoginRequired；删除/私密 → FailureReason
internal static class DouyinPageParser
{
    private static readonly string[] LoginMarkers =
    {
        "captcha", "验证码", "请先登录", "需要登录", "登录后查看",
    };

    private static readonly string[] RemovedMarkers =
    {
        "作品已删除", "内容已删除", "视频已删除", "不存在",
    };

    public static DouyinPageData Parse(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return new DouyinPageData(null, null, null, Array.Empty<DouyinAssetCandidate>(), true, false, null);
        }

        var loginRequired = LoginMarkers.Any(m => html.Contains(m, StringComparison.OrdinalIgnoreCase));
        var failureReason = RemovedMarkers.FirstOrDefault(m => html.Contains(m, StringComparison.OrdinalIgnoreCase));

        // 1. 结构化 JSON 容器（RENDER_DATA 型或内嵌状态）
        foreach (var script in ExtractScriptBlocks(html))
        {
            var json = DecodeScriptJson(script);
            if (json is null)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                if (TryParseWork(document.RootElement, out var data))
                {
                    return data with
                    {
                        LoginRequired = data.LoginRequired || loginRequired,
                        FailureReason = data.FailureReason ?? failureReason,
                    };
                }
            }
            catch (JsonException)
            {
                // 该脚本块不是作品 JSON，继续
            }
        }

        // 2. DOM 兜底：扫描 <img src> 与 <video src>
        var domAssets = ExtractDomCandidates(html);
        if (domAssets.Count > 0)
        {
            return new DouyinPageData(null, null, null, domAssets, false, loginRequired, failureReason);
        }

        // 3. 无任何候选：空壳，交由浏览器模式处理
        return new DouyinPageData(null, null, null, Array.Empty<DouyinAssetCandidate>(), true, loginRequired, failureReason);
    }

    // 直接解析浏览器捕获的结构化 JSON（ExecuteScriptAsync 结果）
    public static DouyinPageData ParseStructuredData(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (TryParseWork(document.RootElement, out var data))
            {
                return data;
            }
        }
        catch (JsonException)
        {
            // 落入空壳
        }

        return new DouyinPageData(null, null, null, Array.Empty<DouyinAssetCandidate>(), true, false, null);
    }

    // 提取所有 <script ...>...</script> 块内容
    private static IEnumerable<string> ExtractScriptBlocks(string html)
    {
        var results = new List<string>();
        var index = 0;
        while (index < html.Length)
        {
            var start = html.IndexOf("<script", index, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                break;
            }

            var contentStart = html.IndexOf('>', start);
            if (contentStart < 0)
            {
                break;
            }

            var end = html.IndexOf("</script", contentStart, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                break;
            }

            results.Add(html[(contentStart + 1)..end]);
            index = end + 8;
        }
        return results;
    }

    // 脚本内容可能是纯 JSON 或 URL 编码的 JSON（真实页面常见），两种都尝试
    private static string? DecodeScriptJson(string script)
    {
        var trimmed = script.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.StartsWith('{'))
        {
            return trimmed;
        }

        try
        {
            var decoded = Uri.UnescapeDataString(trimmed);
            return decoded.StartsWith('{') ? decoded : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    // 从作品 JSON 中提取数据；识别 aweme_detail / aweme 结构
    private static bool TryParseWork(JsonElement root, out DouyinPageData data)
    {
        data = default!;
        if (!root.TryGetProperty("aweme_detail", out var detail) && !root.TryGetProperty("aweme", out detail))
        {
            return false;
        }

        var postId = GetString(detail, "aweme_id");
        var title = GetString(detail, "desc");
        var authorName = detail.TryGetProperty("author", out var author) ? GetString(author, "nickname") : null;

        var assets = new List<DouyinAssetCandidate>();

        // 图片：按数组顺序为资产，url_list 为变体
        if (detail.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var image in images.EnumerateArray())
            {
                var variants = new List<DouyinVariantCandidate>();
                var width = GetInt(image, "width");
                var height = GetInt(image, "height");
                if (image.TryGetProperty("url_list", out var urls) && urls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var url in urls.EnumerateArray())
                    {
                        if (url.GetString() is { Length: > 0 } text)
                        {
                            variants.Add(new DouyinVariantCandidate(text, "image", width, height, null, null, null, null, MediaVariantSource.StructuredData));
                        }
                    }
                }

                if (variants.Count > 0)
                {
                    assets.Add(new DouyinAssetCandidate(index++, MediaKind.Image, variants));
                }
            }
        }

        // 视频：play_addr / download_addr / cover
        if (detail.TryGetProperty("video", out var video))
        {
            var variants = new List<DouyinVariantCandidate>();
            var width = GetInt(video, "width") ?? GetNestedInt(video, "play_addr", "width");
            var height = GetInt(video, "height") ?? GetNestedInt(video, "play_addr", "height");
            var fps = GetNestedInt(video, "play_addr", "fps");

            if (video.TryGetProperty("play_addr", out var playAddr))
            {
                CollectUrlList(playAddr, "url_list", "video/mp4", width, height, fps, MediaVariantSource.StructuredData, variants);
            }

            if (video.TryGetProperty("download_addr", out var downloadAddr))
            {
                CollectUrlList(downloadAddr, "url_list", "video/mp4", width, height, fps, MediaVariantSource.StructuredData, variants);
            }

            // 封面/缩略图仅作兜底
            if (video.TryGetProperty("cover", out var cover))
            {
                CollectUrlList(cover, "url_list", "image", null, null, null, MediaVariantSource.Thumbnail, variants);
            }

            if (variants.Count > 0)
            {
                assets.Add(new DouyinAssetCandidate(0, MediaKind.Video, variants));
            }
        }

        if (assets.Count == 0)
        {
            return false;
        }

        data = new DouyinPageData(postId, title, authorName, assets, false, false, null);
        return true;
    }

    private static void CollectUrlList(
        JsonElement container,
        string propertyName,
        string contentType,
        int? width,
        int? height,
        int? fps,
        MediaVariantSource source,
        List<DouyinVariantCandidate> variants)
    {
        if (!container.TryGetProperty(propertyName, out var urls) || urls.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var url in urls.EnumerateArray())
        {
            if (url.GetString() is { Length: > 0 } text)
            {
                variants.Add(new DouyinVariantCandidate(text, contentType, width, height, fps, null, null, null, source));
            }
        }
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) ? value.GetString() : null;

    private static int? GetInt(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    private static int? GetNestedInt(JsonElement element, string container, string property)
        => element.TryGetProperty(container, out var inner) ? GetInt(inner, property) : null;

    // DOM 兜底：字符串定位 <img src> / <video src>（非正则）
    private static List<DouyinAssetCandidate> ExtractDomCandidates(string html)
    {
        var images = ExtractAttributeValues(html, "img", "src");
        var videos = ExtractAttributeValues(html, "video", "src");

        var assets = new List<DouyinAssetCandidate>();
        var imageIndex = 0;
        foreach (var url in images)
        {
            assets.Add(new DouyinAssetCandidate(imageIndex++, MediaKind.Image, new[] { new DouyinVariantCandidate(url, "image", null, null, null, null, null, null, MediaVariantSource.Dom) }));
        }
        if (videos.Count > 0)
        {
            assets.Add(new DouyinAssetCandidate(0, MediaKind.Video, videos.Select(v => new DouyinVariantCandidate(v, "video/mp4", null, null, null, null, null, null, MediaVariantSource.Dom)).ToArray()));
        }
        return assets;
    }

    // 提取 <tag attr="..."> 中指定属性的值；不做完整 HTML 解析（仅兜底）
    private static List<string> ExtractAttributeValues(string html, string tag, string attribute)
    {
        var results = new List<string>();
        var search = $"<{tag}";
        var attrSearch = $" {attribute}=\"";
        var index = 0;
        while (index < html.Length)
        {
            var tagStart = html.IndexOf(search, index, StringComparison.OrdinalIgnoreCase);
            if (tagStart < 0)
            {
                break;
            }

            var tagEnd = html.IndexOf('>', tagStart);
            if (tagEnd < 0)
            {
                break;
            }

            var segment = html[tagStart..tagEnd];
            var attrStart = segment.IndexOf(attrSearch, StringComparison.OrdinalIgnoreCase);
            if (attrStart >= 0)
            {
                var valueStart = attrStart + attrSearch.Length;
                var valueEnd = segment.IndexOf('"', valueStart);
                if (valueEnd > valueStart)
                {
                    results.Add(segment[valueStart..valueEnd]);
                }
            }

            index = tagEnd + 1;
        }
        return results;
    }
}
