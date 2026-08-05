namespace Transfor;

public sealed class MainForm : Form
{
    private readonly TextToolsPage textToolsPage;
    private readonly Panel contentPanel;
    private bool allowClose;

    internal MainForm(TextStateStore historyStore)
    {
        Text = "文本转换器"; StartPosition = FormStartPosition.CenterScreen; MinimumSize = new Size(900, 600); Size = new Size(980, 660); Font = new Font("Microsoft YaHei UI", 10F);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 2 }; root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var textToolsButton = new Button { AutoSize = true, Text = "文本转换" }; navigation.Controls.Add(textToolsButton);
        contentPanel = new Panel { Dock = DockStyle.Fill }; textToolsPage = new TextToolsPage(historyStore); textToolsButton.Click += (_, _) => ShowPage(textToolsPage);
        root.Controls.Add(navigation, 0, 0); root.Controls.Add(contentPanel, 0, 1); Controls.Add(root); FormClosing += MainForm_FormClosing; ShowPage(textToolsPage);
    }

    public void CloseForExit() { allowClose = true; Close(); }
    private void ShowPage(IFeaturePage page) { contentPanel.Controls.Clear(); var view = page.View; view.Dock = DockStyle.Fill; contentPanel.Controls.Add(view); page.OnActivated(); }
    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e) { if (allowClose || e.CloseReason != CloseReason.UserClosing) return; e.Cancel = true; Hide(); }
}