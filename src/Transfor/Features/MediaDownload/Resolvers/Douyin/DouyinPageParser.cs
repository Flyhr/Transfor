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

    // 直接解析浏览器捕获的结构化 JSON（ExecuteScriptAsync 结果）；
    // 支持纯 JSON 与 URL 编码 JSON 两种形态；
    // JSON null / 非对象输入返回空壳，绝不抛异常（.NET 10 TryGetProperty 对非对象会抛）
    public static DouyinPageData ParseStructuredData(string json)
    {
        foreach (var candidate in DecodeJsonCandidates(json))
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                if (TryParseWork(document.RootElement, out var data))
                {
                    return data;
                }
            }
            catch (JsonException)
            {
                // 该候选不是作品 JSON，继续
            }
            catch (InvalidOperationException)
            {
                // 非对象根元素（JSON null 等）防御兜底，继续下一候选
            }
        }

        return new DouyinPageData(null, null, null, Array.Empty<DouyinAssetCandidate>(), true, false, null);
    }

    // 生成可尝试的 JSON 候选：原样 + URL 解码（真实页面常见 URL 编码形态）
    private static IReadOnlyList<string> DecodeJsonCandidates(string json)
    {
        var candidates = new List<string> { json };
        if (json.StartsWith('{'))
        {
            return candidates;
        }

        try
        {
            var decoded = Uri.UnescapeDataString(json);
            if (decoded.StartsWith('{'))
            {
                candidates.Add(decoded);
            }
        }
        catch (UriFormatException)
        {
            // 忽略解码失败
        }

        return candidates;
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

    // 从作品 JSON 中提取数据；识别 aweme_detail / aweme 结构；
    // 真实抖音 RENDER_DATA 为嵌套结构（如 {"app":{"aweme":{"detail":{"aweme_detail":{...}}}}}），
    // 因此 aweme_detail 做递归查找（该键语义特定，不会误匹配推荐流），aweme 仅根级匹配；
    // 实况图逐个解析 images[i]：静态图（url_list）+ 该图自身 video（play_addr_h264 等）配对；
    // 顶层 video 仅在无 images 时作为普通视频作品解析（图集预览视频不产出）
    private static bool TryParseWork(JsonElement root, out DouyinPageData data)
    {
        data = default!;
        // .NET 10：TryGetProperty 对非 Object 根元素会抛异常（而非返回 false），
        // JSON null / 数组 / 字符串输入必须在此拦截
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!TryFindWorkElement(root, out var detail))
        {
            return false;
        }

        var postId = GetString(detail, "aweme_id");
        var title = GetString(detail, "desc");
        var authorName = detail.TryGetProperty("author", out var author) ? GetString(author, "nickname") : null;

        var assets = new List<DouyinAssetCandidate>();
        var hasImages = detail.TryGetProperty("images", out var images)
            && images.ValueKind == JsonValueKind.Array
            && images.GetArrayLength() > 0;
        var hasVideo = detail.TryGetProperty("video", out var video);

        if (hasImages)
        {
            ParseImageItems(images, assets);
        }
        else if (hasVideo)
        {
            ParseNormalVideo(video, assets);
        }

        if (assets.Count == 0)
        {
            return false;
        }

        data = new DouyinPageData(postId, title, authorName, assets, false, false, null);
        return true;
    }

    // 逐图解析：每张图产出静态照片（url_list）与动态视频（image.video）配对资产；
    // 是否为实况图以存在可播放视频地址为最终依据，live_photo_type/clip_type 仅作辅助
    private static void ParseImageItems(
        JsonElement images,
        List<DouyinAssetCandidate> assets)
    {
        var flatIndex = 0;
        var sourceIndex = 0;

        foreach (var image in images.EnumerateArray())
        {
            var pairId = $"live-{sourceIndex:D3}";

            var stillVariants = CollectImageVariants(image);
            var motionVariants = CollectImageMotionVariants(image);

            // 有真实可播放的动态视频地址才算实况图（音乐链接已被过滤）
            var isLivePhoto = motionVariants.Count > 0;

            if (stillVariants.Count > 0)
            {
                assets.Add(new DouyinAssetCandidate(
                    flatIndex++,
                    MediaKind.Image,
                    stillVariants,
                    sourceIndex,
                    isLivePhoto ? MediaAssetRole.LivePhotoStill : MediaAssetRole.Normal,
                    isLivePhoto ? pairId : null));
            }

            if (motionVariants.Count > 0)
            {
                assets.Add(new DouyinAssetCandidate(
                    flatIndex++,
                    MediaKind.Video,
                    motionVariants,
                    sourceIndex,
                    MediaAssetRole.LivePhotoMotion,
                    pairId));
            }

            sourceIndex++;
        }
    }

    // 收集单张图片的静态照片变体（url_list）
    private static List<DouyinVariantCandidate> CollectImageVariants(JsonElement image)
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
        return variants;
    }

    // 收集单张图片的动态视频变体（image.video）：
    // 优先 H.264（Windows/普通播放器兼容性更好）：play_addr_h264 → play_addr → play_addr_265 → download_addr；
    // 再并入 bit_rate 各档位；去除重复 URL；过滤音乐链接
    private static List<DouyinVariantCandidate> CollectImageMotionVariants(JsonElement image)
    {
        var variants = new List<DouyinVariantCandidate>();

        if (!image.TryGetProperty("video", out var video)
            || video.ValueKind != JsonValueKind.Object)
        {
            return variants;
        }

        var width = GetInt(video, "width");
        var height = GetInt(video, "height");

        foreach (var propertyName in new[]
                 {
                     "play_addr_h264",
                     "play_addr",
                     "play_addr_265",
                     "download_addr",
                 })
        {
            if (video.TryGetProperty(propertyName, out var playAddress))
            {
                CollectUrlList(
                    playAddress,
                    "url_list",
                    "video/mp4",
                    width,
                    height,
                    GetInt(playAddress, "fps"),
                    MediaVariantSource.StructuredData,
                    variants);
            }
        }

        if (video.TryGetProperty("bit_rate", out var bitRates)
            && bitRates.ValueKind == JsonValueKind.Array)
        {
            foreach (var bitRate in bitRates.EnumerateArray())
            {
                if (!bitRate.TryGetProperty("play_addr", out var playAddress))
                {
                    continue;
                }

                CollectUrlList(
                    playAddress,
                    "url_list",
                    "video/mp4",
                    GetInt(bitRate, "width") ?? width,
                    GetInt(bitRate, "height") ?? height,
                    GetInt(playAddress, "fps"),
                    MediaVariantSource.StructuredData,
                    variants,
                    GetInt(bitRate, "bit_rate"));
            }
        }

        return variants
            .Where(v => !IsMusicUrl(v.Url))
            .GroupBy(v => v.Url, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    // 普通视频作品：play_addr / bit_rate（各清晰度档）/ download_addr 为可下载变体；
    // cover 是封面图（JPEG），不得作为视频变体参与下载
    private static void ParseNormalVideo(
        JsonElement video,
        List<DouyinAssetCandidate> assets)
    {
        var variants = new List<DouyinVariantCandidate>();
        var width = GetInt(video, "width") ?? GetNestedInt(video, "play_addr", "width");
        var height = GetInt(video, "height") ?? GetNestedInt(video, "play_addr", "height");
        var fps = GetNestedInt(video, "play_addr", "fps");

        if (video.TryGetProperty("play_addr", out var playAddr))
        {
            CollectUrlList(playAddr, "url_list", "video/mp4", width, height, fps, MediaVariantSource.StructuredData, variants);
        }

        // 高清档位：bit_rate 数组每项含 play_addr（带该档分辨率/码率）
        if (video.TryGetProperty("bit_rate", out var bitRates) && bitRates.ValueKind == JsonValueKind.Array)
        {
            foreach (var bitRate in bitRates.EnumerateArray())
            {
                var bitrateValue = GetInt(bitRate, "bit_rate");
                if (bitRate.TryGetProperty("play_addr", out var bitRatePlayAddr))
                {
                    var bitRateWidth = GetInt(bitRate, "width") ?? GetNestedInt(bitRate, "play_addr", "width");
                    var bitRateHeight = GetInt(bitRate, "height") ?? GetNestedInt(bitRate, "play_addr", "height");
                    CollectUrlList(
                        bitRatePlayAddr, "url_list", "video/mp4",
                        bitRateWidth, bitRateHeight, fps, MediaVariantSource.StructuredData, variants,
                        bitrateValue);
                }
            }
        }

        if (video.TryGetProperty("download_addr", out var downloadAddr))
        {
            CollectUrlList(downloadAddr, "url_list", "video/mp4", width, height, fps, MediaVariantSource.StructuredData, variants);
        }

        if (variants.Count > 0)
        {
            assets.Add(new DouyinAssetCandidate(0, MediaKind.Video, variants));
        }
    }

    // 音乐链接判定：ies-music 目录 / /music/ 路径 / .mp3 扩展名
    private static bool IsMusicUrl(string url)
    {
        var lower = url.ToLowerInvariant();
        return lower.Contains("ies-music", StringComparison.Ordinal)
            || lower.Contains("/music/", StringComparison.Ordinal)
            || lower.EndsWith(".mp3", StringComparison.Ordinal)
            || lower.Contains(".mp3?", StringComparison.Ordinal);
    }

    // 查找作品详情节点：根级 aweme_detail/aweme 优先；
    // 否则递归搜索任意深度的 aweme_detail；最后兜底接受含 aweme_id 的作品对象
    private static bool TryFindWorkElement(JsonElement root, out JsonElement detail)
    {
        if (root.TryGetProperty("aweme_detail", out var rootDetail) || root.TryGetProperty("aweme", out rootDetail))
        {
            detail = rootDetail;
            return true;
        }

        if (TryFindNestedProperty(root, "aweme_detail", out var nestedDetail, depth: 0))
        {
            detail = nestedDetail;
            return true;
        }

        if (TryFindWorkLikeObject(root, out var workLike, depth: 0))
        {
            detail = workLike;
            return true;
        }

        detail = default;
        return false;
    }

    // 递归查找指定属性（深度限制，防止病态结构耗尽栈）
    private static bool TryFindNestedProperty(JsonElement element, string property, out JsonElement found, int depth)
    {
        if (depth > 8)
        {
            found = default;
            return false;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var child in element.EnumerateObject())
            {
                if (string.Equals(child.Name, property, StringComparison.Ordinal)
                    && child.Value.ValueKind == JsonValueKind.Object)
                {
                    found = child.Value;
                    return true;
                }

                if (TryFindNestedProperty(child.Value, property, out found, depth + 1))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindNestedProperty(item, property, out found, depth + 1))
                {
                    return true;
                }
            }
        }

        found = default;
        return false;
    }

    // 兜底：任意含 aweme_id 属性的对象视为作品详情（覆盖路径键直挂详情对象的结构）
    private static bool TryFindWorkLikeObject(JsonElement element, out JsonElement found, int depth)
    {
        if (depth > 8)
        {
            found = default;
            return false;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("aweme_id", out var id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() is { Length: > 0 })
            {
                found = element;
                return true;
            }

            foreach (var child in element.EnumerateObject())
            {
                if (TryFindWorkLikeObject(child.Value, out found, depth + 1))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindWorkLikeObject(item, out found, depth + 1))
                {
                    return true;
                }
            }
        }

        found = default;
        return false;
    }

    private static void CollectUrlList(
        JsonElement container,
        string propertyName,
        string contentType,
        int? width,
        int? height,
        int? fps,
        MediaVariantSource source,
        List<DouyinVariantCandidate> variants,
        long? bitrate = null)
    {
        if (!container.TryGetProperty(propertyName, out var urls) || urls.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var url in urls.EnumerateArray())
        {
            if (url.GetString() is { Length: > 0 } text)
            {
                variants.Add(new DouyinVariantCandidate(text, contentType, width, height, fps, bitrate, null, null, source));
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
