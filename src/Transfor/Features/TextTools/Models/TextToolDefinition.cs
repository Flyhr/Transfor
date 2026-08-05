namespace Transfor;

// 文本工具的静态定义：唯一标识、显示名称与对应的转换函数
internal sealed record TextToolDefinition(
    TextToolId Id,
    string DisplayName,
    Func<string?, string> Convert);
