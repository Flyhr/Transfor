namespace Transfor;

internal sealed class TransforApplicationContext : ApplicationContext
{
    private readonly TextStateStore historyStore;
    private readonly AppServices services;
    private readonly MainForm mainForm;
    private readonly HistoryPanelForm historyPanel;
    private readonly GlobalHotKeyManager hotKeyManager;
    private readonly NotifyIcon trayIcon;
    private string? startupHotKeyError;
    private bool exiting;

    public TransforApplicationContext(AppServices services)
    {
        this.services = services;
        historyStore = services.State;
        hotKeyManager = services.HotKeys;
        mainForm = new MainForm(historyStore);
        historyPanel = new HistoryPanelForm(
            historyStore,
            services.PasteCoordinator);

        trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Transfor",
            Visible = true,
            ContextMenuStrip = CreateTrayMenu(),
        };
        trayIcon.DoubleClick += (_, _) => ShowMainWindow();
        hotKeyManager.HotKeyPressed += (_, _) => ShowHistoryPanel();

        RegisterSavedHotKey();
        mainForm.Shown += MainForm_Shown;
        mainForm.Show();
    }

    private ContextMenuStrip CreateTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => ShowMainWindow());
        menu.Items.Add("设置", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());
        return menu;
    }

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
                historyStore.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                startupHotKeyError += $"默认快捷键状态保存失败：{ex.Message}";
            }
        }
    }

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

    private void ShowSettings()
    {
        using var settings = new SettingsForm(historyStore, hotKeyManager);
        settings.ShowDialog(mainForm.Visible ? mainForm : null);
    }

    private void ShowHistoryPanel()
    {
        var targetWindow = WindowsNative.GetForegroundWindow();
        historyPanel.ShowFor(targetWindow);
    }

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



