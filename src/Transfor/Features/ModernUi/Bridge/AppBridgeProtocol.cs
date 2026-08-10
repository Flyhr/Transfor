using System.Text.Json;

namespace Transfor;

// App Bridge JSON 协议（Phase 5B）：Web UI ↔ C# 应用服务的消息模型；
// 请求（JS → C#）：{ "id": n, "method": "xxx", "params": {...} }
// 响应（C# → JS）：{ "id": n, "result": {...} } 或 { "id": n, "error": "消息" }
// 推送事件（C# → JS）：{ "event": "xxx", "data": {...} }
internal static class AppBridgeProtocol
{
    // 解析请求；非法消息返回 null（调用方忽略）
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

    private bool TryGet(string name, out JsonElement element)
    {
        element = default;
        return Parameters is { } parameters
            && parameters.TryGetProperty(name, out element);
    }
}
