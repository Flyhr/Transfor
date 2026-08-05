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
        // 由页面集合构造主窗口外壳（后续任务会加入媒体下载页）
        var pages = new IFeaturePage[] { new TextToolsPage(services.State) };
        mainForm = new MainForm(pages);
        historyPanel = new HistoryPanelForm(
            historyStore,
            services.PasteCoordinator);

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

    // 退出应用：依次关闭历史面板、主窗口，释放服务与托盘图标，最后结束消息循环
    private void ExitApplication()
    {
        if (exiting)
        {
            return;
        }

        exiting = true;
        historyPanel.CloseForExit();
        mainForm.CloseForExit();
        services.Dispose();
        trayIcon.Visible = false;
        trayIcon.Dispose();
        ExitThread();
    }
}
