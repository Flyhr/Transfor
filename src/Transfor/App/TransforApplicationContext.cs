using Microsoft.Web.WebView2.Core;

namespace Transfor;

// 应用级上下文：持有现代界面、历史面板、托盘图标与全局热键，并协调它们之间的跳转。
internal sealed class TransforApplicationContext : ApplicationContext
{
    private readonly TextStateStore historyStore;
    private readonly AppServices services;
    private readonly UpdateService updatesService;
    private readonly HistoryPanelForm historyPanel;
    private readonly GlobalHotKeyManager hotKeyManager;
    private readonly NotifyIcon trayIcon;

    // 是否正在退出（防止重复触发退出流程）
    private bool exiting;

    // 更新检查进行中（防止托盘多次触发并发检查）
    private bool updateCheckRunning;

    // 现代界面是唯一主界面；Runtime 缺失时保持 null，托盘仍可检查更新或退出。
    private AppShellForm? appShell;
    private AppShellForm? appShellAllowedToClose;

    public TransforApplicationContext(AppServices services)
    {
        this.services = services;
        historyStore = services.State;
        hotKeyManager = services.HotKeys;
        updatesService = services.Updates;

        historyPanel = new HistoryPanelForm(
            historyStore,
            services.PasteCoordinator);

        // 创建系统托盘图标：主界面隐藏后进程驻留托盘。
        trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Transfor",
            Visible = true,
            ContextMenuStrip = CreateTrayMenu(),
        };
        trayIcon.DoubleClick += (_, _) => ShowAppShell();
        // 按下全局快捷键时，在鼠标附近呼出历史面板
        hotKeyManager.HotKeyPressed += (_, _) => ShowHistoryPanel();

