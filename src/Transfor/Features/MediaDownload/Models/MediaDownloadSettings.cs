namespace Transfor;

// 媒体下载设置
internal sealed record MediaDownloadSettings(
    string DownloadDirectory,
    int MaxConcurrentDownloads,
    bool DefaultSelectAll,
    bool OpenFolderAfterDownload,
    MediaQualityPreference QualityPreference,
    MediaNetworkMode NetworkMode = MediaNetworkMode.Direct,
    string ProxyAddress = "")
{
    public const int MinimumConcurrency = 1;
    public const int MaximumConcurrency = 8;
    public const int DefaultConcurrency = 3;
    public const bool DefaultSelectAllValue = true;
    public const bool DefaultOpenFolderAfterDownload = false;
    public const MediaQualityPreference DefaultQualityPreference = MediaQualityPreference.Highest;
    public const MediaNetworkMode DefaultNetworkMode = MediaNetworkMode.Direct;

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
            DefaultNetworkMode);
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

        if (!Enum.IsDefined(NetworkMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(NetworkMode));
        }

        if (NetworkMode == MediaNetworkMode.CustomProxy)
        {
            if (string.IsNullOrWhiteSpace(ProxyAddress)
                || !Uri.TryCreate(ProxyAddress.Trim(), UriKind.Absolute, out var proxyUri)
                || proxyUri.Scheme is not ("http" or "https" or "socks4" or "socks5")
                || string.IsNullOrEmpty(proxyUri.Host))
            {
                throw new ArgumentException(
                    "指定代理模式需要有效的代理地址（如 http://127.0.0.1:7897）。",
                    nameof(ProxyAddress));
            }
        }

        if (!Enum.IsDefined(QualityPreference))
        {
            throw new ArgumentOutOfRangeException(
                nameof(QualityPreference));
        }
    }
}
