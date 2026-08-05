namespace Transfor;

internal sealed class HistoryPanelForm : Form
{
    private readonly TextStateStore historyStore;
    private readonly PasteCoordinator pasteCoordinator;
    private readonly Button quoteButton;
    private readonly Button spaceButton;
    private readonly ListBox historyList;
    private readonly Label errorLabel;
    private TextToolId currentTool;
    private nint targetWindow;
    private bool allowClose;

    public HistoryPanelForm(TextStateStore historyStore, PasteCoordinator pasteCoordinator)
    {
        this.historyStore = historyStore;
        this.pasteCoordinator = pasteCoordinator;
        currentTool = historyStore.UiState.LastViewedTool;

        Text = "Transfor 历史记录";
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        ClientSize = new Size(620, 500);
        Font = new Font("Microsoft YaHei UI", 10F);

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

        var nav = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        quoteButton = CreateToolButton("引号转换");
        quoteButton.Click += (_, _) => SelectTool(TextToolId.QuoteConversion);
        spaceButton = CreateToolButton("去除空格");
        spaceButton.Click += (_, _) => SelectTool(TextToolId.SpaceRemoval);
        nav.Controls.Add(quoteButton);
        nav.Controls.Add(spaceButton);

        historyList = new ListBox
        {
            Dock = DockStyle.Fill,
            HorizontalScrollbar = true,
            IntegralHeight = false,
        };
        historyList.MouseClick += (_, _) => ExecuteSelected();
        historyList.KeyDown += HistoryList_KeyDown;

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

    public void ShowFor(nint foregroundWindow)
    {
        targetWindow = foregroundWindow;
        errorLabel.Text = string.Empty;
        SelectTool(historyStore.UiState.LastViewedTool);
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

    private void SelectTool(TextToolId tool)
    {
        currentTool = tool;
        ApplyButtonState(quoteButton, tool == TextToolId.QuoteConversion);
        ApplyButtonState(spaceButton, tool == TextToolId.SpaceRemoval);
        RefreshHistory();
        if (historyStore.UiState.LastViewedTool != tool)
        {
            historyStore.SetLastViewedTool(tool);
            try
            {
                historyStore.Save();
            }
            catch (IOException ex)
            {
                errorLabel.Text = $"保存当前分类失败：{ex.Message}";
            }
        }
    }

    private static void ApplyButtonState(Button button, bool selected)
    {
        button.BackColor = selected ? Color.FromArgb(31, 111, 235) : Color.FromArgb(242, 245, 249);
        button.ForeColor = selected ? Color.White : Color.FromArgb(35, 40, 48);
        button.FlatAppearance.BorderSize = selected ? 0 : 1;
    }

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

    private void HistoryPanelForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private sealed record HistoryListItem(HistoryEntry Entry)
    {
        public override string ToString()
        {
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
