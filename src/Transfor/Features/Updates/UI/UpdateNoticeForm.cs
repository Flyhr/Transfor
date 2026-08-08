namespace Transfor;

// 更新提示窗体：可选更新（发现新版本 + 更新内容 + 稍后/立即）
// 与强制更新（停止支持说明 + 版本信息 + 重新检查/立即/退出）；
// Phase 2 接入 Velopack：「立即更新」由调用方执行真实下载安装
internal sealed class UpdateNoticeForm : Form
{
    public enum UserAction
    {
        Later,
        UpdateNow,
        Recheck,
        Exit,
    }

    private readonly bool required;

    internal UpdateNoticeForm(UpdateCheckResult result, bool required)
    {
        this.required = required;

        Text = required ? "需要更新" : $"发现新版本 {result.LatestVersion}";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(480, 320);
        Font = new Font("Microsoft YaHei UI", 10F);

        // 根布局：标题 /（可选）更新内容 / 按钮行
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        // 标题区（加粗）：普通显示最新版本；强制显示停止支持说明与当前/最新版本
        var title = new Label
        {
            Dock = DockStyle.Fill,
            Font = new Font(Font, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(0, 0, 0, 8),
            Text = required
                ? $"当前版本已停止支持。{Environment.NewLine}当前版本：{result.CurrentVersion}　　最新版本：{result.LatestVersion}"
                : $"发现新版本 {result.LatestVersion}",
        };
        root.Controls.Add(title, 0, 0);

        if (required)
        {
            // 强制更新：无内容区（标题 + 按钮两行）
            root.RowCount = 2;
            root.RowStyles.RemoveAt(1);
        }
        else
        {
            // 可选更新：更新内容（发布说明 + 变更列表）
            var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            content.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "更新内容：",
                Padding = new Padding(0, 0, 0, 4),
            }, 0, 0);

            var bodyParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(result.Policy?.Message))
            {
                bodyParts.Add(result.Policy.Message);
            }

            if (result.Policy?.Changelog is { Count: > 0 })
            {
                bodyParts.Add(string.Join(Environment.NewLine, result.Policy.Changelog.Take(8).Select(line => $"• {line}")));
            }

            content.Controls.Add(new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.FixedSingle,
                Text = bodyParts.Count > 0 ? string.Join(Environment.NewLine + Environment.NewLine, bodyParts) : "暂无更新说明。",
            }, 0, 1);
            root.Controls.Add(content, 0, 1);
        }

        // 按钮行（RightToLeft 布局，第一个添加的控件在最右）：
        // 普通 = [稍后更新][立即更新]；强制 = [重新检查][立即更新][退出]（左→右显示）
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        Button? laterButton = null;
        Button? exitButton = null;
        var updateNowButton = new Button { Text = "立即更新", AutoSize = true, DialogResult = DialogResult.OK };
        if (required)
        {
            exitButton = new Button { Text = "退出", AutoSize = true, DialogResult = DialogResult.Abort };
            var recheckButton = new Button { Text = "重新检查", AutoSize = true, DialogResult = DialogResult.Retry };
            buttons.Controls.Add(exitButton);
            buttons.Controls.Add(updateNowButton);
            buttons.Controls.Add(recheckButton);
        }
        else
        {
            laterButton = new Button { Text = "稍后更新", AutoSize = true, DialogResult = DialogResult.No };
            buttons.Controls.Add(updateNowButton);
            buttons.Controls.Add(laterButton);
        }

        root.Controls.Add(buttons, 0, required ? 1 : 2);

        // 根布局挂载到窗体（此前缺失导致窗口内容为空）
        Controls.Add(root);

        AcceptButton = updateNowButton;
        CancelButton = laterButton ?? exitButton;
    }

    public static UserAction ShowOptional(Form? owner, UpdateCheckResult result)
    {
        using var form = new UpdateNoticeForm(result, required: false);
        return form.Map(form.ShowDialog(owner));
    }

    public static UserAction ShowRequired(Form? owner, UpdateCheckResult result)
    {
        using var form = new UpdateNoticeForm(result, required: true);
        return form.Map(form.ShowDialog(owner));
    }

    // 关闭/ESC 等未定义结果：可选更新视为稍后，强制更新视为退出（不允许被静默跳过）
    private UserAction Map(DialogResult dialogResult) => dialogResult switch
    {
        DialogResult.OK => UserAction.UpdateNow,
        DialogResult.No => UserAction.Later,
        DialogResult.Retry => UserAction.Recheck,
        DialogResult.Abort => UserAction.Exit,
        _ => required ? UserAction.Exit : UserAction.Later,
    };
}
