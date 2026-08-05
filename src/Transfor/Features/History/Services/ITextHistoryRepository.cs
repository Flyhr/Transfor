namespace Transfor;

internal interface ITextHistoryRepository
{
    IReadOnlyList<HistoryEntry> GetHistory(TextToolId tool);

    void Add(HistoryEntry entry);

    void ClearHistory(TextToolId tool);
}