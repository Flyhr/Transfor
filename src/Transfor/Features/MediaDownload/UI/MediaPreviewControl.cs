namespace Transfor;

// 媒体预览控件：显示图片预览或视频元数据占位
internal sealed class MediaPreviewControl : UserControl
{
    private readonly PictureBox pictureBox;
    private readonly Label infoLabel;

    public MediaPreviewControl()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));

        pictureBox = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.White,
        };
        root.Controls.Add(pictureBox, 0, 0);

        infoLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        root.Controls.Add(infoLabel, 0, 1);

        Controls.Add(root);
    }

    // 显示图片预览：Image.FromStream 后复制为独立 Bitmap，避免底层流关闭导致显示失败
    public void ShowImage(string imagePath)
    {
        using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var original = Image.FromStream(stream);
        var copy = new Bitmap(original);
        var old = pictureBox.Image;
        pictureBox.Image = copy;
        old?.Dispose();
        infoLabel.Text = Path.GetFileName(imagePath);
    }

    // 视频第一版只显示元数据，不自动播放
    public void ShowVideoInfo(MediaVariant variant)
    {
        pictureBox.Image = null;
        var size = variant.Width.HasValue && variant.Height.HasValue ? $"{variant.Width}×{variant.Height}" : "-";
        var fps = variant.FramesPerSecond.HasValue ? $"{variant.FramesPerSecond} fps" : "-";
        var bitrate = variant.Bitrate.HasValue ? $"{variant.Bitrate / 1000d:F0} kbps" : "-";
        infoLabel.Text = $"视频：{size}，{fps}，{bitrate}（第一版不支持预览播放）";
    }

    public void ShowError(string message)
    {
        pictureBox.Image = null;
        infoLabel.Text = message;
    }

    public void Clear()
    {
        var old = pictureBox.Image;
        pictureBox.Image = null;
        old?.Dispose();
        infoLabel.Text = string.Empty;
    }
}
