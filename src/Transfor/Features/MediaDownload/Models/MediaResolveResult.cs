namespace Transfor;

// 统一解析结果：使用单一状态枚举表达所有结果，"需要浏览器"是正常业务状态；
// 该类型是运行时结果，不参与 JSON 持久化
internal sealed record MediaResolveResult
{
    private MediaResolveResult(
        MediaResolveStatus status,
        ResolvedMediaPost? post,
        string? message)
    {
        Status = status;
        Post = post;
        Message = message;
    }

    public MediaResolveStatus Status { get; }
    public ResolvedMediaPost? Post { get; }
    public string? Message { get; }

    public static MediaResolveResult Success(ResolvedMediaPost post)
    {
        ArgumentNullException.ThrowIfNull(post);
        ValidatePost(post);
        return new(MediaResolveStatus.Succeeded, post, null);
    }

    public static MediaResolveResult RequiresUserInteraction(string message) =>
        new(MediaResolveStatus.RequiresUserInteraction, null, message);

    public static MediaResolveResult Unsupported(string message) =>
        new(MediaResolveStatus.Unsupported, null, message);

    public static MediaResolveResult Failure(string message) =>
        new(MediaResolveStatus.Failed, null, message);

    // 校验成功结果的可下载性：拒绝空资产、空变体与非 HTTP/HTTPS 变体 URI
    private static void ValidatePost(ResolvedMediaPost post)
    {
        if (post.Assets.Count == 0)
            throw new InvalidDataException("作品没有可下载资产。");

        foreach (var asset in post.Assets)
        {
            if (asset.Variants.Count == 0)
                throw new InvalidDataException("资产没有可下载变体。");

            foreach (var variant in asset.Variants)
            {
                if (variant.Uri.Scheme is not ("http" or "https"))
                    throw new InvalidDataException("媒体 URI 协议不安全。");
            }
        }
    }
}
