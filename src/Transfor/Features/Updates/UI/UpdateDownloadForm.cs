namespace Transfor;

// 更新下载窗体：展示下载进度（百分比 + 已下载/总量）与取消；
// 下载完成后提供「立即重启并更新」（可选更新另有「稍后重启」）；
// 强制更新下载完成后自动要求重启，不允许跳过
internal sealed class UpdateDownloadForm : Form
{
    public enum Result
    {
        // 用户主动取消（强制更新不允许跳过，由调用方回到强制更新循环）
        Cancelled,
        // 更新已应用并请求重启
        RestartNow,
        // 可选更新：用户选择稍后重启（更新已暂存）
        Later,
        // 下载/安装失败（与取消区分：失败后应允许重试或退出，而不是静默放过）
        Failed,
    }

    private readonly UpdateCheckResult updateResult;
    private readonly bool required;
    private readonly ProgressBar progressBar;
    private readonly Label statusLabel;
    private readonly Label sizeLabel;
    private readonly Button cancelButton;
    private readonly Button restartButton;
    private readonly TaskCompletionSource<Result> decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource cancellation = new();
    private bool completed;
    private bool failed;

    private UpdateDownloadForm(UpdateCheckResult result, bool required)
    {
        this.updateResult = result;
        this.required = required;

        Text = "正在更新";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(440, 210);
        Font = new Font("Microsoft YaHei UI", 10F);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        var title = new Label
        {
            Dock = DockStyle.Fill,
            Font = new Font(Font, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Text = $"正在下载 Transfor {result.LatestVersion}",
        };
        root.Controls.Add(title, 0, 0);

        progressBar = new ProgressBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, Style = ProgressBarStyle.Continuous };
        root.Controls.Add(progressBar, 0, 1);

        statusLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Text = "准备下载…" };
        root.Controls.Add(statusLabel, 0, 2);

        sizeLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Text = string.Empty };
        root.Controls.Add(sizeLabel, 0, 3);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        restartButton = new Button { Text = "立即重启并更新", AutoSize = true, Visible = false };
        restartButton.Click += (_, _) => decision.TrySetResult(Result.RestartNow);
        cancelButton = new Button { Text = "取消", AutoSize = true };
        cancelButton.Click += (_, _) => CancelRequested();
        buttons.Controls.Add(restartButton);
        buttons.Controls.Add(cancelButton);
        root.Controls.Add(buttons, 0, 4);

        Controls.Add(root);
        AcceptButton = restartButton;
        CancelButton = cancelButton;
    }

    public CancellationToken Cancellation => cancellation.Token;

    // 在 UI 线程调用：展示进度窗体并执行下载；返回用户动作（下载取消/失败 → Cancelled）
    public static async Task<Result> RunAsync(Form? owner, IUpdateInstaller installer, UpdateCheckResult result, bool required)
    {
        using var form = new UpdateDownloadForm(result, required);
        form.Show(owner);
        try
        {
            var progress = new Progress<UpdateDownloadProgress>(form.OnProgress);
            var version = await installer.DownloadAsync(progress, form.Cancellation).ConfigureAwait(true);
            form.OnCompleted(version);
            if (required)
            {
                // 强制更新：下载完成后必须立即重启应用，不允许稍后
                return Result.RestartNow;
            }

            return await form.decision.Task.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            form.OnCancelled();
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            form.OnFailed(ex.Message);
            // 失败原因在窗口内可见：等待用户确认（点「确定」或关闭窗口）后再返回，
            // 避免下载失败时窗口闪退、用户看不到任何错误提示
            return await form.decision.Task.ConfigureAwait(true);
        }
        finally
        {
            form.Close();
        }
    }

    // 下载进度回调（UI 线程）：更新进度条与大小文本
    private void OnProgress(UpdateDownloadProgress progress)
    {
        if (IsDisposed)
        {
            return;
        }

        progressBar.Value = Math.Clamp(progress.Percent, 0, 100);
        statusLabel.Text = $"正在下载… {progress.Percent}%";
        if (progress.BytesTotal > 0)
        {
            sizeLabel.Text = $"{FormatBytes(progress.BytesReceived)} / {FormatBytes(progress.BytesTotal)}";
        }
    }

    // 下载完成（UI 线程）：切换为重启提示
    private void OnCompleted(UpdateVersion version)
    {
        if (IsDisposed)
        {
            return;
        }

        completed = true;
        progressBar.Value = 100;
        statusLabel.Text = "更新已准备完成。";
        sizeLabel.Text = $"目标版本 {version}";
        restartButton.Visible = true;
        if (!required)
        {
            cancelButton.Text = "稍后重启";
        }
    }

    private void OnCancelled()
    {
        if (!IsDisposed)
        {
            statusLabel.Text = "下载已取消。";
        }
    }

    // 下载失败（UI 线程）：窗口内展示失败原因并等待用户确认，不自动关闭
    private void OnFailed(string message)
    {
        if (IsDisposed)
        {
            return;
        }

        failed = true;
        statusLabel.Text = $"下载失败：{message}";
        cancelButton.Text = "确定";
    }

    // 下载中取消；失败后点「确定」确认；下载完成后点「稍后重启」则暂存更新
    private void CancelRequested()
    {
        if (failed)
        {
            decision.TrySetResult(Result.Failed);
        }
        else if (!completed)
        {
            cancellation.Cancel();
        }
        else if (!required)
        {
            decision.TrySetResult(Result.Later);
        }
    }

    // 关闭窗体：失败视为已确认；下载中视为取消；已完成的可选更新视为稍后重启（更新已暂存，下次启动自动应用）
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (failed)
        {
            decision.TrySetResult(Result.Failed);
        }
        else if (!completed)
        {
            cancellation.Cancel();
            decision.TrySetResult(Result.Cancelled);
        }
        else if (!required)
        {
            decision.TrySetResult(Result.Later);
        }

        base.OnFormClosing(e);
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024
            ? $"{bytes / 1024.0 / 1024.0:0.0} MB"
            : $"{bytes / 1024.0:0.0} KB";
}
