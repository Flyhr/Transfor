namespace Transfor;

internal interface IFeaturePage
{
    string Id { get; }
    string DisplayName { get; }
    Control View { get; }
    void OnActivated();
}