namespace Transfor;

// 解析协调器：对匹配的解析器按注册顺序依次尝试（多级 fallback）；
// 成功 / 需要交互 / 不支持 → 终止链（交互结果不应被后续解析器覆盖）；
// 失败 → 继续下一个；全部失败返回最后一个失败结果；
// 不引用任何具体平台解析器或浏览器实现
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
        var resolvers = registry.GetResolvers(request.SourceUri);
        if (resolvers.Count == 0)
        {
            return MediaResolveResult.Unsupported("暂不支持该链接。");
        }

        MediaResolveResult? lastFailure = null;
        foreach (var resolver in resolvers)
        {
            MediaResolveResult result;
            try
            {
                result = await resolver.ResolveAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 调用方取消：保持取消语义，不转换为 Failed
                throw;
            }
            catch (Exception ex)
            {
                // 保留完整异常链，便于定位 TLS/DNS 等传输层问题；作为该级失败继续下一级
                result = MediaResolveResult.Failure(ErrorChainFormatter.Format(ex));
            }

            // 成功 / 需要交互 / 不支持：终止 fallback 链
            if (result.Status is MediaResolveStatus.Succeeded
                or MediaResolveStatus.RequiresUserInteraction
                or MediaResolveStatus.Unsupported)
            {
                return result;
            }

            lastFailure = result;
        }

        return lastFailure ?? MediaResolveResult.Failure("解析失败。");
    }
}
