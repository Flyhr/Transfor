namespace Transfor;

// 媒体设置窗体：默认下载目录、最大并发、默认全选、下载后打开目录、质量策略；
// 保存通过 MediaStateStore.UpdateSettings 内部持久化
internal sealed class MediaSettingsForm : Form
{
    private readonly MediaStateStore stateStore;
    private readonly TextBox directoryBox;
    private readonly NumericUpDown concurrencyBox;
    private readonly CheckBox selectAllBox;
    private readonly CheckBox openFolderBox;
    private readonly ComboBox qualityBox;
    private readonly Label errorLabel;

    public MediaSettingsForm(MediaStateStore stateStore)
    {
        this.stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));

        Text = "媒体下载设置";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(460, 320);
        Font = new Font("Microsoft YaHei UI", 10F);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 6 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        // 默认下载目录
        root.Controls.Add(new Label { Text = "默认下载目录", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        var directoryRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        directoryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        directoryRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        directoryBox = new TextBox { Dock = DockStyle.Fill, Text = stateStore.Settings.DownloadDirectory };
        var browseButton = new Button { AutoSize = true, Text = "选择…" };
        browseButton.Click += (_, _) => BrowseDirectory();
        directoryRow.Controls.Add(directoryBox, 0, 0);
        directoryRow.Controls.Add(browseButton, 1, 0);
        root.Controls.Add(directoryRow, 1, 0);

        // 最大并发
        root.Controls.Add(new Label { Text = "最大并发", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        concurrencyBox = new NumericUpDown
        {
            Minimum = MediaDownloadSettings.MinimumConcurrency,
            Maximum = MediaDownloadSettings.MaximumConcurrency,
            Value = stateStore.Settings.MaxConcurrentDownloads,
            Width = 120,
        };
        root.Controls.Add(concurrencyBox, 1, 1);

        // 默认全选
        selectAllBox = new CheckBox { Text = "解析后默认全选", Checked = stateStore.Settings.DefaultSelectAll, AutoSize = true };
        root.Controls.Add(selectAllBox, 1, 2);

        // 下载后打开目录
        openFolderBox = new CheckBox { Text = "下载完成后打开目录", Checked = stateStore.Settings.OpenFolderAfterDownload, AutoSize = true };
        root.Controls.Add(openFolderBox, 1, 3);

        // 质量策略
        root.Controls.Add(new Label { Text = "质量策略", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 4);
        qualityBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        qualityBox.Items.Add(new QualityOption("最高质量", MediaQualityPreference.Highest));
        qualityBox.Items.Add(new QualityOption("平衡（优先 720p）", MediaQualityPreference.Balanced));
        qualityBox.SelectedItem = qualityBox.Items.OfType<QualityOption>().First(o => o.Value == stateStore.Settings.QualityPreference);
        root.Controls.Add(qualityBox, 1, 4);

        errorLabel = new Label { Dock = DockStyle.Fill, ForeColor = Color.Firebrick, AutoSize = false, TextAlign = ContentAlignment.TopLeft };
        root.Controls.Add(errorLabel, 0, 5);
        root.SetColumnSpan(errorLabel, 2);

        Controls.Add(root);

        var saveButton = new Button { Text = "保存", AutoSize = true };
        saveButton.Click += (_, _) => SaveAndClose();
        AcceptButton = saveButton;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(16, 8, 16, 8) };
        buttons.Controls.Add(saveButton);
        Controls.Add(buttons);
    }

    // 测试入口：从当前控件组合设置（与保存逻辑一致）
    internal MediaDownloadSettings ComposeFromControls()
    {
        var quality = (QualityOption)qualityBox.SelectedItem;
        return new MediaDownloadSettings(
            directoryBox.Text,
            (int)concurrencyBox.Value,
            selectAllBox.Checked,
            openFolderBox.Checked,
            quality.Value);
    }

    private void BrowseDirectory()
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = directoryBox.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            directoryBox.Text = dialog.SelectedPath;
        }
    }

    private void SaveAndClose()
    {
        errorLabel.Text = string.Empty;
        try
        {
            var settings = ComposeFromControls();
            settings.Validate();
            stateStore.UpdateSettings(settings);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException or IOException or UnauthorizedAccessException)
        {
            errorLabel.Text = ex.Message;
        }
    }

    private sealed record QualityOption(string Name, MediaQualityPreference Value)
    {
        public override string ToString() => Name;
    }
}
