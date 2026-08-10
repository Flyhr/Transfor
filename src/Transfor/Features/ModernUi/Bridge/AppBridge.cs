namespace Transfor;

// App Bridge（Phase 5B）：处理 Web UI 发来的 JSON 消息并调用应用服务；
// 方法分发为纯逻辑（可离线测试），WebView2 消息桥接由 AppShellForm 负责；
// Phase 5 仅暴露只读信息与设置（媒体解析/下载等能力 Phase 6 随页面迁移接入）
internal sealed class AppBridge
{
    private readonly TextStateStore stateStore;
    private readonly IUpdateService updateService;

    public AppBridge(TextStateStore stateStore, IUpdateService updateService)
    {
        this.stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        this.updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
    }

    // 处理一条请求 JSON，返回应回发的响应 JSON；非法消息返回 null；
    // checkUpdate 涉及网络，整体异步（不阻塞 UI 线程）
    public async Task<string?> HandleAsync(string requestJson)
    {
        var request = AppBridgeProtocol.ParseRequest(requestJson);
        if (request is null)
        {
            return null;
        }

        try
        {
            return request.Method switch
            {
                "getAppInfo" => AppBridgeProtocol.CreateSuccessResponse(request.Id, GetAppInfo()),
                "getSettings" => AppBridgeProtocol.CreateSuccessResponse(request.Id, GetSettings()),
                "saveSettings" => AppBridgeProtocol.CreateSuccessResponse(request.Id, SaveSettings(request)),
                "checkUpdate" => await CheckUpdateAsync(request).ConfigureAwait(false),
                _ => AppBridgeProtocol.CreateErrorResponse(request.Id, $"未知方法：{request.Method}"),
            };
        }
        catch (Exception ex)
        {
            return AppBridgeProtocol.CreateErrorResponse(request.Id, ex.Message);
        }
    }

    private object GetAppInfo() => new
    {
        version = AppVersion.Current,
        channel = stateStore.Settings.UpdateChannel.ToString().ToLowerInvariant(),
    };

    private object GetSettings() => new
    {
        updateChannel = stateStore.Settings.UpdateChannel.ToString().ToLowerInvariant(),
        quoteHistoryLimit = stateStore.Settings.QuoteHistoryLimit,
        spaceHistoryLimit = stateStore.Settings.SpaceHistoryLimit,
    };

    private object SaveSettings(BridgeRequest request)
    {
        // Phase 5 仅开放更新通道与历史上限（逐字段校验，非法值拒绝）
        var channelText = request.GetString("updateChannel");
        if (!string.IsNullOrWhiteSpace(channelText)
            && Enum.TryParse<UpdateChannel>(channelText, true, out var channel)
            && Enum.IsDefined(channel))
        {
            stateStore.UpdateSettings(stateStore.Settings with { UpdateChannel = channel });
        }

        var quoteLimit = request.Parameters.HasValue && int.TryParse(request.GetString("quoteHistoryLimit"), out var quote) && quote is >= 1 and <= 500
            ? quote
            : (int?)null;
        if (quoteLimit is not null)
        {
            stateStore.UpdateSettings(stateStore.Settings with { QuoteHistoryLimit = quoteLimit.Value });
        }

        return GetSettings();
    }

    private async Task<string> CheckUpdateAsync(BridgeRequest request)
    {
        var result = await updateService.CheckForUpdatesAsync(stateStore.Settings.UpdateChannel, CancellationToken.None)
            .ConfigureAwait(false);
        var status = result.Status switch
        {
            UpdateStatus.UpToDate => "upToDate",
            UpdateStatus.OptionalUpdate => "optionalUpdate",
            UpdateStatus.RequiredUpdate => "requiredUpdate",
            UpdateStatus.CheckFailed => "checkFailed",
            UpdateStatus.Disabled => "disabled",
            _ => "unknown",
        };
        return AppBridgeProtocol.CreateSuccessResponse(request.Id, new { status, version = AppVersion.Current });
    }
}
