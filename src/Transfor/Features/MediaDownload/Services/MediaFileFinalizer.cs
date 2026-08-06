namespace Transfor;

// 媒体文件落盘终化：校验魔数 → 目标存在且内容相同则幂等成功；
// 内容不同或不存在则原子移动（竞态时重新生成唯一路径），绝不静默覆盖；
// HttpClient 下载与浏览器兜底下载共用
internal static class MediaFileFinalizer
{
    // 返回保存路径；失败时返回错误并保留 .part 文件（由调用方决定清理）
    public static async Task<(string? SavedPath, string? Error)> TryFinalizeAsync(
        string partPath,
        string targetPath,
        MediaKind kind,
        CancellationToken cancellationToken)
    {
        string partHash;
        try
        {
            using var partStream = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!await MediaContentValidator.HasValidMagicNumberAsync(partStream, kind, cancellationToken))
            {
                return (null, "下载内容不是有效的媒体文件。");
            }

            partStream.Position = 0;
            partHash = await MediaHashService.ComputeSha256Async(partStream, cancellationToken);

            if (File.Exists(targetPath))
            {
                // 目标存在且哈希相同：删除临时文件，幂等成功
                using var targetStream = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var targetHash = await MediaHashService.ComputeSha256Async(targetStream, cancellationToken);
                if (string.Equals(targetHash, partHash, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(partPath);
                    return (targetPath, null);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return (null, ex.Message);
        }

        var savedPath = MoveWithUniqueFallback(targetPath, partPath);
        return (savedPath, null);
    }

    // 独占移动：目标被并发任务创建时重新生成 (1)(2) 后缀，不覆盖其他任务文件
    public static string MoveWithUniqueFallback(string targetPath, string partPath)
    {
        var directory = Path.GetDirectoryName(targetPath)!;
        var fileName = Path.GetFileName(targetPath);
        var attempt = 0;
        while (true)
        {
            var candidate = attempt == 0
                ? targetPath
                : DownloadFileNameBuilder.BuildUniquePath(directory, fileName);
            try
            {
                File.Move(partPath, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate) && attempt < 10)
            {
                attempt++;
            }
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不掩盖原始结果
        }
    }
}
