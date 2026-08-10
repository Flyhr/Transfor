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
        // 统一解析并校验全部参数：任一非法 → 抛错（响应 error），不落盘；
        // 全部合法 → 一次性 UpdateSettings（原子写入，任一写失败整体回滚）
        var next = stateStore.Settings;

        var channelText = request.GetString("updateChannel");
        if (!string.IsNullOrWhiteSpace(channelText))
        {
            if (!Enum.TryParse<UpdateChannel>(channelText, true, out var channel) || !Enum.IsDefined(channel))
            {
                throw new ArgumentException($"非法更新通道：{channelText}");
            }

            next = next with { UpdateChannel = channel };
        }

        var quoteText = request.GetString("quoteHistoryLimit");
        if (!string.IsNullOrWhiteSpace(quoteText))
        {
            if (!int.TryParse(quoteText, out var quote) || quote is < 1 or > 500)
            {
                throw new ArgumentException($"非法引号转换历史上限：{quoteText}");
            }

            next = next with { QuoteHistoryLimit = quote };
        }

        var spaceText = request.GetString("spaceHistoryLimit");
        if (!string.IsNullOrWhiteSpace(spaceText))
        {
            if (!int.TryParse(spaceText, out var space) || space is < 1 or > 500)
            {
                throw new ArgumentException($"非法去除空格历史上限：{spaceText}");
            }

            next = next with { SpaceHistoryLimit = space };
        }

        stateStore.UpdateSettings(next);
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
