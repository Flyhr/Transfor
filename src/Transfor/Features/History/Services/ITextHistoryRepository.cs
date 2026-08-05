namespace Transfor;

// 文本历史仓库的抽象：读取、追加与清空指定工具的转换历史
internal interface ITextHistoryRepository
{
    // 获取指定工具的历史记录（按时间先后排列，最新的在末尾）
    IReadOnlyList<HistoryEntry> GetHistory(TextToolId tool);

    // 追加一条历史记录（超出设置上限时自动裁剪最旧的记录）
    void Add(HistoryEntry entry);

    // 清空指定工具的全部历史
    void ClearHistory(TextToolId tool);
}
