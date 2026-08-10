namespace Transfor;

// 解析器注册中心：按注册顺序返回所有 CanResolve 匹配的解析器；
// 允许同 Provider 多解析器并存（计划最终解析链：
// DirectResolver → BrowserResolver → NetworkResolver，以及未来各平台的 Direct/Browser/Network 三兄弟），
// 由 Coordinator 依次尝试形成多级 fallback
internal sealed class MediaResolverRegistry
{
    private readonly IReadOnlyList<IMediaResolver> resolvers;

    public MediaResolverRegistry(IReadOnlyList<IMediaResolver> resolvers)
    {
        ArgumentNullException.ThrowIfNull(resolvers);
        foreach (var resolver in resolvers)
        {
            ArgumentNullException.ThrowIfNull(resolver);
        }
        this.resolvers = resolvers;
    }

    // 返回所有 CanResolve 匹配的解析器（注册顺序）；CanResolve 只做无网络的 URI 形态判断
    public IReadOnlyList<IMediaResolver> GetResolvers(Uri sourceUri)
    {
        ArgumentNullException.ThrowIfNull(sourceUri);
        var matched = new List<IMediaResolver>();
        foreach (var candidate in resolvers)
        {
            if (candidate.CanResolve(sourceUri))
            {
                matched.Add(candidate);
            }
        }

        return matched;
    }
}
