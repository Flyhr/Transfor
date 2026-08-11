using Microsoft.Web.WebView2.Core;

namespace Transfor;

// 应用级上下文：持有主窗口、历史面板、托盘图标与全局热键，并协调它们之间的跳转
internal sealed class TransforApplicationContext : ApplicationContext
{
    private readonly TextStateStore historyStore;
    private readonly AppServices services;
    private readonly UpdateService updatesService;
    private readonly MainForm mainForm;
    private readonly HistoryPanelForm historyPanel;
    private readonly GlobalHotKeyManager hotKeyManager;
    private readonly NotifyIcon trayIcon;

    // 启动时注册快捷键失败的错误信息，待主窗口首次显示后弹窗提示
    private string? startupHotKeyError;

    // 是否正在退出（防止重复触发退出流程）
    private bool exiting;

    // 更新检查进行中（防止托盘多次触发并发检查）
    private bool updateCheckRunning;

    // 新界面宿主（Phase 5 预览）：首次打开创建，关闭后仍可再次打开
    private AppShellForm? appShell;

    public TransforApplicationContext(AppServices services)
    {
        this.services = services;
        historyStore = services.State;
        hotKeyManager = services.HotKeys;
        updatesService = services.Updates;
        // 由页面集合构造主窗口外壳：文本转换 + 媒体下载 + 浏览器
        var pages = new IFeaturePage[]
        {
            new TextToolsPage(services.State),
            new MediaDownloadPage(
                services.Media.ResolveCoordinator,
                services.Media.DownloadCoordinator,
                services.Media.State,
                services.Media.EnsureBrowserInitializedAsync,
                services.Media.Preview),
            new BrowserView(services.Browser),
        };
        mainForm = new MainForm(pages);
        historyPanel = new HistoryPanelForm(
            historyStore,
            services.PasteCoordinator);

        // 启动即挂接浏览器会话（只挂接不启动，首次使用时惰性启动专用 Edge），
        // Automatic 解析的浏览器兜底无需用户先点击「打开真实 Edge 登录」
        try
        {
            services.Media.EnsureBrowserInitializedAsync(mainForm).GetAwaiter().GetResult();
        }
        catch
        {
            // 浏览器不可用不阻断应用启动；相关功能在解析时给出明确提示
        }

        // 启动预初始化隐藏宿主：统一线程锚点 = 主窗体，在构造器（STA 主线程 = UI 线程）
        // 同步创建，消灭首次解析的懒初始化竞态与跨线程风险；
        // 主窗体不再自动显示（新界面为主界面）——必须先创建句柄，否则后台线程的
        // 浏览器调度（EnsureUiAnchorReady）会报「主窗口句柄未创建」；
        // 失败不阻断启动（记录脱敏日志；解析/下载时给出明确提示）
        _ = mainForm.Handle;
        try
        {
            services.Browser.UiAnchor = mainForm;
            services.Browser.EnsureHostCoreAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception hostEx)
        {
            var category = ErrorClassifier.Classify(hostEx, ErrorCategory.Browser);
            AppLog.Browser.Warn($"[{category}] 浏览器隐藏宿主初始化失败：{hostEx.Message}");
        }

        // 创建系统托盘图标：关闭主窗口后进程驻留托盘
        trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Transfor",
            Visible = true,
            ContextMenuStrip = CreateTrayMenu(),
        };
        // 双击托盘图标重新打开主窗口
        trayIcon.DoubleClick += (_, _) => ShowMainWindow();
        // 按下全局快捷键时，在鼠标附近呼出历史面板
        hotKeyManager.HotKeyPressed += (_, _) => ShowHistoryPanel();

        CheckWebView2Runtime();

