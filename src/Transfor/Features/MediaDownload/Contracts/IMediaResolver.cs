namespace Transfor;

// 平台解析器契约：与协调器统一返回 MediaResolveResult；"需要浏览器"不通过异常表达
internal interface IMediaResolver
{
    MediaProviderId Provider { get; }

    // 无网络 URI 形态判断，真正的安全校验由 SafeHttpRequestSender 执行
    bool CanResolve(Uri sourceUri);

    Task<MediaResolveResult> ResolveAsync(
        MediaResolveRequest request,
        CancellationToken cancellationToken);
}
