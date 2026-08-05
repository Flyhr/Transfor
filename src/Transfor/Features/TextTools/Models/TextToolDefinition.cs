namespace Transfor;

internal sealed record TextToolDefinition(
    TextToolId Id,
    string DisplayName,
    Func<string?, string> Convert);