        CheckWebView2Runtime();
        if (webView2Available)
        {
            InitializeAppShellAndBrowser();
        }
        RegisterSavedHotKey();
        // 延迟显示现代界面：先完成启动更新检查；Runtime 缺失时仅保留托盘能力。
        _ = ShowAfterStartupCheckAsync();
    }

    private void InitializeAppShellAndBrowser()
    {
        appShell = CreateAppShell();
        _ = appShell.Handle;
        services.Browser.UiAnchor = appShell;

        try
        {
            services.Browser.EnsureHostCoreAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception hostEx)
        {
            var category = ErrorClassifier.Classify(hostEx, ErrorCategory.Browser);
            AppLog.Browser.Warn($"[{category}] 浏览器隐藏宿主初始化失败：{hostEx.Message}");
        }

        try
        {
            services.Media.EnsureBrowserInitializedAsync(appShell).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            var category = ErrorClassifier.Classify(ex, ErrorCategory.Browser);
            AppLog.Browser.Error($"[{category}] 启动浏览器会话挂接失败：{ErrorChainFormatter.Format(ex)}");
        }
    }

    // 启动流程：后台检查更新（最多等待 timeout）；强制更新 → 不显示主窗体直接进入阻断循环；
    // 检查完成/超时/失败 → 正常显示主窗体；可选更新在显示后弹提示
    private async Task ShowAfterStartupCheckAsync()
    {
        var (result, timedOut) = await WaitStartupCheckAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        var status = result?.Status ?? UpdateStatus.CheckFailed;
        if (appShell is { IsDisposed: true })
        {
            return;
        }

        if (!timedOut && status == UpdateStatus.RequiredUpdate)
        {
            // 强制更新：仅在用户重新检查确认不再强制时才能进入业务界面；
            // 退出或更新重启都不允许回落到主界面。
            var requiredUpdateResolved = await RunRequiredUpdateLoopAsync(result!).ConfigureAwait(true);
            if (!AppShellLifecyclePolicy.ShouldShowStartupInterface(
                    status,
                    timedOut,
                    requiredUpdateResolved,
                    appShell is { IsDisposed: true },
                    exiting))
            {
                return;
            }

            ShowStartupInterface();
            return;
        }

        if (result is not null && !timedOut)
        {
            await HandleUpdateResultAsync(result, manual: false).ConfigureAwait(true);
        }

        if (AppShellLifecyclePolicy.ShouldShowStartupInterface(
                status,
                timedOut,
                requiredUpdateResolved: false,
                appShell is { IsDisposed: true },
                exiting))
        {
            ShowStartupInterface();
        }
    }

    private void ShowStartupInterface()
    {
        if (webView2Available)
        {
            ShowAppShell();
        }
    }

    // 启动更新检查（最多等待 timeout）：网络缓慢/失败按正常启动处理（用户可手动检查）
    private async Task<(UpdateCheckResult? Result, bool TimedOut)> WaitStartupCheckAsync(TimeSpan timeout)
    {
        var checkTask = Task.Run(() => updatesService.CheckForUpdatesAsync(CurrentUpdateChannel, CancellationToken.None));
        var completed = await Task.WhenAny(checkTask, Task.Delay(timeout)).ConfigureAwait(true);
        if (completed != checkTask)
        {
            // 超时后检查任务仍可能失败：观察异常避免未观察任务异常
            _ = checkTask.ContinueWith(
                t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return (null, true);
        }

        try
        {
            return (checkTask.Result, false);
        }
        catch
        {
            // 启动检查失败按正常启动处理（不打扰用户，可手动检查）
            return (null, false);
        }
    }

    // 构建托盘右键菜单：新界面 / 检查更新 / 退出。
    private ContextMenuStrip CreateTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("新界面", null, (_, _) => ShowAppShell());
        menu.Items.Add("检查更新", null, (_, _) => _ = CheckAndPromptAsync(manual: true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());
        return menu;
    }

    // 注册设置中保存的全局快捷键；若该组合被其他程序占用，则回退到默认 Alt+Q 并持久化
    private void RegisterSavedHotKey()
    {
        if (hotKeyManager.TryRegister(historyStore.Settings.HistoryHotKey, out var error))
        {
            return;
        }

        var message = error;
        if (historyStore.Settings.HistoryHotKey != HotKeyBinding.Default
            && hotKeyManager.TryRegister(HotKeyBinding.Default, out var defaultError))
        {
            message += $"已回退为默认快捷键 Alt+Q。{defaultError}";
            try
            {
                historyStore.UpdateSettings(historyStore.Settings with { HistoryHotKey = HotKeyBinding.Default });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                message += $"默认快捷键状态保存失败：{ex.Message}";
            }
        }
        trayIcon.ShowBalloonTip(5000, "Transfor", message, ToolTipIcon.Warning);
    }

    // WebView2 Runtime 启动检查：缺失时不构造现代界面或初始化浏览器，仅保留托盘更新/退出能力。
    private bool webView2Available = true;

    private void CheckWebView2Runtime()
    {
        try
        {
            if (CoreWebView2Environment.GetAvailableBrowserVersionString() is not null)
            {
                return;
            }
        }
        catch
        {
            // Runtime 未安装时 GetAvailableBrowserVersionString 抛异常，视为缺失
        }

        webView2Available = false;
        AppLog.Browser.Warn("未检测到 WebView2 Runtime：主界面不可用");
        trayIcon.ShowBalloonTip(
            5000,
            "Transfor",
            "未检测到 WebView2 Runtime：请安装后再使用主界面。",
            ToolTipIcon.Warning);
    }

    // 打开现代界面宿主：Web UI + App Bridge；独立 Profile 与互联网浏览器隔离。
    private void ShowAppShell()
    {
        if (!webView2Available)
        {
            return;
        }

        if (appShell is null || appShell.IsDisposed)
        {
            appShell = CreateAppShell();
            _ = appShell.Handle;
        }

        // 若新界面被意外释放后重建，必须立即恢复浏览器 UI 锚点。
        services.Browser.UiAnchor = appShell;

        appShell.Show();
        appShell.Activate();
    }

    private AppShellForm CreateAppShell()
    {
        // 局部捕获：Bridge 的惰性初始化委托必须引用本次创建的 shell。
        AppShellForm shell = null!;
        shell = new AppShellForm(
            new AppBridge(
                historyStore,
                updatesService,
                services.Media.ResolveCoordinator,
                services.Media.DownloadCoordinator,
                services.Media.State,
                services.Media.Preview,
                new MediaSizeProbe(services.Media.RequestSender, services.Media.BrowserSessions))
            {
                HotKeyManager = services.HotKeys,
                EnsureBrowserInitialized = () => services.Media.EnsureBrowserInitializedAsync(shell),
            },
            services.Browser,
            AppPaths.Default.AppUiProfileDirectory,
            services.Media.DownloadCoordinator);
        shell.InitializationFailed += AppShell_InitializationFailed;
        shell.FormClosing += AppShell_FormClosing;
        return shell;
    }

    private void AppShell_InitializationFailed(object? sender, EventArgs e)
    {
        if (sender is not AppShellForm failedShell || !ReferenceEquals(failedShell, appShell))
        {
            return;
        }

        appShellAllowedToClose = failedShell;
    }

    // 用户关闭主界面时仅隐藏到托盘；显式「退出」会先设置 exiting，允许真正释放窗体。
    private void AppShell_FormClosing(object? sender, FormClosingEventArgs e)
    {
        // AppShell 初始化失败会在 Close 前显式授权本次程序性关闭，
        // 不能仅依赖 CloseReason（Form.Close 也可能报告为 UserClosing）。
        var initializationFailureClose = ReferenceEquals(sender, appShellAllowedToClose);
        if (initializationFailureClose)
        {
            appShellAllowedToClose = null;
        }

        var decision = AppShellLifecyclePolicy.DecideClose(
            initializationFailureClose,
            exiting,
            e.CloseReason == CloseReason.UserClosing);
        if (decision == AppShellCloseDecision.AllowClose)
        {
            return;
        }

        e.Cancel = true;
        if (appShell is { IsDisposed: false })
        {
            appShell.Hide();
        }
    }

    // 呼出历史面板：记录当前前台窗口句柄，作为稍后粘贴的目标
    private void ShowHistoryPanel()
    {
        var targetWindow = WindowsNative.GetForegroundWindow();
        historyPanel.ShowFor(targetWindow);
    }

    // 退出应用：有活动任务时先确认，再取消任务并等待落定；
    // 主窗体仍存活时释放服务，最后关闭窗口与托盘；
    // 释放异常转换为可见错误，不遗留半退出状态
    private async void ExitApplication()
    {
        if (exiting)
        {
            return;
        }

        exiting = true;

        // 退出确认放在 try/finally 之前：用户选择「否」直接返回，
        // 不进入释放与 ExitThread 序列（避免 finally 无条件退出）
        if (services.Media.DownloadCoordinator.HasActiveTasks)
        {
            var confirm = MessageBox.Show(DialogOwner, "仍有下载任务进行中，确定要退出并取消任务吗？", "退出确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes)
            {
                exiting = false;
                return;
            }

            await services.Media.DownloadCoordinator.CancelAllAsync();
        }

        try
        {
            // 先关闭新界面宿主（WebView2 进程随窗体释放），再释放服务
            appShell?.Close();
            appShell = null;
            // 释放全部服务：媒体先取消下载，浏览器宿主最后释放
            await services.DisposeAsync();
            historyPanel.CloseForExit();
        }
        catch (Exception ex)
        {
            MessageBox.Show(DialogOwner, $"退出时发生错误：{ex.Message}", "退出错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            trayIcon.Visible = false;
            trayIcon.Dispose();
            ExitThread();
        }
    }

    // 检查并提示更新：检查在后台执行，提示/下载/重启编排回到 UI 线程；
    // 任何网络失败都静默（手动检查时给出提示），绝不阻断应用使用
    private async Task CheckAndPromptAsync(bool manual)
    {
        if (updateCheckRunning)
        {
            return;
        }

        updateCheckRunning = true;
        try
        {
            UpdateCheckResult result;
            try
            {
                result = await Task.Run(() => updatesService.CheckForUpdatesAsync(CurrentUpdateChannel, CancellationToken.None))
                    .ConfigureAwait(true);
            }
            catch
            {
                if (manual)
                {
                    ShowNotice("检查更新失败，请稍后重试。", MessageBoxIcon.Warning);
                }
                return;
            }

            if (exiting)
            {
                return;
            }

            await HandleUpdateResultAsync(result, manual).ConfigureAwait(true);
        }
        finally
        {
            updateCheckRunning = false;
        }
    }

    // 当前更新通道（设置中可切换，实时生效）
    private UpdateChannel CurrentUpdateChannel => historyStore.Settings.UpdateChannel;

    // UI 线程：按检查结果处理（可选更新提示一次；强制更新进入阻断循环）
    private async Task HandleUpdateResultAsync(UpdateCheckResult result, bool manual)
    {
        if (manual && result.Status is UpdateStatus.UpToDate or UpdateStatus.CheckFailed)
        {
            var message = result.Status == UpdateStatus.UpToDate ? "当前已是最新版本。" : $"检查更新失败：{result.Error}";
            ShowNotice(message, MessageBoxIcon.Information);
            return;
        }

        switch (result.Status)
        {
            case UpdateStatus.OptionalUpdate:
                var action = UpdateNoticeForm.ShowOptional(DialogFormOwner, result);
                if (action == UpdateNoticeForm.UserAction.UpdateNow)
                {
                    await RunDownloadFlowAsync(result, required: false);
                }
                break;

            case UpdateStatus.RequiredUpdate:
                await RunRequiredUpdateLoopAsync(result);
                break;
        }
    }

    // 强制更新阻断循环：退出 → 退出应用；立即更新 → 下载（完成后必须重启）；重新检查 → 重新判定
    private async Task<bool> RunRequiredUpdateLoopAsync(UpdateCheckResult result)
    {
        while (!exiting && appShell is not { IsDisposed: true })
        {
            var action = UpdateNoticeForm.ShowRequired(DialogFormOwner, result);
            switch (action)
            {
                case UpdateNoticeForm.UserAction.Exit:
                    ExitApplication();
                    return false;

                case UpdateNoticeForm.UserAction.UpdateNow:
                    // 强制更新状态机：Restarting 结束；取消/失败都不允许跳过——
                    // 取消重新显示强制更新框；失败提示后回到强制框（重试或退出）
                    var flow = await RunDownloadFlowAsync(result, required: true);
                    switch (flow)
                    {
                        case UpdateDownloadForm.Result.RestartNow:
                            return false;

                        case UpdateDownloadForm.Result.Failed:
                            ShowNotice("更新下载失败，请重试或选择退出后手动更新。", MessageBoxIcon.Error);
                            continue;

                        case UpdateDownloadForm.Result.Cancelled:
                            ShowNotice("更新下载已取消，必须更新后才能继续使用。", MessageBoxIcon.Warning);
                            continue;

                        default:
                            return false;
                    }

                case UpdateNoticeForm.UserAction.Recheck:
                    var rechecked = await Task.Run(() => updatesService.CheckForUpdatesAsync(CurrentUpdateChannel, CancellationToken.None))
                        .ConfigureAwait(true);
                    if (rechecked.Status == UpdateStatus.RequiredUpdate)
                    {
                        result = rechecked;
                        continue;
                    }

                    await HandleUpdateResultAsync(rechecked, manual: false);
                    return !exiting && appShell is not { IsDisposed: true };

                default:
                    return false;
            }
        }

        return false;
    }

    // 下载流程：进度窗体 → 下载 → 重启提示；强制更新下载完成直接重启；
    // 「稍后重启」的更新已暂存，下次启动时由 Velopack 自动应用；
    // 返回结果供强制更新循环决策（取消/失败不得跳过强制更新）
    private async Task<UpdateDownloadForm.Result> RunDownloadFlowAsync(UpdateCheckResult result, bool required)
    {
        var installer = services.UpdateInstallerFactory(CurrentUpdateChannel);
        try
        {
            var action = await UpdateDownloadForm.RunAsync(DialogFormOwner, installer, result, required);
            switch (action)
            {
                case UpdateDownloadForm.Result.RestartNow:
                    // 应用更新并重启为新版本；成功后进程退出，应用不再返回
                    installer.ApplyAndRestart();
                    break;

                case UpdateDownloadForm.Result.Later:
                    break;

                case UpdateDownloadForm.Result.Cancelled:
                    if (!required)
                    {
                        ShowNotice("更新下载已取消。", MessageBoxIcon.Information);
                    }
                    break;

                case UpdateDownloadForm.Result.Failed:
                    break;
            }

            return action;
        }
        catch (Exception ex)
        {
            ShowNotice($"更新失败：{ex.Message}", MessageBoxIcon.Error);
            return UpdateDownloadForm.Result.Failed;
        }
        finally
        {
            installer.Dispose();
        }
    }

    private IWin32Window? DialogOwner => appShell is { IsDisposed: false, Visible: true } ? appShell : null;

    private Form? DialogFormOwner => appShell is { IsDisposed: false, Visible: true } ? appShell : null;

    private void ShowNotice(string message, MessageBoxIcon icon) =>
        MessageBox.Show(DialogOwner, message, "更新", MessageBoxButtons.OK, icon);
}
