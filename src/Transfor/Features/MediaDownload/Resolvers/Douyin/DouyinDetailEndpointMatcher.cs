namespace Transfor;

// 抖音作品详情接口匹配器：识别 CDP Network 事件中的作品详情 XHR/Fetch 响应。
// 真实桌面页作品数据（含图文 images 数组）由登录态详情接口异步加载，
// 页面脚本中往往没有完整数据，因此需要从网络层捕获
internal static class DouyinDetailEndpointMatcher
{
    public static bool IsDetailEndpoint(string url, string? resourceType)
    {
        // 只认 XHR/Fetch 响应（页面文档/图片/样式等不可能是详情 JSON）；
        // type 缺失时放行，交由路径特征判断（aweme/detail 是专有路径，不会误判）
        if (resourceType is not (null or "XHR" or "Fetch"))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!DouyinHttpPageResolver.IsDouyinPageHost(uri.Host))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        // 详情接口路径特征：/aweme/v1/web/aweme/detail/ 等
        if (path.Contains("aweme/detail", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 路径含 detail 且带 aweme_id 查询参数（接口路径变体）
        if (path.Contains("detail", StringComparison.OrdinalIgnoreCase)
            && uri.Query.Contains("aweme_id=", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
