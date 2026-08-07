using System.Diagnostics;

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

    public TransforApplicationContext(AppServices services)
    {
        this.services = services;
        historyStore = services.State;
        hotKeyManager = services.HotKeys;
        updatesService = services.Updates;
        // 由页面集合构造主窗口外壳：文本转换 + 媒体下载
        var pages = new IFeaturePage[]
        {
            new TextToolsPage(services.State),
            new MediaDownloadPage(
                services.Media.ResolveCoordinator,
                services.Media.DownloadCoordinator,
                services.Media.State,
                services.Media.EnsureBrowserInitializedAsync,
                services.Media.Preview),
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

        RegisterSavedHotKey();
        mainForm.Shown += MainForm_Shown;
        mainForm.Show();
        // 启动后后台静默检查更新：失败不打扰；可选/强制更新按状态提示
        _ = CheckAndPromptAsync(manual: false);
    }

    // 构建托盘右键菜单：打开主窗口 / 设置 / 检查更新 / 退出
    private ContextMenuStrip CreateTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => ShowMainWindow());
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

    // 以模态对话框打开设置窗口（主窗口不可见时以无所有者方式弹出）
    private void ShowSettings()
    {
        using var settings = new SettingsForm(historyStore, hotKeyManager);
        settings.ShowDialog(mainForm.Visible ? mainForm : null);
    }

    // 呼出历史面板：记录当前前台窗口句柄，作为稍后粘贴的目标
    private void ShowHistoryPanel()
    {
        var targetWindow = WindowsNative.GetForegroundWindow();
        historyPanel.ShowFor(targetWindow);
    }

    // 退出应用：有活动任务时先确认，再取消任务并等待落定；
    // 主窗体仍存活时释放服务（含关闭专用 Edge 进程），
    // 最后关闭窗口与托盘；释放异常转换为可见错误，不遗留半退出状态
    private async void ExitApplication()
    {
        if (exiting)
        {
            return;
        }

        exiting = true;
        try
        {
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

            // 主窗体仍存活：释放服务并关闭专用 Edge 进程
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

    // 检查并提示更新：检查在后台执行，UI 交互回到主线程；
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
                result = await Task.Run(() => updatesService.CheckForUpdatesAsync(CancellationToken.None)).ConfigureAwait(false);
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

            if (manual && result.Status is UpdateStatus.UpToDate or UpdateStatus.CheckFailed)
            {
                var message = result.Status == UpdateStatus.UpToDate ? "当前已是最新版本。" : $"检查更新失败：{result.Error}";
                mainForm.BeginInvoke(() => ShowNotice(message, MessageBoxIcon.Information));
                return;
            }

            await PromptForResultAsync(result).ConfigureAwait(false);
        }
        finally
        {
            updateCheckRunning = false;
        }
    }

    // 按结果类型弹出对应提示：可选更新一次；强制更新进入阻断循环直到更新/退出
    private async Task PromptForResultAsync(UpdateCheckResult result)
    {
        switch (result.Status)
        {
            case UpdateStatus.OptionalUpdate:
                await mainForm.InvokeAsync(() =>
                {
                    var action = UpdateNoticeForm.ShowOptional(mainForm.Visible ? mainForm : null, result);
                    if (action == UpdateNoticeForm.UserAction.UpdateNow)
                    {
                        OpenDownloadPage(result);
                    }
                }).ConfigureAwait(false);
                break;

            case UpdateStatus.RequiredUpdate:
                await RunRequiredUpdateLoopAsync(result).ConfigureAwait(false);
                break;
        }
    }

    // 强制更新阻断循环：退出 → 退出应用；立即更新 → 打开下载页并继续阻断；重新检查 → 重新判定
    private async Task RunRequiredUpdateLoopAsync(UpdateCheckResult result)
    {
        while (!mainForm.IsDisposed)
        {
            var action = await mainForm.InvokeAsync(() =>
                UpdateNoticeForm.ShowRequired(mainForm.Visible ? mainForm : null, result)).ConfigureAwait(false);

            switch (action)
            {
                case UpdateNoticeForm.UserAction.Exit:
                    mainForm.BeginInvoke(ExitApplication);
                    return;

                case UpdateNoticeForm.UserAction.UpdateNow:
                    OpenDownloadPage(result);
                    break;

                case UpdateNoticeForm.UserAction.Recheck:
                    UpdateCheckResult rechecked;
                    try
                    {
                        rechecked = await Task.Run(() => updatesService.CheckForUpdatesAsync(CancellationToken.None))
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        return;
                    }

                    if (rechecked.Status != UpdateStatus.RequiredUpdate)
                    {
                        await PromptForResultAsync(rechecked).ConfigureAwait(false);
                        return;
                    }

                    result = rechecked;
                    break;

                default:
                    return;
            }
        }
    }

    // 「立即更新」（Phase 1）：打开下载地址；Phase 2 接入 Velopack 后替换为真实下载安装流程
    private void OpenDownloadPage(UpdateCheckResult result)
    {
        var url = result.Policy?.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowNotice("尚未提供下载地址，请稍后到项目主页获取新版本。", MessageBoxIcon.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowNotice($"打开下载地址失败：{ex.Message}", MessageBoxIcon.Warning);
        }
    }

    private void ShowNotice(string message, MessageBoxIcon icon) =>
        MessageBox.Show(mainForm, message, "更新", MessageBoxButtons.OK, icon);
}
