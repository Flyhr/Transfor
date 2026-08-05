using System.Text.Json;
using System.Text.Json.Serialization;

namespace Transfor;

// 通用 JSON 文件存储：schemaVersion 外壳 + 同目录临时文件原子替换；
// 读取失败/版本不符返回 default，不覆盖损坏原文件
internal static class JsonFileStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Document<T>(1, value), Options));
        File.Move(temporary, path, true);
    }

    public static T? TryRead<T>(string path)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<Document<T>>(File.ReadAllText(path), Options);
            if (doc is null || doc.SchemaVersion != 1 || doc.Value is null) return default;
            return doc.Value;
        }
        catch
        {
            return default;
        }
    }

    // 磁盘文件外壳：schemaVersion 用于版本校验
    private sealed record Document<T>(int SchemaVersion, T Value);
}
