using System.Text.Json;

namespace Transfor;

// 临时诊断：浏览器捕获解析完成后记录捕获现场（每次解析都写，文件名带时间戳）。
// 记录结构化 JSON、DOM 候选与解析出的资产摘要，用于排查实况/图文/视频解析问题；定位后移除
internal static class CaptureDiagnostics
{
    public static void Write(
        BrowserCaptureResult capture,
        Uri sourceUri,
        DouyinPageData? parsedData = null,
        bool usedCandidateFallback = false)
    {
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "Transfor", "diagnostics");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"capture-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
            var payload = new
            {
                sourceUri = sourceUri.ToString(),
                status = capture.Status.ToString(),
                error = capture.Error,
                path = sourceUri.AbsolutePath,
                resolutionPath = usedCandidateFallback ? "candidate-fallback" : "structured",
                structuredJson = Truncate(capture.StructuredDataJson, 3000),
                candidates = capture.Candidates
                    .Take(50)
                    .Select(c => new
                    {
                        url = c.Uri.ToString(),
                        kind = c.Kind?.ToString(),
                        source = c.Source.ToString(),
                        width = c.Width,
                        height = c.Height,
                    })
                    .ToArray(),
                assets = parsedData?.Assets
                    .Take(30)
                    .Select(a => new
                    {
                        kind = a.Kind.ToString(),
                        order = a.OrderIndex,
                        variantCount = a.Variants.Count,
                        variantUrls = a.Variants
                            .Take(5)
                            .Select(v => new
                            {
                                url = v.Url,
                                contentType = v.ContentType,
                                width = v.Width,
                                height = v.Height,
                                source = v.Source.ToString(),
                            })
                            .ToArray(),
                    })
                    .ToArray(),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 诊断写入失败不影响主流程
        }
    }

    private static string? Truncate(string? value, int maxLength)
        => value is null ? null : value.Length <= maxLength ? value : value[..maxLength];
}
