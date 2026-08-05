namespace Transfor;

// 媒体状态存储：设置与下载历史独立于文本状态，持久化到 media-settings.json 与 download-history.json；
// 修改方法内部落盘并以私有锁串行化写入，保证并发批次不互相覆盖 JSON
internal sealed class MediaStateStore : IMediaDownloadHistoryRepository
{
    // 下载历史最多保留的批次数量
    public const int MaxHistoryBatches = 200;

    private readonly AppPaths paths;
    private readonly object sync = new();
    private MediaDownloadSettings settings;
    private readonly List<MediaDownloadHistoryEntry> history = new();

    private MediaStateStore(AppPaths paths, MediaDownloadSettings settings, IEnumerable<MediaDownloadHistoryEntry> history)
    {
        this.paths = paths;
        this.settings = settings;
        this.history.AddRange(history);
        TrimHistory();
    }

    public static MediaStateStore Load(AppPaths paths)
    {
        // 设置：缺失/损坏/版本不符时回退默认值；下载目录统一规范化为完整路径
        var settings = JsonFileStore.TryRead<MediaDownloadSettings>(paths.MediaSettingsFile);
        if (settings is null || !TryValidate(settings))
        {
            settings = MediaDownloadSettings.CreateDefault(paths.ApplicationDirectory);
        }
        else
        {
            settings = settings with { DownloadDirectory = Path.GetFullPath(settings.DownloadDirectory) };
        }

        // 历史：缺失/损坏时为空列表，超出上限裁剪最旧批次
        var history = JsonFileStore.TryRead<List<MediaDownloadHistoryEntry>>(paths.MediaDownloadHistoryFile) ?? new();
        return new MediaStateStore(paths, settings, history);
    }

    public MediaDownloadSettings Settings
    {
        get
        {
            lock (sync)
            {
                return settings;
            }
        }
    }

    // 更新设置并立即落盘（校验失败时不落盘）
    public void UpdateSettings(MediaDownloadSettings settings)
    {
        settings.Validate();
        lock (sync)
        {
            this.settings = settings with { DownloadDirectory = Path.GetFullPath(settings.DownloadDirectory) };
            TrimHistory();
            JsonFileStore.Write(paths.MediaSettingsFile, this.settings);
            SaveHistoryCore();
        }
    }

    public IReadOnlyList<MediaDownloadHistoryEntry> GetHistory()
    {
        lock (sync)
        {
            return history.ToArray();
        }
    }

    // 追加一条批次历史并立即落盘（超出上限裁剪最旧批次）
    public void Add(MediaDownloadHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (sync)
        {
            history.Add(entry);
            TrimHistory();
            SaveHistoryCore();
        }
    }

    private static bool TryValidate(MediaDownloadSettings settings)
    {
        try
        {
            settings.Validate();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void TrimHistory()
    {
        if (history.Count > MaxHistoryBatches)
        {
            history.RemoveRange(0, history.Count - MaxHistoryBatches);
        }
    }

    private void SaveHistoryCore() => JsonFileStore.Write(paths.MediaDownloadHistoryFile, history.ToArray());
}
