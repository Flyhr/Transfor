using System.Text;
using System.Text.Json;

namespace Transfor;

// App Bridge JSON 协议（Phase 5B + 5B 加固）：Web UI ↔ C# 应用服务的消息模型；
// 请求（JS → C#）：{ "protocolVersion": "1.0", "id": n, "method": "xxx", "params": {...} }
// 响应（C# → JS）：{ "id": n, "result": {...} } 或 { "id": n, "error": "消息" }
// 推送事件（C# → JS）：{ "event": "xxx", "data": {...} }
internal static class AppBridgeProtocol
{
    // 当前支持的协议版本（仅接受此版本）
    private const string CurrentProtocolVersion = "1.0";

    // UTF-8 编码后请求体上限（1 MiB）
    internal const int MaxPayloadBytes = 1024 * 1024;

    // WebMessageReceived 可信源：仅本地 UI 虚拟主机（https://appassets.transfor）
    public static bool IsTrustedMessageSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)
            || !Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        return string.Equals(uri.Host, "appassets.transfor", StringComparison.OrdinalIgnoreCase);
    }

    // UTF-8 编码后请求体是否在 1 MiB 上限内
    public static bool IsPayloadWithinLimit(string json) =>
        Encoding.UTF8.GetByteCount(json) <= MaxPayloadBytes;

    // 解析请求；非法消息（含 protocolVersion 缺失或不支持）返回 null（调用方忽略）
    public static BridgeRequest? ParseRequest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("method", out var methodElement)
                || methodElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var method = methodElement.GetString();
            if (string.IsNullOrWhiteSpace(method))
            {
                return null;
            }

            if (!root.TryGetProperty("protocolVersion", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.String
                || !string.Equals(versionElement.GetString(), CurrentProtocolVersion, StringComparison.Ordinal))
            {
                return null;
            }

            long? id = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number
                ? idElement.GetInt64()
                : null;
            // Clone：参数元素独立于 JsonDocument 存活（文档在 ParseRequest 返回后释放）
            var parameters = root.TryGetProperty("params", out var paramsElement) && paramsElement.ValueKind == JsonValueKind.Object
                ? paramsElement.Clone()
                : (JsonElement?)null;
            return new BridgeRequest(id, method, parameters);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // 构造成功响应
    public static string CreateSuccessResponse(long? id, object? result) =>
        JsonSerializer.Serialize(new { id, result });

    // 构造失败响应
    public static string CreateErrorResponse(long? id, string error) =>
        JsonSerializer.Serialize(new { id, error });

    // 构造推送事件
    public static string CreateEvent(string eventName, object? data) =>
        JsonSerializer.Serialize(new { @event = eventName, data });
}

// Bridge 请求：id 可为空（无需回应的调用）
internal sealed record BridgeRequest(long? Id, string Method, JsonElement? Parameters)
{
    // 读取字符串参数；缺失或类型不符返回 fallback
    public string? GetString(string name, string? fallback = null) =>
        TryGet(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : fallback;

    // 读取布尔参数
    public bool? GetBool(string name) =>
        TryGet(name, out var element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : null;

    // 读取整数参数
    public int? GetInt32(string name) =>
        TryGet(name, out var element) && element.ValueKind == JsonValueKind.Number
            ? element.GetInt32()
            : null;

    private bool TryGet(string name, out JsonElement element)
    {
        element = default;
        return Parameters is { } parameters
            && parameters.TryGetProperty(name, out element);
    }
}
