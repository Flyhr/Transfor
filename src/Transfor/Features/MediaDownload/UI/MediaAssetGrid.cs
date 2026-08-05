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
        Controls.Add(grid);
    }

    public bool HasAssets => grid.Rows.Count > 0;

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

    private static string FormatSize(long bytes)
    {
        const double mb = 1024 * 1024;
        const double gb = 1024 * 1024 * 1024;
        return bytes >= gb ? $"{bytes / gb:F1} GB" : $"{bytes / mb:F1} MB";
    }
}
