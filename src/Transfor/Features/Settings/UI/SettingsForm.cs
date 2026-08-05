using System.Windows.Forms;

namespace Transfor;

// 设置窗口：配置全局快捷键、两功能的历史上限，并可清空对应历史
internal sealed class SettingsForm : Form
{
    private readonly TextStateStore historyStore;
    private readonly GlobalHotKeyManager hotKeyManager;
    private readonly CheckedListBox modifiersBox;
    private readonly ComboBox keyBox;
    private readonly NumericUpDown quoteLimitBox;
    private readonly NumericUpDown spaceLimitBox;
    private readonly Label errorLabel;
    private readonly TextToolId quoteTool = TextToolId.QuoteConversion;
    private readonly TextToolId spaceTool = TextToolId.SpaceRemoval;

    public SettingsForm(TextStateStore historyStore, GlobalHotKeyManager hotKeyManager)
    {
        this.historyStore = historyStore;
        this.hotKeyManager = hotKeyManager;

        // 固定大小的模态对话框
        Text = "设置";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, 390);
        Font = new Font("Microsoft YaHei UI", 10F);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 2,
            RowCount = 7,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));

        // 快捷键区域：修饰键复选列表 + 主键下拉框
        root.Controls.Add(new Label { Text = "历史面板快捷键", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);

        var hotKeyPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        modifiersBox = new CheckedListBox { CheckOnClick = true, Height = 76, Width = 170 };
        modifiersBox.Items.Add(new ModifierOption("Ctrl", Keys.Control));
        modifiersBox.Items.Add(new ModifierOption("Alt", Keys.Alt));
        modifiersBox.Items.Add(new ModifierOption("Shift", Keys.Shift));
        modifiersBox.Items.Add(new ModifierOption("Win", Keys.LWin));
        keyBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        foreach (var key in GetOrdinaryKeys())
        {
            keyBox.Items.Add(key);
        }
        hotKeyPanel.Controls.Add(modifiersBox);
        hotKeyPanel.Controls.Add(keyBox);
        root.Controls.Add(hotKeyPanel, 1, 0);
        root.SetRowSpan(hotKeyPanel, 2);

        // 两种工具的历史上限
        root.Controls.Add(new Label { Text = "引号转换历史上限", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
        quoteLimitBox = CreateLimitBox(historyStore.Settings.QuoteHistoryLimit);
        root.Controls.Add(quoteLimitBox, 1, 2);

        root.Controls.Add(new Label { Text = "去除空格历史上限", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 3);
        spaceLimitBox = CreateLimitBox(historyStore.Settings.SpaceHistoryLimit);
        root.Controls.Add(spaceLimitBox, 1, 3);

        // 清空历史按钮（各自独立，带确认提示）
        var clearPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var clearQuoteButton = new Button { AutoSize = true, Text = "清空引号转换历史" };
        clearQuoteButton.Click += (_, _) => ClearHistory(quoteTool, "引号转换");
        var clearSpaceButton = new Button { AutoSize = true, Text = "清空去除空格历史" };
        clearSpaceButton.Click += (_, _) => ClearHistory(spaceTool, "去除空格");
        clearPanel.Controls.Add(clearQuoteButton);
        clearPanel.Controls.Add(clearSpaceButton);
        root.Controls.Add(clearPanel, 1, 4);

        // 校验/保存错误提示
        errorLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            ForeColor = Color.Firebrick,
            TextAlign = ContentAlignment.TopLeft,
        };
        root.Controls.Add(errorLabel, 0, 5);
        root.SetColumnSpan(errorLabel, 2);

        // 底部按钮：取消 / 保存
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancelButton = new Button { AutoSize = true, Text = "取消", DialogResult = DialogResult.Cancel };
        var saveButton = new Button { AutoSize = true, Text = "保存", DialogResult = DialogResult.None };
        saveButton.Click += SaveButton_Click;
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(saveButton);
        root.Controls.Add(buttons, 0, 6);
        root.SetColumnSpan(buttons, 2);

        Controls.Add(root);
        AcceptButton = saveButton;
        CancelButton = cancelButton;
        // 打开时用当前设置填充快捷键控件
        Load += (_, _) => LoadHotKey(historyStore.Settings.HistoryHotKey);
    }

    // 候选主键：排除修饰键、功能键区与组合键值，得到可选的普通按键列表
    private static IEnumerable<Keys> GetOrdinaryKeys()
    {
        return Enum.GetValues<Keys>()
            .Where(key => key != Keys.None
                && key != Keys.LWin
                && key != Keys.RWin
                && (key & Keys.Modifiers) == 0
                && (key & Keys.KeyCode) >= Keys.Back
                && (key & Keys.KeyCode) is not (Keys.ShiftKey or Keys.ControlKey or Keys.Menu))
            .Distinct()
            .OrderBy(key => key.ToString(), StringComparer.OrdinalIgnoreCase);
    }

    // 创建历史上限输入框（1–500）
    private static NumericUpDown CreateLimitBox(int value)
    {
        return new NumericUpDown
        {
            Dock = DockStyle.Left,
            Maximum = AppSettings.MaximumHistoryLimit,
            Minimum = AppSettings.MinimumHistoryLimit,
            Value = value,
            Width = 120,
        };
    }

    // 把当前保存的快捷键回填到控件
    private void LoadHotKey(HotKeyBinding binding)
    {
        for (var i = 0; i < modifiersBox.Items.Count; i++)
        {
            var option = (ModifierOption)modifiersBox.Items[i];
            modifiersBox.SetItemChecked(i, binding.Modifiers.HasFlag(option.Value));
        }

        keyBox.SelectedItem = binding.Key;
        // 保存的键不在候选列表中时回退为 Q
        if (keyBox.SelectedIndex < 0)
        {
            keyBox.SelectedIndex = keyBox.Items.IndexOf(Keys.Q);
        }
    }

    // 保存：先注册新热键成功，再持久化设置；保存失败则回滚热键
    private void SaveButton_Click(object? sender, EventArgs e)
    {
        errorLabel.Text = string.Empty;
        try
        {
            // 收集勾选的修饰键
            var modifiers = Keys.None;
            foreach (ModifierOption option in modifiersBox.CheckedItems)
            {
                modifiers |= option.Value;
            }

            if (keyBox.SelectedItem is not Keys key)
            {
                throw new ArgumentException("请选择普通按键。", nameof(key));
            }

            // 组装并校验新设置
            var hotKey = HotKeyBinding.Create(modifiers, key);
            var nextSettings = historyStore.Settings with
            {
                HistoryHotKey = hotKey,
                QuoteHistoryLimit = (int)quoteLimitBox.Value,
                SpaceHistoryLimit = (int)spaceLimitBox.Value,
            };
            nextSettings.Validate();
            var oldSettings = historyStore.Settings;
            // 先替换系统级热键；被占用时提示错误并中止
            if (!hotKeyManager.TryReplace(hotKey, out var hotKeyError))
            {
                errorLabel.Text = hotKeyError;
                return;
            }

            // 持久化设置（UpdateSettings 内部已落盘）；失败时把热键回滚为旧值
            try
            {
                historyStore.UpdateSettings(nextSettings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                hotKeyManager.TryReplace(oldSettings.HistoryHotKey, out _);
                errorLabel.Text = $"设置保存失败：{ex.Message}";
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            errorLabel.Text = ex.Message;
        }
    }

    // 清空指定工具的历史：确认后清空并保存
    private void ClearHistory(TextToolId tool, string displayName)
    {
        if (MessageBox.Show(this, $"确定清空“{displayName}”的全部历史记录吗？", "确认清空", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        // 清空指定工具的历史（ClearHistory 内部已落盘）
        try
        {
            historyStore.ClearHistory(tool);
            errorLabel.Text = string.Empty;
        }
        catch (IOException ex)
        {
            errorLabel.Text = $"历史清空后保存失败：{ex.Message}";
        }
    }

    // 修饰键选项：显示名 + 对应的 Keys 值
    private sealed record ModifierOption(string Name, Keys Value)
    {
        public override string ToString() => Name;
    }
}
