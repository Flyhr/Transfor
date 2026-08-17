namespace Transfor;

// Erise 服务器设置（Phase 6.6）：仅存规范化 Origin 与非敏感 UI 设置，
// 禁止 Token/Password 等凭据字段（凭据按 Origin 隔离，由登录层内存持有）
internal sealed record EriseServerSettings(string? ServerOrigin)
{
    public static EriseServerSettings Default { get; } = new((string?)null);

    // 允许的开发 loopback 主机（http 仅限本机，其余远程主机一律要求 https）
    public static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
        // Uri.Host 对 IPv6 返回带括号形式（如 [::1]），两种形式都接受
        || string.Equals(host, "::1", StringComparison.Ordinal)
        || string.Equals(host, "[::1]", StringComparison.Ordinal);

    // 规范化 Origin 纯函数：
    // - 输出 scheme://host[:port]（host 小写，IPv6 带括号，默认端口折叠，无路径/query/fragment/尾斜杠）
    // - 拒绝空/空白、相对 URL、远程 http、userinfo、无 Host、非法端口
    // - 允许远程 https 与开发 loopback（http://localhost / 127.0.0.1 / [::1]，任意端口）
    public static bool TryNormalizeOrigin(string? input, out string? origin, out string? error)
    {
        origin = null;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "请输入服务器地址";
            return false;
        }

        var trimmed = input.Trim();
        Uri? uri;
        try
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out uri))
            {
                error = "地址格式无效（需要完整的 scheme://host 形式）";
                return false;
            }
        }
        catch (Exception)
        {
            // 非法端口等构造异常（如 https://host:99999）统一视为无效
            error = "地址格式无效（端口或格式错误）";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "地址不能包含用户名或密码";
            return false;
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            error = "地址缺少主机名";
            return false;
        }

        var isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isLoopbackHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && IsLoopbackHost(uri.Host);
        if (!isHttps && !isLoopbackHttp)
        {
            error = "仅允许 HTTPS 或本地开发地址（http://localhost / 127.0.0.1 / [::1]）";
            return false;
        }

        var hostPart = uri.Host;
        var portPart = uri.IsDefaultPort ? string.Empty : ":" + uri.Port;
        origin = $"{uri.Scheme.ToLowerInvariant()}://{hostPart}{portPart}";
        return true;
    }
}
