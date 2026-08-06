namespace Transfor;

// 资产列表控件：DataGridView 展示作品的媒体资产（勾选/序号/类型/尺寸/预计大小/质量状态/预览占位）
internal sealed class MediaAssetGrid : UserControl
{
    private readonly DataGridView grid;

    public MediaAssetGrid()
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
        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "选择", Width = 48, FillWeight = 10 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "序号", FillWeight = 10 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "类型", FillWeight = 15 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "尺寸", FillWeight = 25 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "预计大小", FillWeight = 20 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "质量状态", FillWeight = 25 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "预览", FillWeight = 15 });
        grid.CellContentClick += Grid_CellContentClick;
        Controls.Add(grid);
    }

    // 预览请求：仅可下载（携带变体）的行触发
    public event EventHandler<(MediaAsset Asset, MediaVariant Variant)>? PreviewRequested;

    public bool HasAssets => grid.Rows.Count > 0;

    // 测试入口：读取指定行列的单元格值
    internal object? GetCellValueForTest(int rowIndex, int columnIndex)
        => grid.Rows[rowIndex].Cells[columnIndex].Value;

    private void Grid_CellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != 6)
        {
            return;
        }

        if (grid.Rows[e.RowIndex].Tag is (MediaAsset asset, MediaVariant variant) && variant is not null)
        {
            PreviewRequested?.Invoke(this, (asset, variant));
        }
    }

    // 展示作品资产：每个资产一个行；仅 Selected 状态的行携带可下载变体并参与勾选
    public void LoadPost(ResolvedMediaPost post, IReadOnlyList<MediaSelectionResult> selections, bool defaultSelectAll)
    {
        grid.Rows.Clear();
        for (var i = 0; i < post.Assets.Count; i++)
        {
            var asset = post.Assets[i];
            var selection = i < selections.Count ? selections[i] : null;
            var variant = selection?.Variant;

            var row = new DataGridViewRow();
            row.Tag = (asset, variant);
            row.Cells.Add(new DataGridViewCheckBoxCell { Value = defaultSelectAll && selection?.Status == MediaSelectionStatus.Selected && variant is not null });
            row.Cells.Add(new DataGridViewTextBoxCell { Value = (i + 1).ToString("D2") });
            row.Cells.Add(new DataGridViewTextBoxCell { Value = asset.Kind == MediaKind.Image ? "图片" : "视频" });
            row.Cells.Add(new DataGridViewTextBoxCell
            {
                Value = variant is not null && variant.Width.HasValue && variant.Height.HasValue
                    ? $"{variant.Width}×{variant.Height}"
                    : "-",
            });
            row.Cells.Add(new DataGridViewTextBoxCell { Value = variant?.ContentLength is long length ? FormatSize(length) : "-" });
            row.Cells.Add(new DataGridViewTextBoxCell
            {
                Value = selection?.Status switch
                {
                    MediaSelectionStatus.Selected => "可下载",
                    MediaSelectionStatus.UnsupportedSegmented => "分段不支持",
                    _ => "无可用变体",
                },
            });
            row.Cells.Add(new DataGridViewTextBoxCell { Value = variant is not null ? "预览" : "-" });
            grid.Rows.Add(row);
        }
    }

    public void Clear() => grid.Rows.Clear();

    public void SelectAll()
    {
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.Tag is (MediaAsset, MediaVariant { }))
            {
                row.Cells[0].Value = true;
            }
        }
    }

    public void UnselectAll()
    {
        foreach (DataGridViewRow row in grid.Rows)
        {
            row.Cells[0].Value = false;
        }
    }

    // 返回勾选且可下载的 (资产, 变体)
    public IReadOnlyList<(MediaAsset Asset, MediaVariant Variant)> GetSelected()
    {
        var result = new List<(MediaAsset, MediaVariant)>();
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.Tag is (MediaAsset asset, MediaVariant variant) && variant is not null && row.Cells[0].Value is true)
            {
                result.Add((asset, variant));
            }
        }
        return result;
    }

    // 下载开始时回填「预计大小」（响应头 Content-Length）
    public void UpdateEstimatedSize(Uri variantUri, long bytes)
    {
        if (FindRowByVariant(variantUri) is not { } row)
        {
            return;
        }

        row.Cells[4].Value = FormatSize(bytes);
    }

    // 下载完成后回填「尺寸」与「预计大小」（实际文件信息）
    public void UpdateFileInfo(Uri variantUri, long bytes, int? width, int? height)
    {
        if (FindRowByVariant(variantUri) is not { } row)
        {
            return;
        }

        row.Cells[4].Value = FormatSize(bytes);
        if (width.HasValue && height.HasValue)
        {
            row.Cells[3].Value = $"{width}×{height}";
        }
    }

    private DataGridViewRow? FindRowByVariant(Uri variantUri)
    {
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.Tag is (MediaAsset, MediaVariant variant) && variant is not null
                && string.Equals(variant.Uri.ToString(), variantUri.ToString(), StringComparison.Ordinal))
            {
                return row;
            }
        }
        return null;
    }

    private static string FormatSize(long bytes)
    {
        const double mb = 1024 * 1024;
        const double gb = 1024 * 1024 * 1024;
        return bytes >= gb ? $"{bytes / gb:F1} GB" : $"{bytes / mb:F1} MB";
    }
}
