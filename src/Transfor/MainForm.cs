using System.Runtime.InteropServices;

namespace Transfor;

public sealed class MainForm : Form
{
    private static readonly Color SelectedNavColor = Color.FromArgb(31, 111, 235);
    private static readonly Color SelectedNavTextColor = Color.White;
    private static readonly Color DefaultNavColor = Color.FromArgb(242, 245, 249);
    private static readonly Color DefaultNavTextColor = Color.FromArgb(35, 40, 48);

    private readonly TextBox inputTextBox;
    private readonly TextBox outputTextBox;
    private readonly Button copyButton;
    private readonly Button quoteButton;
    private readonly Button spaceButton;
    private readonly Label titleLabel;
    private readonly TextToolDefinition quoteTool;
    private readonly TextToolDefinition spaceTool;
    private readonly LegacyHistoryStore historyStore;

    private TextToolDefinition currentTool;
    private bool allowClose;

    internal MainForm(LegacyHistoryStore historyStore)
    {
        this.historyStore = historyStore;
        quoteTool = new TextToolDefinition(TextToolId.QuoteConversion, "引号转换", QuoteConverter.Convert);
        spaceTool = new TextToolDefinition(TextToolId.SpaceRemoval, "去除空格", SpaceRemover.Remove);
        currentTool = quoteTool;

        Text = "文本转换器";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 600);
        Size = new Size(980, 660);
        Font = new Font("Microsoft YaHei UI", 10F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 4,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        quoteButton = CreateNavButton(quoteTool.DisplayName);
        quoteButton.Click += (_, _) => SelectTool(quoteTool);

        spaceButton = CreateNavButton(spaceTool.DisplayName);
        spaceButton.Click += (_, _) => SelectTool(spaceTool);

        var navRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        navRow.Controls.Add(quoteButton);
        navRow.Controls.Add(spaceButton);

        titleLabel = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 18F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        var textGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
        };
        textGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        textGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        inputTextBox = CreateTextBox(readOnly: false);
        outputTextBox = CreateTextBox(readOnly: true);

        textGrid.Controls.Add(CreateTextPanel("输入内容", inputTextBox), 0, 0);
        textGrid.Controls.Add(CreateTextPanel("转换结果", outputTextBox), 1, 0);

        copyButton = new Button
        {
            AutoSize = true,
            Enabled = false,
            Text = "复制结果",
        };
        copyButton.Click += CopyButton_Click;

        var clearButton = new Button
        {
            AutoSize = true,
            Text = "清空",
        };
        clearButton.Click += (_, _) =>
        {
            inputTextBox.Clear();
            inputTextBox.Focus();
        };

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 8, 0, 0),
        };
        buttonRow.Controls.Add(copyButton);
        buttonRow.Controls.Add(clearButton);

        root.Controls.Add(navRow, 0, 0);
        root.Controls.Add(titleLabel, 0, 1);
        root.Controls.Add(textGrid, 0, 2);
        root.Controls.Add(buttonRow, 0, 3);

        Controls.Add(root);

        inputTextBox.TextChanged += (_, _) => UpdateOutput();
        FormClosing += MainForm_FormClosing;
        SelectTool(quoteTool);
    }
    public void CloseForExit()
    {
        allowClose = true;
        Close();
    }

    private static Button CreateNavButton(string text)
    {
        var button = new Button
        {
            FlatStyle = FlatStyle.Flat,
            Height = 36,
            Margin = new Padding(0, 4, 8, 4),
            Text = text,
            UseVisualStyleBackColor = false,
            Width = 120,
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(198, 206, 218);

        return button;
    }

    private static TextBox CreateTextBox(bool readOnly)
    {
        return new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            BackColor = SystemColors.Window,
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = readOnly,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
        };
    }

    private static Control CreateTextPanel(string labelText, TextBox textBox)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 0, 12, 0),
            RowCount = 2,
            ColumnCount = 1,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var label = new Label
        {
            Dock = DockStyle.Fill,
            Text = labelText,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        panel.Controls.Add(label, 0, 0);
        panel.Controls.Add(textBox, 0, 1);

        return panel;
    }

    private void SelectTool(TextToolDefinition tool)
    {
        currentTool = tool;
        titleLabel.Text = tool.DisplayName;
        ApplyNavButtonState(quoteButton, tool == quoteTool);
        ApplyNavButtonState(spaceButton, tool == spaceTool);
        UpdateOutput();
    }

    private static void ApplyNavButtonState(Button button, bool selected)
    {
        button.BackColor = selected ? SelectedNavColor : DefaultNavColor;
        button.ForeColor = selected ? SelectedNavTextColor : DefaultNavTextColor;
        button.FlatAppearance.BorderSize = selected ? 0 : 1;
    }

    private void UpdateOutput()
    {
        outputTextBox.Text = currentTool.Convert(inputTextBox.Text);
        copyButton.Enabled = outputTextBox.TextLength > 0;
    }

    private void CopyButton_Click(object? sender, EventArgs e)
    {
        if (outputTextBox.TextLength == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(outputTextBox.Text);
        }
        catch (Exception ex) when (ex is ExternalException or ThreadStateException or InvalidOperationException)
        {
            MessageBox.Show(this, $"写入系统剪贴板失败：{ex.Message}", "复制失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        historyStore.Add(new HistoryEntry(
            currentTool.Id,
            inputTextBox.Text,
            outputTextBox.Text,
            DateTimeOffset.UtcNow));
        try
        {
            historyStore.Save();
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, $"历史记录保存失败：{ex.Message}", "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (allowClose || e.CloseReason != CloseReason.UserClosing)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

}
