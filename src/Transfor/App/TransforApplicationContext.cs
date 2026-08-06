namespace Transfor;

// 应用级上下文：持有主窗口、历史面板、托盘图标与全局热键，并协调它们之间的跳转
internal sealed class TransforApplicationContext : ApplicationContext
{
    private readonly TextStateStore historyStore;
    private readonly AppServices services;
    private readonly MainForm mainForm;
    private readonly HistoryPanelForm historyPanel;
    private readonly GlobalHotKeyManager hotKeyManager;
    private readonly NotifyIcon trayIcon;

    // 启动时注册快捷键失败的错误信息，待主窗口首次显示后弹窗提示
    private string? startupHotKeyError;

    // 是否正在退出（防止重复触发退出流程）
    private bool exiting;

    public TransforApplicationContext(AppServices services)
    {
        this.services = services;
        historyStore = services.State;
        hotKeyManager = services.HotKeys;
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
    }

    // 构建托盘右键菜单：打开主窗口 / 设置 / 退出
    private ContextMenuStrip CreateTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => ShowMainWindow());
        menu.Items.Add("设置", null, (_, _) => ShowSettings());
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
}
