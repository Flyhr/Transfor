using System.Net;

namespace Transfor;

// 系统 DNS 解析实现（生产环境使用）
internal sealed class SystemDnsResolver : IDnsResolver
{
    public async Task<IPAddress[]> ResolveAsync(
        string host,
        CancellationToken cancellationToken)
    {
        return await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
    }
}
