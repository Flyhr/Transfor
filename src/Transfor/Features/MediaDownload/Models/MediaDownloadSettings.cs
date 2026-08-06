namespace Transfor;

// 媒体下载设置
internal sealed record MediaDownloadSettings(
    string DownloadDirectory,
    int MaxConcurrentDownloads,
    bool DefaultSelectAll,
    bool OpenFolderAfterDownload,
    MediaQualityPreference QualityPreference,
    bool UseProxy = false)
{
    public const int MinimumConcurrency = 1;
    public const int MaximumConcurrency = 8;
    public const int DefaultConcurrency = 3;
    public const bool DefaultSelectAllValue = true;
    public const bool DefaultOpenFolderAfterDownload = false;
    public const MediaQualityPreference DefaultQualityPreference = MediaQualityPreference.Highest;
    public const bool DefaultUseProxy = false;

    // 创建默认设置：优先使用用户 Downloads 目录，缺失时回退到 fallbackDirectory
    public static MediaDownloadSettings CreateDefault(
        string fallbackDirectory,
        string? downloadsDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackDirectory);

        downloadsDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");

        var directory = Directory.Exists(downloadsDirectory)
            ? Path.GetFullPath(downloadsDirectory)
            : Path.GetFullPath(fallbackDirectory);

        return new MediaDownloadSettings(
            directory,
            DefaultConcurrency,
            DefaultSelectAllValue,
            DefaultOpenFolderAfterDownload,
            DefaultQualityPreference,
            DefaultUseProxy);
    }

    public void Validate()
    {
        if (MaxConcurrentDownloads is < MinimumConcurrency or > MaximumConcurrency)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrentDownloads),
                "并发数必须在 1 到 8 之间。");
        }

        if (string.IsNullOrWhiteSpace(DownloadDirectory))
        {
            throw new ArgumentException(
                "下载目录不能为空。",
                nameof(DownloadDirectory));
        }

        if (!Path.IsPathFullyQualified(DownloadDirectory))
        {
            throw new ArgumentException(
                "下载目录必须是绝对路径。",
                nameof(DownloadDirectory));
        }

        if (!Enum.IsDefined(QualityPreference))
        {
            throw new ArgumentOutOfRangeException(
                nameof(QualityPreference));
        }
    }
}