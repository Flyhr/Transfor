namespace Transfor;

// 解析器注册中心：拒绝重复 Provider，按注册顺序（专用解析器优先、Direct 兜底）选择
internal sealed class MediaResolverRegistry
{
    private readonly IReadOnlyList<IMediaResolver> resolvers;

    public MediaResolverRegistry(IReadOnlyList<IMediaResolver> resolvers)
    {
        ArgumentNullException.ThrowIfNull(resolvers);
        var providers = new HashSet<MediaProviderId>();
        foreach (var resolver in resolvers)
        {
            ArgumentNullException.ThrowIfNull(resolver);
            if (!providers.Add(resolver.Provider))
            {
                throw new ArgumentException($"重复的解析器提供方：{resolver.Provider}。", nameof(resolvers));
            }
        }
        this.resolvers = resolvers;
    }

    // 返回第一个 CanResolve 匹配的解析器；CanResolve 只做无网络的 URI 形态判断
    public bool TryGetResolver(Uri sourceUri, out IMediaResolver? resolver)
    {
        ArgumentNullException.ThrowIfNull(sourceUri);
        foreach (var candidate in resolvers)
        {
            if (candidate.CanResolve(sourceUri))
            {
                resolver = candidate;
                return true;
            }
        }

        resolver = null;
        return false;
    }
}
