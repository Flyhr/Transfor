namespace Transfor;

// 应用日志（Phase 7 Task 7.1）：分类文件日志——%TEMP%\Transfor\logs\，按天分文件，
// 1MB 轮转保留近 5 个备份；五类分类（application/update/browser/media/download）；
// 安全约定：敏感数据（Cookie/Token/完整认证头）禁止写入日志——方法签名不接收此类参数，
// 调用方负责不传入
internal static class AppLog
{
    private const long MaxFileBytes = 1 * 1024 * 1024;
    private const int MaxBackupFiles = 5;
    private static readonly object Sync = new();

    public static CategoryWriter Application => new("application");
    public static CategoryWriter Update => new("update");
    public static CategoryWriter Browser => new("browser");
    public static CategoryWriter MediaResolve => new("media-resolve");
    public static CategoryWriter Download => new("download");

    // 单类别写入器（按级别记录）
    internal readonly record struct CategoryWriter(string Category)
    {
        public void Info(string message) => Write(Category, "INFO", message);
        public void Warn(string message) => Write(Category, "WARN", message);
        public void Error(string message, Exception? exception = null) => Write(Category, "ERROR", message, exception);
    }

    // 写入日志（失败静默，不影响主流程）；超限时轮转备份
    internal static void Write(string category, string level, string message, Exception? exception = null)
    {
        try
        {
            lock (Sync)
            {
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{category}] {message}";
                if (exception is not null)
                {
                    line += " | " + exception;
                }

                var file = CurrentFile(category);
                File.AppendAllText(file, line + Environment.NewLine);
                if (new FileInfo(file).Length > MaxFileBytes)
                {
                    Rotate(file);
                }
            }
        }
        catch
        {
            // 日志写入失败不影响主流程
        }
    }

    // 当前日志文件：%TEMP%\Transfor\logs\{category}-yyyyMMdd.log
    internal static string CurrentFile(string category)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Transfor", "logs");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{category}-{DateTime.Now:yyyyMMdd}.log");
    }

    // 滚动备份：.4→.5 … .1→.2，当前→.1（保留最近 MaxBackupFiles 个）
    private static void Rotate(string file)
    {
        for (var i = MaxBackupFiles - 1; i >= 1; i--)
        {
            var source = file + "." + i;
            var target = file + "." + (i + 1);
            if (File.Exists(source))
            {
                File.Move(source, target, overwrite: true);
            }
        }

        File.Move(file, file + ".1", overwrite: true);
    }
}
