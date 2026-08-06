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
                // 临时诊断：魔数校验失败时保存文件头部样本，便于定位真实格式（定位后移除）
                SaveFailureSample(partPath, kind);
                return (null, "下载内容不是有效的媒体文件。");
            }

            partStream.Position = 0;
            partHash = await MediaHashService.ComputeSha256Async(partStream, cancellationToken);

            // 泛化扩展名（.img/.bin，URL 无法推断时的兜底）按实际内容魔数修正为标准扩展名
            partStream.Position = 0;
            targetPath = await CorrectGenericExtensionAsync(partStream, targetPath, kind, cancellationToken);

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

    // 目标扩展名为泛化类型时，按文件实际格式修正为真实扩展名
    private static async Task<string> CorrectGenericExtensionAsync(
        Stream partStream,
        string targetPath,
        MediaKind kind,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(targetPath);
        if (extension is not (".img" or ".bin"))
        {
            return targetPath;
        }

        var detected = await MediaContentValidator.DetectExtensionAsync(partStream, kind, cancellationToken);
        return detected is null ? targetPath : Path.ChangeExtension(targetPath, detected);
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

    // 临时诊断：保存校验失败文件的前 64KB 与十六进制头部，供定位真实格式（定位后移除）
    private static void SaveFailureSample(string partPath, MediaKind kind)
    {
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "Transfor", "diagnostics");
            Directory.CreateDirectory(directory);
            var samplePath = Path.Combine(directory, $"failed-media-{kind}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.bin");
            using (var source = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var destination = new FileStream(samplePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[64 * 1024];
                var read = source.Read(buffer, 0, buffer.Length);
                destination.Write(buffer, 0, read);
            }

            using var sample = new FileStream(samplePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = new byte[16];
            var headerRead = sample.Read(header, 0, header.Length);
            var hex = Convert.ToHexString(header.AsSpan(0, headerRead));
            var notePath = samplePath + ".txt";
            File.WriteAllText(notePath, $"kind={kind}\nhead-hex={hex}\n");
        }
        catch
        {
            // 诊断写入失败不影响主流程
        }
    }
}