        RegisterSavedHotKey();
        mainForm.Shown += MainForm_Shown;
        // 主窗体延迟显示：先完成启动更新检查——
        // 强制更新时不进入主业务界面，直接进入阻断更新循环；其余情况正常显示
        _ = ShowAfterStartupCheckAsync();
        // 新界面为当前主界面：启动即打开（主窗体保留作托盘/热键载体与旧界面入口）；
        // WebView2 Runtime 缺失时降级为旧界面（不依赖 WebView2 的入口）
        if (webView2Available)
        {
            ShowAppShell();
        }
        else
        {
            ShowMainWindow();
        }
    }

    // 启动流程：后台检查更新（最多等待 timeout）；强制更新 → 不显示主窗体直接进入阻断循环；
    // 检查完成/超时/失败 → 正常显示主窗体；可选更新在显示后弹提示
    private async Task ShowAfterStartupCheckAsync()
    {
        var (result, timedOut) = await WaitStartupCheckAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        if (mainForm.IsDisposed)
        {
            return;
        }

        if (!timedOut && result?.Status == UpdateStatus.RequiredUpdate)
        {
            // 强制更新：不进入主业务界面；循环内「重新检查」变正常后回到这里继续显示
            await RunRequiredUpdateLoopAsync(result).ConfigureAwait(true);
            if (mainForm.IsDisposed)
            {
                return;
            }
        }

        if (mainForm.Visible)
        {
            return;
        }

        // 旧界面（预览）不再自动显示：新界面为当前主界面，启动自动打开；
        // 旧界面经托盘「旧界面（预览）」手动打开（显示时触发 Shown 提示快捷键问题）

        if (result is not null && !timedOut)
        {
            await HandleUpdateResultAsync(result, manual: false).ConfigureAwait(true);
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

    // 构建托盘右键菜单：新界面（主界面）/ 旧界面（预览）/ 设置 / 检查更新 / 退出
    private ContextMenuStrip CreateTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("旧界面（预览）", null, (_, _) => ShowMainWindow());
        menu.Items.Add("新界面", null, (_, _) => ShowAppShell());
        menu.Items.Add("设置", null, (_, _) => ShowSettings());
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

        startupHotKeyError = error;
        if (historyStore.Settings.HistoryHotKey != HotKeyBinding.Default
            && hotKeyManager.TryRegister(HotKeyBinding.Default, out var defaultError))
        {
            startupHotKeyError += $"已回退为默认快捷键 Alt+Q。{defaultError}";
            try
            {
                historyStore.UpdateSettings(historyStore.Settings with { HistoryHotKey = HotKeyBinding.Default });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                startupHotKeyError += $"默认快捷键状态保存失败：{ex.Message}";
            }
        }
    }

    // 主窗口首次显示后，弹窗告知启动时的快捷键问题
    private void MainForm_Shown(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(startupHotKeyError))
        {
            return;
        }

        var message = startupHotKeyError;
        startupHotKeyError = null;
        mainForm.BeginInvoke(() => MessageBox.Show(mainForm, message, "快捷键提示", MessageBoxButtons.OK, MessageBoxIcon.Warning));
    }

    // 显示并激活主窗口（最小化时先恢复为正常大小）
    private void ShowMainWindow()
    {
        if (mainForm.IsDisposed)
        {
            return;
        }

        mainForm.Show();
        if (mainForm.WindowState == FormWindowState.Minimized)
        {
            mainForm.WindowState = FormWindowState.Normal;
        }

        mainForm.BringToFront();
        mainForm.Activate();
    }

    // WebView2 Runtime 启动检查（Phase 7 Task 7.4）：缺失时记录日志 + 托盘气泡提示一次，
    // 并标记现代界面不可用（启动与 ShowAppShell 均降级为旧界面，不弹模态、不阻断启动）
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
        AppLog.Browser.Warn("未检测到 WebView2 Runtime：现代界面降级为旧界面");
        trayIcon.ShowBalloonTip(
            5000,
            "Transfor",
            "未检测到 WebView2 Runtime：现代界面不可用，已使用旧界面（浏览器与部分解析功能受限）。",
            ToolTipIcon.Warning);
    }

    // 以模态对话框打开设置窗口（主窗口不可见时以无所有者方式弹出）
    private void ShowSettings()
    {
        using var settings = new SettingsForm(historyStore, hotKeyManager, services.Browser);
        settings.ShowDialog(mainForm.Visible ? mainForm : null);
    }

    // 打开新界面宿主（Phase 5 预览）：Web UI + App Bridge；独立 Profile 与互联网浏览器隔离；
    // 下载协调器事件经 Bridge 推送（随窗体生命周期挂接）
    private void ShowAppShell()
    {
        // Runtime 缺失时降级为旧界面（现代界面依赖 WebView2）
        if (!webView2Available)
        {
            ShowMainWindow();
            return;
        }

        if (appShell is null || appShell.IsDisposed)
        {
            appShell = new AppShellForm(
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
                },
                services.Browser,
                AppPaths.Default.AppUiProfileDirectory,
                services.Media.DownloadCoordinator);
        }

        appShell.Show();
        appShell.Activate();
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
            var confirm = MessageBox.Show(mainForm, "仍有下载任务进行中，确定要退出并取消任务吗？", "退出确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
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
            mainForm.CloseForExit();
        }
        catch (Exception ex)
        {
            MessageBox.Show(mainForm, $"退出时发生错误：{ex.Message}", "退出错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                    .ConfigureAwait(false);
            }
            catch
            {
                if (manual)
                {
                    mainForm.BeginInvoke(() => ShowNotice("检查更新失败，请稍后重试。", MessageBoxIcon.Warning));
                }
                return;
            }

            if (mainForm.IsDisposed)
            {
                return;
            }

            await InvokeUiAsync(() => HandleUpdateResultAsync(result, manual)).ConfigureAwait(false);
        }
        finally
        {
            updateCheckRunning = false;
        }
    }

    // 当前更新通道（设置中可切换，实时生效）
    private UpdateChannel CurrentUpdateChannel => historyStore.Settings.UpdateChannel;

    // 把异步动作调度到 UI 线程执行：对话框与窗体必须在 UI 线程创建，
    // 动作内的 await 延续会回到 UI 线程上下文
    private Task InvokeUiAsync(Func<Task> action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (mainForm.IsDisposed)
        {
            tcs.TrySetCanceled();
            return tcs.Task;
        }

        mainForm.BeginInvoke(async () =>
        {
            try
            {
                await action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

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
                var action = UpdateNoticeForm.ShowOptional(mainForm.Visible ? mainForm : null, result);
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
    private async Task RunRequiredUpdateLoopAsync(UpdateCheckResult result)
    {
        while (!mainForm.IsDisposed)
        {
            var action = UpdateNoticeForm.ShowRequired(mainForm.Visible ? mainForm : null, result);
            switch (action)
            {
                case UpdateNoticeForm.UserAction.Exit:
                    ExitApplication();
                    return;

                case UpdateNoticeForm.UserAction.UpdateNow:
                    // 强制更新状态机：Restarting 结束；取消/失败都不允许跳过——
                    // 取消重新显示强制更新框；失败提示后回到强制框（重试或退出）
                    var flow = await RunDownloadFlowAsync(result, required: true);
                    switch (flow)
                    {
                        case UpdateDownloadForm.Result.RestartNow:
                            return;

                        case UpdateDownloadForm.Result.Failed:
                            ShowNotice("更新下载失败，请重试或选择退出后手动更新。", MessageBoxIcon.Error);
                            continue;

                        case UpdateDownloadForm.Result.Cancelled:
                            ShowNotice("更新下载已取消，必须更新后才能继续使用。", MessageBoxIcon.Warning);
                            continue;

                        default:
                            return;
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
                    return;

                default:
                    return;
            }
        }
    }

    // 下载流程：进度窗体 → 下载 → 重启提示；强制更新下载完成直接重启；
    // 「稍后重启」的更新已暂存，下次启动时由 Velopack 自动应用；
    // 返回结果供强制更新循环决策（取消/失败不得跳过强制更新）
    private async Task<UpdateDownloadForm.Result> RunDownloadFlowAsync(UpdateCheckResult result, bool required)
    {
        var installer = services.UpdateInstallerFactory(CurrentUpdateChannel);
        try
        {
            var action = await UpdateDownloadForm.RunAsync(mainForm.Visible ? mainForm : null, installer, result, required);
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

    private void ShowNotice(string message, MessageBoxIcon icon) =>
        MessageBox.Show(mainForm, message, "更新", MessageBoxButtons.OK, icon);
}
