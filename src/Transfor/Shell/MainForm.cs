namespace Transfor;

// 主窗口外壳：顶部导航栏 + 内容区，承载各功能页；用户关闭时隐藏窗口而非退出（驻留托盘）
public sealed class MainForm : Form
{
    private readonly IReadOnlyList<IFeaturePage> pages;
    private readonly Panel contentPanel;

    // 是否允许真正关闭（仅「退出」流程会置为 true）
    private bool allowClose;

    internal MainForm(IReadOnlyList<IFeaturePage> pages)
    {
        if (pages is null || pages.Count == 0)
        {
            throw new ArgumentException("主窗口至少需要一个功能页。", nameof(pages));
        }

        this.pages = pages;
        Text = "文本转换器"; StartPosition = FormStartPosition.CenterScreen; MinimumSize = new Size(900, 600); Size = new Size(980, 660); Font = new Font("Microsoft YaHei UI", 10F);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 2 }; root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        // 顶部导航区：根据页面集合动态创建导航按钮
        var navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        foreach (var page in pages)
        {
            var button = new Button { AutoSize = true, Text = page.DisplayName };
            button.Click += (_, _) => ShowPage(page);
            navigation.Controls.Add(button);
        }
        contentPanel = new Panel { Dock = DockStyle.Fill };
        root.Controls.Add(navigation, 0, 0); root.Controls.Add(contentPanel, 0, 1); Controls.Add(root); FormClosing += MainForm_FormClosing; ShowPage(pages[0]);
    }

    // 退出应用时调用：放行关闭
    public void CloseForExit() { allowClose = true; Close(); }

    // 切换内容区显示的页面：清空旧页面 → 填充新页面 → 通知页面激活
    private void ShowPage(IFeaturePage page) { contentPanel.Controls.Clear(); var view = page.View; view.Dock = DockStyle.Fill; contentPanel.Controls.Add(view); page.OnActivated(); }

    // 用户点击关闭按钮时取消关闭并隐藏窗口，让程序继续驻留托盘
    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e) { if (allowClose || e.CloseReason != CloseReason.UserClosing) return; e.Cancel = true; Hide(); }
}
