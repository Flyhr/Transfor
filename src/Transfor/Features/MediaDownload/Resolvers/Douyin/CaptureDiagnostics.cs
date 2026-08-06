using System.Text.Json;

namespace Transfor;

// 临时诊断：浏览器捕获解析失败时记录捕获现场（结构化 JSON 片段与候选摘要），
// 用于排查结构化数据路径（详情接口/NEXT_DATA）未命中的原因；定位后移除
internal static class CaptureDiagnostics
{
    public static void Write(BrowserCaptureResult capture, Uri sourceUri)
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
                structuredJson = Truncate(capture.StructuredDataJson, 2000),
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
