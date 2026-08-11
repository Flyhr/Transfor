namespace Transfor;

// 崩溃现场记录：把未捕获异常写入 %TEMP%\Transfor\diagnostics\crash-*.txt，
// 供用户反馈与后续定位；写入失败不影响主流程
internal static class CrashDiagnostics
{
    public static void Write(Exception exception)
    {
        try
        {
            AppLog.Application.Error("未处理异常", exception);
            var directory = Path.Combine(Path.GetTempPath(), "Transfor", "diagnostics");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
            File.WriteAllText(path, $"[{DateTime.Now:O}] {ErrorChainFormatter.Format(exception)}{Environment.NewLine}{exception}");
        }
        catch
        {
            // 崩溃现场写入失败不影响主流程
        }
    }
}
