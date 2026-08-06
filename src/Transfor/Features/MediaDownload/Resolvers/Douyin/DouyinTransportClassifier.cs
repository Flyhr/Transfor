namespace Transfor;

// 抖音传输失败类别：按结构化异常分类，不依赖异常消息字符串
internal enum DouyinTransportFailureKind
{
    None,
    DnsFailure,
    TlsHandshakeRejected,
    ConnectionReset,
    ResponseEnded,
    Timeout,
    SecurityPolicyRejected,
    Unknown,
}

// 传输失败分类器：把网络层异常归类为可决策的失败种类；
// 其中 TLS 握手被拒 / DNS 失败 / 连接重置 / EOF / 超时 属于可进入浏览器兜底的类型
internal static class DouyinTransportClassifier
{
    public static DouyinTransportFailureKind Classify(Exception exception)
    {
        if (exception is UriValidationException validation)
        {
            return validation.Kind == UriValidationKind.DnsFailed
                ? DouyinTransportFailureKind.DnsFailure
                : DouyinTransportFailureKind.SecurityPolicyRejected;
        }

        if (exception is HttpRequestException http)
        {
            return http.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => DouyinTransportFailureKind.DnsFailure,
                HttpRequestError.SecureConnectionError => DouyinTransportFailureKind.TlsHandshakeRejected,
                HttpRequestError.ConnectionError => DouyinTransportFailureKind.ConnectionReset,
                HttpRequestError.ResponseEnded => DouyinTransportFailureKind.ResponseEnded,
                HttpRequestError.ProxyTunnelError => DouyinTransportFailureKind.ConnectionReset,
                _ => DouyinTransportFailureKind.Unknown,
            };
        }

        // 超时（HttpClient.Timeout 或连接挂起）：归类为可兜底类型，避免长时间等待
        if (exception is TaskCanceledException or TimeoutException)
        {
            return DouyinTransportFailureKind.Timeout;
        }

        if (exception is IOException)
        {
            return DouyinTransportFailureKind.ResponseEnded;
        }

        return DouyinTransportFailureKind.Unknown;
    }

    // 可进入浏览器兜底的失败种类
    public static bool ShouldUseBrowserFallback(DouyinTransportFailureKind kind)
    {
        return kind is DouyinTransportFailureKind.DnsFailure
            or DouyinTransportFailureKind.TlsHandshakeRejected
            or DouyinTransportFailureKind.ConnectionReset
            or DouyinTransportFailureKind.ResponseEnded
            or DouyinTransportFailureKind.Timeout;
    }
}
