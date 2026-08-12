using System.Drawing.Drawing2D;

namespace Transfor;

// 历史面板：全局快捷键呼出的小窗口，展示所选工具的历史并支持单击/回车粘贴回原窗口；
// 视觉对齐新界面 Focused Flow 风格：白底、圆角、青绿选中态、双行列表项
internal sealed class HistoryPanelForm : Form
{
    private static readonly Color ColorBackground = Color.FromArgb(255, 255, 255);
    private static readonly Color ColorBorder = Color.FromArgb(226, 232, 240);
    private static readonly Color ColorPrimary = Color.FromArgb(15, 118, 110);
    private static readonly Color ColorPrimarySoft = Color.FromArgb(234, 248, 246);
    private static readonly Color ColorText = Color.FromArgb(11, 18, 32);
    private static readonly Color ColorMuted = Color.FromArgb(71, 85, 105);
    private static readonly Color ColorFaint = Color.FromArgb(148, 163, 184);
    private static readonly Color ColorDanger = Color.FromArgb(184, 63, 59);

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
        // FixedSingle 与主窗体（Sizable）使用相同的标准系统标题栏渲染（背景/关闭叉号一致），
        // 同时保持窗口不可缩放
        Text = "Transfor 历史记录";
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        ClientSize = new Size(620, 500);
        BackColor = ColorBackground;
        Font = new Font("Segoe UI Variable", 10F);
        Region = CreateRoundedRegion(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), 12);

        // 布局：工具切换栏 / 历史列表 / 错误提示
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = ColorBackground,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 3,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

        // 顶部工具切换按钮（胶囊选中态）
        var nav = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = ColorBackground, Padding = new Padding(0, 2, 0, 8) };
        quoteButton = CreateToolButton("引号转换");
        quoteButton.Click += (_, _) => SelectTool(TextToolId.QuoteConversion);
        spaceButton = CreateToolButton("去除空格");
        spaceButton.Click += (_, _) => SelectTool(TextToolId.SpaceRemoval);
        nav.Controls.Add(quoteButton);
        nav.Controls.Add(spaceButton);

        // 历史列表：单击或回车执行粘贴（OwnerDraw 双行 + 圆角选中块）
        historyList = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = ColorBackground,
            ForeColor = ColorText,
            IntegralHeight = false,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 44,
            Font = new Font("Segoe UI Variable", 9.5F),
        };
        historyList.DrawItem += HistoryList_DrawItem;
        historyList.MouseClick += (_, _) => ExecuteSelected();
        historyList.KeyDown += HistoryList_KeyDown;

        // 底部错误提示区
        errorLabel = new Label
        {
            Dock = DockStyle.Fill,
            BackColor = ColorBackground,
            ForeColor = ColorDanger,
            Font = new Font("Segoe UI Variable", 9F),
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
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI Variable", 9.5F),
            Height = 30,
            Padding = new Padding(14, 0, 14, 0),
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

    // 切换按钮的选中样式（Focused Flow 青绿胶囊）
    private static void ApplyButtonState(Button button, bool selected)
    {
        button.BackColor = selected ? ColorPrimary : ColorBackground;
        button.ForeColor = selected ? Color.White : ColorMuted;
        button.FlatAppearance.BorderColor = ColorBorder;
        button.FlatAppearance.BorderSize = selected ? 0 : 1;
        button.FlatAppearance.MouseOverBackColor = selected ? ColorPrimary : ColorPrimarySoft;
        button.Region = CreateRoundedRegion(new Rectangle(0, 0, button.Width, button.Height), 8);
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

    // 自绘列表项：预览主行 + 时间副行；选中项浅青圆角块 + 青绿主文字
    private void HistoryList_DrawItem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= historyList.Items.Count)
        {
            return;
        }

        var item = (HistoryListItem)historyList.Items[e.Index];
        var bounds = e.Bounds;
        var selected = (e.State & DrawItemState.Selected) != 0;

        using var bg = new SolidBrush(selected ? ColorPrimarySoft : ColorBackground);
        using var bgPath = CreateRoundedPath(new Rectangle(bounds.X + 2, bounds.Y + 2, bounds.Width - 4, bounds.Height - 4), 8);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillPath(bg, bgPath);

        var textRect = new Rectangle(bounds.X + 10, bounds.Y + 5, bounds.Width - 20, 22);
        using var mainBrush = new SolidBrush(selected ? ColorPrimary : ColorText);
        using var timeBrush = new SolidBrush(ColorFaint);
        using var mainFont = new Font("Segoe UI Variable", 9.5F);
        using var timeFont = new Font("Segoe UI Variable", 8.5F);
        e.Graphics.DrawString(item.Preview, mainFont, mainBrush, textRect, StringFormat.GenericDefault);
        e.Graphics.DrawString(item.TimeText, timeFont, timeBrush, new Rectangle(textRect.X, bounds.Y + 26, textRect.Width, 16), StringFormat.GenericDefault);

        // 焦点虚线保留（键盘可访问性）
        if ((e.State & DrawItemState.Focus) != 0)
        {
            ControlPaint.DrawFocusRectangle(e.Graphics, bounds, selected ? ColorPrimary : ColorMuted, ColorBackground);
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

    // 圆角 Region（按控件实际尺寸构建；窗体 FixedToolWindow 不可缩放，构造时一次即可）
    private static Region CreateRoundedRegion(Rectangle bounds, int radius)
        => new Region(CreateRoundedPath(bounds, radius));

    private static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // 列表项：预览单行 + 本地时间（双行绘制）
    private sealed record HistoryListItem(HistoryEntry Entry)
    {
        public string Preview
        {
            get
            {
                var preview = Entry.ConvertedOutput
                    .Replace("\r\n", " ", StringComparison.Ordinal)
                    .Replace('\r', ' ')
                    .Replace('\n', ' ');
                return preview.Length > 80 ? preview[..80] + "…" : preview;
            }
        }

        public string TimeText => Entry.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        public override string ToString() => $"{Preview}    {TimeText}";
    }
}
