using System.Runtime.InteropServices;

namespace Transfor;

// 文本转换页面：输入 → 实时转换 → 复制结果并记入历史；支持两种工具切换
internal sealed class TextToolsPage : UserControl, IFeaturePage
{
    private readonly TextBox inputTextBox;
    private readonly TextBox outputTextBox;
    private readonly Button copyButton;
    private readonly Button quoteButton;
    private readonly Button spaceButton;
    private readonly Label titleLabel;

    // 两种文本工具的静态定义
    private readonly TextToolDefinition quoteTool = new(TextToolId.QuoteConversion, "引号转换", QuoteConverter.Convert);
    private readonly TextToolDefinition spaceTool = new(TextToolId.SpaceRemoval, "去除空格", SpaceRemover.Remove);

    private readonly TextStateStore historyStore;
    private TextToolDefinition currentTool;

    public TextToolsPage(TextStateStore historyStore)
    {
        this.historyStore = historyStore;
        currentTool = quoteTool;
        Dock = DockStyle.Fill;
        // 页面布局：导航栏 / 标题 / 输入输出区 / 操作按钮区
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        // 工具切换按钮
        quoteButton = CreateNavButton(quoteTool.DisplayName); quoteButton.Click += (_, _) => SelectTool(quoteTool);
        spaceButton = CreateNavButton(spaceTool.DisplayName); spaceButton.Click += (_, _) => SelectTool(spaceTool);
        var nav = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false }; nav.Controls.AddRange([quoteButton, spaceButton]);
        titleLabel = new Label { Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 18F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
        // 输入与输出文本框并排
        inputTextBox = CreateTextBox(false); outputTextBox = CreateTextBox(true);
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 }; grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); grid.Controls.Add(CreateTextPanel("输入内容", inputTextBox), 0, 0); grid.Controls.Add(CreateTextPanel("转换结果", outputTextBox), 1, 0);
        // 操作按钮：复制结果（无结果时禁用）、清空输入
        copyButton = new Button { AutoSize = true, Enabled = false, Text = "复制结果" }; copyButton.Click += CopyButton_Click;
        var clearButton = new Button { AutoSize = true, Text = "清空" }; clearButton.Click += (_, _) => { inputTextBox.Clear(); inputTextBox.Focus(); };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) }; actions.Controls.AddRange([copyButton, clearButton]);
        root.Controls.Add(nav, 0, 0); root.Controls.Add(titleLabel, 0, 1); root.Controls.Add(grid, 0, 2); root.Controls.Add(actions, 0, 3); Controls.Add(root);
        // 输入变化时实时更新转换结果
        inputTextBox.TextChanged += (_, _) => UpdateOutput(); SelectTool(quoteTool);
    }

    public string Id => "text-tools";
    public string DisplayName => "文本转换";
    public Control View => this;

    // 页面被切换到前台时聚焦输入框
    public void OnActivated() => inputTextBox.Focus();

    // 创建导航按钮（扁平样式）
    private static Button CreateNavButton(string text) { var button = new Button { FlatStyle = FlatStyle.Flat, Height = 36, Margin = new Padding(0, 4, 8, 4), Text = text, UseVisualStyleBackColor = false, Width = 120 }; button.FlatAppearance.BorderColor = Color.FromArgb(198, 206, 218); return button; }

    // 创建多行文本框；readOnly=true 用于结果框（不允许编辑）
    private static TextBox CreateTextBox(bool readOnly) => new() { AcceptsReturn = true, AcceptsTab = true, BackColor = SystemColors.Window, Dock = DockStyle.Fill, Multiline = true, ReadOnly = readOnly, ScrollBars = ScrollBars.Both, WordWrap = false };

    // 创建带标题的文本区（标签在上、文本框占满剩余空间）
    private static Control CreateTextPanel(string labelText, TextBox textBox) { var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 12, 0), RowCount = 2 }; panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); panel.Controls.Add(new Label { Dock = DockStyle.Fill, Text = labelText, TextAlign = ContentAlignment.MiddleLeft }, 0, 0); panel.Controls.Add(textBox, 0, 1); return panel; }

    // 切换当前工具：更新标题、按钮选中态并重新计算结果
    private void SelectTool(TextToolDefinition tool) { currentTool = tool; titleLabel.Text = tool.DisplayName; Apply(quoteButton, tool == quoteTool); Apply(spaceButton, tool == spaceTool); UpdateOutput(); }

    // 切换按钮的选中样式（高亮背景）
    private static void Apply(Button button, bool selected) { button.BackColor = selected ? Color.FromArgb(31,111,235) : Color.FromArgb(242,245,249); button.ForeColor = selected ? Color.White : Color.FromArgb(35,40,48); button.FlatAppearance.BorderSize = selected ? 0 : 1; }

    // 用当前工具转换输入并刷新结果；有结果时启用复制按钮
    private void UpdateOutput() { outputTextBox.Text = currentTool.Convert(inputTextBox.Text); copyButton.Enabled = outputTextBox.TextLength > 0; }

    // 复制结果到剪贴板，成功后把本次转换记入历史（Add 内部已落盘）
    private void CopyButton_Click(object? sender, EventArgs e)
    {
        if (outputTextBox.TextLength == 0) return;
        try { Clipboard.SetText(outputTextBox.Text); }
        catch (Exception ex) when (ex is ExternalException or ThreadStateException or InvalidOperationException) { MessageBox.Show(this, $"写入系统剪贴板失败：{ex.Message}", "复制失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        try { historyStore.Add(new HistoryEntry(currentTool.Id, inputTextBox.Text, outputTextBox.Text, DateTimeOffset.UtcNow)); }
        catch (IOException ex) { MessageBox.Show(this, $"历史记录保存失败：{ex.Message}", "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
