namespace Transfor;

internal sealed record HistoryEntry(
    TextToolId Tool,
    string OriginalInput,
    string ConvertedOutput,
    DateTimeOffset CreatedAtUtc);