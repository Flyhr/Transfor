using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Transfor;

// 浏览器功能页（Phase 3）：地址栏 + 后退/前进/刷新/停止 + WebView2 页面；
// 独立 Profile 持久化登录态；初始化失败（如 Runtime 缺失）时显示提示面板而非崩溃
internal sealed class BrowserView : UserControl, IFeaturePage
{
    private readonly IBrowserService browserService;
    private readonly TextBox addressBox;
    private readonly Button backButton;
    private readonly Button forwardButton;
    private readonly Button refreshButton;
    private readonly Button stopButton;
    private readonly WebView2 webView;
    private readonly Label errorLabel;
    private bool initialized;

    public BrowserView(IBrowserService browserService)
    {
        this.browserService = browserService ?? throw new ArgumentNullException(nameof(browserService));

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // 顶部工具条：后退/前进/刷新/停止 + 地址栏 + 前往
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 4, 0, 4) };
        backButton = CreateToolbarButton("←", () => browserService.Navigation.Back());
        forwardButton = CreateToolbarButton("→", () => browserService.Navigation.Forward());
        refreshButton = CreateToolbarButton("⟳", () => browserService.Navigation.Refresh());
        stopButton = CreateToolbarButton("×", () => browserService.Navigation.Stop());
        addressBox = new TextBox { Width = 420, Height = 30, Font = new Font("Microsoft YaHei UI", 10F) };
        var goButton = new Button { AutoSize = true, Text = "前往", Height = 30 };
        goButton.Click += (_, _) => NavigateToAddress();
        addressBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                NavigateToAddress();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };
        toolbar.Controls.AddRange(new Control[] { backButton, forwardButton, refreshButton, stopButton, addressBox, goButton });
        root.Controls.Add(toolbar, 0, 0);

        // WebView2 内容区 + 初始化失败提示面板
        webView = new WebView2 { Dock = DockStyle.Fill, TabIndex = 1 };
        errorLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Visible = false,
            Padding = new Padding(24),
            Text = "浏览器不可用。",
        };
        root.Controls.Add(webView, 0, 1);
        root.Controls.Add(errorLabel, 0, 1);
        root.SetRowSpan(errorLabel, 1);
        Controls.Add(root);

        Load += (_, _) => InitializeBrowser();
    }

    public string Id => "browser";
    public string DisplayName => "浏览器";
    public Control View => this;

    // 页面激活时聚焦地址栏并全选，方便直接输入新地址
    public void OnActivated()
    {
        if (initialized || errorLabel.Visible)
        {
            return;
        }

        addressBox.Focus();
        addressBox.SelectAll();
    }

    private static Button CreateToolbarButton(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 30, TabStop = false };
        button.Click += (_, _) => action();
        return button;
    }

    // 惰性初始化：首次进入页面时创建 WebView2 环境（独立 Profile）；
    // 失败显示提示面板，浏览器按钮变为不可用
    private async void InitializeBrowser()
    {
        if (initialized || errorLabel.Visible)
        {
            return;
        }

        try
        {
            await browserService.InitializeAsync(webView);
            initialized = true;
            SetBrowserEnabled(true);
            webView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
            webView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            webView.Focus();
            addressBox.Focus();
        }
        catch
        {
            errorLabel.Text = browserService.InitializationError ?? "浏览器不可用。";
            errorLabel.Visible = true;
            webView.Visible = false;
            SetBrowserEnabled(false);
        }
    }

    private void SetBrowserEnabled(bool enabled)
    {
        backButton.Enabled = enabled;
        forwardButton.Enabled = enabled;
        refreshButton.Enabled = enabled;
        stopButton.Enabled = enabled;
        addressBox.Enabled = enabled;
    }

    private void NavigateToAddress()
    {
        if (!initialized)
        {
            return;
        }

        try
        {
            browserService.Navigation.Navigate(addressBox.Text);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(this, ex.Message, "浏览器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void CoreWebView2_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        addressBox.Text = e.Uri;
        UpdateNavigationButtons();
    }

    private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        UpdateNavigationButtons();
    }

    private void UpdateNavigationButtons()
    {
        backButton.Enabled = initialized && browserService.Navigation.CanGoBack;
        forwardButton.Enabled = initialized && browserService.Navigation.CanGoForward;
        refreshButton.Enabled = initialized;
        stopButton.Enabled = initialized;
    }
}
