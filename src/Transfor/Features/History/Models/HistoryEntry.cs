namespace Transfor;

// 一条文本转换历史：记录所属工具、转换前后的文本与创建时间（UTC）
internal sealed record HistoryEntry(
    TextToolId Tool,
    string OriginalInput,
    string ConvertedOutput,
    DateTimeOffset CreatedAtUtc);
