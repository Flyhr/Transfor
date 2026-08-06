namespace Transfor;

// 抖音传输偏好：会话级熔断状态（仅当前进程有效，重启复位）；
// 首次确认 HttpClient 抖音链路被拒（TLS/DNS/RST/EOF/超时）后，
// 本次会话后续解析直接浏览器优先，不再重复撞被拒的握手；
// 不持久化：网络、运营商或抖音边缘策略可能变化
internal enum DouyinTransportPreference
{
    Automatic,
    BrowserPreferred,
}

internal sealed class DouyinTransportPreferenceState
{
    private volatile DouyinTransportPreference current = DouyinTransportPreference.Automatic;

    public bool ShouldUseBrowser => current == DouyinTransportPreference.BrowserPreferred;

    // 记录一次失败；仅可兜底的失败种类触发熔断
    public void RecordFailure(DouyinTransportFailureKind kind)
    {
        if (DouyinTransportClassifier.ShouldUseBrowserFallback(kind))
        {
            current = DouyinTransportPreference.BrowserPreferred;
        }
    }
}
