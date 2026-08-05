namespace Transfor;

// 历史面板：全局快捷键呼出的小窗口，展示所选工具的历史并支持单击/回车粘贴回原窗口
internal sealed class HistoryPanelForm : Form
{
    private readonly TextStateStore historyStore;
    private readonly PasteCoordinator pasteCoordinator;
    private readonly Button quoteButton;
    private readonly Button spaceButton;
    private readonly ListBox historyList;
    private readonly Label errorLabel;

    // 当前展示哪个工具的历史
    private TextToolId currentTool;

    // 呼出面板时的前台窗口，作为粘贴目标
    private nint targetWindow;

    // 是否允许真正关闭（仅退出应用时置 true）
    private bool allowClose;

    public HistoryPanelForm(TextStateStore historyStore, PasteCoordinator pasteCoordinator)
    {
        this.historyStore = historyStore;
        this.pasteCoordinator = pasteCoordinator;
        // 恢复上次查看的工具分类
        currentTool = historyStore.UiState.LastViewedTool;

        // 面板样式：无任务栏入口的置顶工具窗口
        Text = "Transfor 历史记录";
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        ClientSize = new Size(620, 500);
        Font = new Font("Microsoft YaHei UI", 10F);

        // 布局：工具切换栏 / 历史列表 / 错误提示
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 3,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        // 顶部工具切换按钮
        var nav = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        quoteButton = CreateToolButton("引号转换");
        quoteButton.Click += (_, _) => SelectTool(TextToolId.QuoteConversion);
        spaceButton = CreateToolButton("去除空格");
        spaceButton.Click += (_, _) => SelectTool(TextToolId.SpaceRemoval);
        nav.Controls.Add(quoteButton);
        nav.Controls.Add(spaceButton);

        // 历史列表：单击或回车执行粘贴
        historyList = new ListBox
        {
            Dock = DockStyle.Fill,
            HorizontalScrollbar = true,
            IntegralHeight = false,
        };
        historyList.MouseClick += (_, _) => ExecuteSelected();
        historyList.KeyDown += HistoryList_KeyDown;

        // 底部错误提示区
        errorLabel = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.Firebrick,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        root.Controls.Add(nav, 0, 0);
        root.Controls.Add(historyList, 0, 1);
        root.Controls.Add(errorLabel, 0, 2);
        Controls.Add(root);
        FormClosing += HistoryPanelForm_FormClosing;
    }

    // 在鼠标附近显示面板；foregroundWindow 为呼出前的目标窗口
    public void ShowFor(nint foregroundWindow)
    {
        targetWindow = foregroundWindow;
        errorLabel.Text = string.Empty;
        SelectTool(historyStore.UiState.LastViewedTool);
        // 首次呼出：面板定位在鼠标位置附近
        if (!Visible)
        {
            var cursor = Cursor.Position;
            Location = new Point(Math.Max(0, cursor.X - Width / 2), Math.Max(0, cursor.Y - Height / 2));
            Show();
        }
        else
        {
            BringToFront();
        }

        Activate();
    }

    // 退出应用时调用：放行关闭
    public void CloseForExit()
    {
        allowClose = true;
        Close();
    }

    private static Button CreateToolButton(string text)
    {
        return new Button
        {
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            Text = text,
            UseVisualStyleBackColor = false,
        };
    }

    // 切换工具分类：更新按钮选中态、刷新列表，并持久化「最近查看」状态
    private void SelectTool(TextToolId tool)
    {
        currentTool = tool;
        ApplyButtonState(quoteButton, tool == TextToolId.QuoteConversion);
        ApplyButtonState(spaceButton, tool == TextToolId.SpaceRemoval);
        RefreshHistory();
        if (historyStore.UiState.LastViewedTool != tool)
        {
            try
            {
                historyStore.SetLastViewedTool(tool);
            }
            catch (IOException ex)
            {
                errorLabel.Text = $"保存当前分类失败：{ex.Message}";
            }
        }
    }

    // 切换按钮的选中样式
    private static void ApplyButtonState(Button button, bool selected)
    {
        button.BackColor = selected ? Color.FromArgb(31, 111, 235) : Color.FromArgb(242, 245, 249);
        button.ForeColor = selected ? Color.White : Color.FromArgb(35, 40, 48);
        button.FlatAppearance.BorderSize = selected ? 0 : 1;
    }

    // 刷新列表：倒序（最新在前）显示当前工具的历史
    private void RefreshHistory()
    {
        historyList.BeginUpdate();
        try
        {
            historyList.Items.Clear();
            foreach (var entry in historyStore.GetHistory(currentTool).Reverse())
            {
                historyList.Items.Add(new HistoryListItem(entry));
            }

            // 默认选中最新一条
            if (historyList.Items.Count > 0)
            {
                historyList.SelectedIndex = 0;
            }
        }
        finally
        {
            historyList.EndUpdate();
        }
    }

    // 把选中的历史项粘贴回原窗口；成功后隐藏面板
    private void ExecuteSelected()
    {
        if (historyList.SelectedItem is not HistoryListItem item)
        {
            return;
        }

        var result = pasteCoordinator.TryPaste(item.Entry, targetWindow);
        if (!result.Succeeded)
        {
            errorLabel.Text = result.Error;
            return;
        }

        errorLabel.Text = string.Empty;
        Hide();
    }

    // 键盘操作：回车 = 粘贴选中项，Esc = 关闭面板
    private void HistoryList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            ExecuteSelected();
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            Hide();
        }
    }

    // 拦截关闭：非退出流程一律改为隐藏，保证面板可反复呼出
    private void HistoryPanelForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    // 列表项：显示转换结果预览（单行截断至 80 字符）与本地时间
    private sealed record HistoryListItem(HistoryEntry Entry)
    {
        public override string ToString()
        {
            // 把多行内容压成单行便于预览
            var preview = Entry.ConvertedOutput
                .Replace("\r\n", " ", StringComparison.Ordinal)
                .Replace('\r', ' ')
                .Replace('\n', ' ');
            if (preview.Length > 80)
            {
                preview = preview[..80] + "…";
            }

            return $"{preview}    {Entry.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        }
    }
}
