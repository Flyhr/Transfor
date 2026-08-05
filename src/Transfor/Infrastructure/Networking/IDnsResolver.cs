using System.Net;

namespace Transfor;

// DNS 解析抽象：测试注入 Fake，禁止真实 DNS 查询
internal interface IDnsResolver
{
    Task<IPAddress[]> ResolveAsync(
        string host,
        CancellationToken cancellationToken);
}
