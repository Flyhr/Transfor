namespace Transfor;

internal sealed class AppServices : IDisposable
{
    public required TextStateStore State { get; init; }
    public required GlobalHotKeyManager HotKeys { get; init; }
    public required PasteCoordinator PasteCoordinator { get; init; }
    public void Dispose() => HotKeys.Dispose();
}