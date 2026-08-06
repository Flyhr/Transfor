using System.Net;
using System.Net.Sockets;

namespace Transfor;

// URI 安全校验：拒绝非 HTTP/HTTPS、localhost、回环、私网、链路本地与解析到禁止范围的域名；
// 这是桌面客户端的最佳努力防护，不宣称构成服务端级 SSRF 安全边界
internal sealed class SafeUriValidator
{
    private readonly IDnsResolver dnsResolver;

    public SafeUriValidator(IDnsResolver dnsResolver)
    {
        this.dnsResolver = dnsResolver ?? throw new ArgumentNullException(nameof(dnsResolver));
    }

    public async Task<UriValidationResult> ValidateAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        if (uri.Scheme is not ("http" or "https"))
        {
            return new UriValidationResult(false, $"不支持的协议：{uri.Scheme}");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return new UriValidationResult(false, "URI 不允许包含用户信息。");
        }

        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return new UriValidationResult(false, "禁止访问 localhost。");
        }

        // IP 字面量：直接按地址范围检查，不查询 DNS
        if (IPAddress.TryParse(host, out var literal))
        {
            return IsAddressAllowed(literal)
                ? new UriValidationResult(true, null)
                : new UriValidationResult(false, "目标地址属于禁止访问的网络范围。");
        }

        // 域名：解析后逐地址检查
        IPAddress[] addresses;
        try
        {
            addresses = await dnsResolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return new UriValidationResult(false, "域名解析失败。", UriValidationKind.DnsFailed);
        }

        if (addresses.Length == 0)
        {
            return new UriValidationResult(false, "域名没有解析结果。", UriValidationKind.DnsFailed);
        }

        foreach (var address in addresses)
        {
            if (!IsAddressAllowed(address))
            {
                return new UriValidationResult(false, "域名解析到禁止访问的网络范围。");
            }
        }

        return new UriValidationResult(true, null);
    }

    private static bool IsAddressAllowed(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
        {
            return false;
        }

        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            // 0.0.0.0/8、127.0.0.0/8（回环）
            if (bytes[0] is 0 or 127) return false;
            // 10.0.0.0/8
            if (bytes[0] == 10) return false;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) return false;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return false;
            // 169.254.0.0/16（链路本地）
            if (bytes[0] == 169 && bytes[1] == 254) return false;
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            // fc00::/7（唯一本地地址）
            if ((bytes[0] & 0xFE) == 0xFC) return false;
        }

        return true;
    }
}
