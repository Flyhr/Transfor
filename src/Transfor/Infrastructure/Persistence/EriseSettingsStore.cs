using System.Text.Json;

namespace Transfor;

// Erise 服务器设置存储（Phase 6.6）：沿用现有 JSON Store 模式（原子写 + 损坏回退）。
// JSON 仅包含规范化 Origin 与非敏感 UI 设置，禁止 Token/Password；
// 凭据按规范化 Origin 隔离，由登录层内存持有，切换 Origin 时经 OriginChanged 通知清理
// （内存 Access Token / 当前 Session / Context 选择）。
internal sealed class EriseSettingsStore
{
    private readonly string filePath;
    private readonly object gate = new();
    private EriseServerSettings current;

    public EriseSettingsStore(string filePath)
    {
        this.filePath = filePath;
        current = Load();
    }

    // Origin 变化通知（含首次配置）；供凭据层清理内存会话
    public event Action<string?>? OriginChanged;

    public EriseServerSettings Current => current;

    public static EriseSettingsStore Load(AppPaths paths) => new(paths.EriseSettingsFile);

    // 校验 + 保存；Origin 变化时触发 OriginChanged。非法输入不改动现有设置。
    public bool TrySetOrigin(string? input, out string? error)
    {
        if (!EriseServerSettings.TryNormalizeOrigin(input, out var origin, out error))
        {
            return false;
        }

        lock (gate)
        {
            var changed = !string.Equals(current.ServerOrigin, origin, StringComparison.Ordinal);
            current = current with { ServerOrigin = origin };
            Save();
            if (changed)
            {
                OriginChanged?.Invoke(origin);
            }
        }
        return true;
    }

    private EriseServerSettings Load()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("serverOrigin", out var originElement)
                && originElement.ValueKind == JsonValueKind.String)
            {
                var stored = originElement.GetString();
                if (EriseServerSettings.TryNormalizeOrigin(stored, out var origin, out _))
                {
                    return new EriseServerSettings(origin);
                }
            }
        }
        catch
        {
            // 文件缺失或损坏：回退默认（未配置）
        }
        return EriseServerSettings.Default;
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(new { serverOrigin = current.ServerOrigin });
        var temporary = filePath + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, filePath, overwrite: true);
    }
}
