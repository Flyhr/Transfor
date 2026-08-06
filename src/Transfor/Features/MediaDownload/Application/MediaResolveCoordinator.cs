namespace Transfor;

// 解析协调器：只负责选择解析器与统一错误边界，不引用任何具体平台解析器或浏览器实现
internal sealed class MediaResolveCoordinator
{
    private readonly MediaResolverRegistry registry;

    public MediaResolveCoordinator(MediaResolverRegistry registry)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public async Task<MediaResolveResult> ResolveAsync(
        MediaResolveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 无匹配解析器：Unsupported 是正常业务结果，不抛异常
        if (!registry.TryGetResolver(request.SourceUri, out var resolver))
        {
            return MediaResolveResult.Unsupported("暂不支持该链接。");
        }

        try
        {
            return await resolver!.ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方取消：保持取消语义，不转换为 Failed
            throw;
        }
        catch (Exception ex)
        {
            // 保留完整异常链，便于定位 TLS/DNS 等传输层问题
            return MediaResolveResult.Failure(ErrorChainFormatter.Format(ex));
        }
    }
}
