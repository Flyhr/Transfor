namespace Transfor;

// 下载队列控件：DataGridView 展示任务的文件名/进度/状态，并提供取消与重试操作
internal sealed class DownloadQueueGrid : UserControl
{
    private readonly DataGridView grid;

    public DownloadQueueGrid()
    {
        grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "文件名", FillWeight = 45 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "进度", FillWeight = 15 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", FillWeight = 20 });
        grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "操作", FillWeight = 20, Text = "", UseColumnTextForButtonValue = false });
        grid.CellContentClick += Grid_CellContentClick;
        Controls.Add(grid);
    }

    // 操作请求：Guid 为任务 ID，bool 为是否重试（false 为取消）
    public event EventHandler<(Guid TaskId, bool Retry)>? OperationRequested;

    public void AddTask(MediaDownloadTask task)
    {
        var row = new DataGridViewRow();
        row.Tag = task;
        row.Cells.Add(new DataGridViewTextBoxCell { Value = Path.GetFileName(task.TargetPath) });
        row.Cells.Add(new DataGridViewTextBoxCell { Value = "0%" });
        row.Cells.Add(new DataGridViewTextBoxCell { Value = "排队" });
        row.Cells.Add(new DataGridViewButtonCell { Value = "取消" });
        grid.Rows.Add(row);
    }

    public void UpdateProgress(Guid taskId, double? percent)
    {
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.Tag is MediaDownloadTask { Id: var id } && id == taskId)
            {
                row.Cells[1].Value = percent.HasValue ? $"{percent.Value:F0}%" : "...";
                break;
            }
        }
    }

    public void CompleteTask(Guid taskId, MediaDownloadStatus status, string? error)
    {
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.Tag is MediaDownloadTask { Id: var id } && id == taskId)
            {
                row.Cells[2].Value = status switch
                {
                    MediaDownloadStatus.Succeeded => "成功",
                    MediaDownloadStatus.Failed => $"失败：{error}",
                    _ => "已取消",
                };
                row.Cells[3].Value = status == MediaDownloadStatus.Failed ? "重试" : string.Empty;
                break;
            }
        }
    }

    public void Clear() => grid.Rows.Clear();

    // 按任务 ID 查找原始任务（供重试使用）
    public MediaDownloadTask? FindTask(Guid taskId)
    {
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.Tag is MediaDownloadTask { Id: var id } && id == taskId)
            {
                return (MediaDownloadTask)row.Tag;
            }
        }
        return null;
    }

    private void Grid_CellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != 3)
        {
            return;
        }

        if (grid.Rows[e.RowIndex].Tag is MediaDownloadTask task)
        {
            var value = grid.Rows[e.RowIndex].Cells[3].Value as string;
            if (value == "重试")
            {
                OperationRequested?.Invoke(this, (task.Id, true));
            }
            else if (value == "取消")
            {
                OperationRequested?.Invoke(this, (task.Id, false));
            }
        }
    }
}
