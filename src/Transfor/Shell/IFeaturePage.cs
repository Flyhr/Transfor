namespace Transfor;

// 功能页契约：主窗口内容区可承载的页面
internal interface IFeaturePage
{
    // 页面唯一标识
    string Id { get; }

    // 导航按钮上显示的名称
    string DisplayName { get; }

    // 页面根控件（切换到该页时放入内容区）
    Control View { get; }

    // 页面被切换到前台时回调（用于聚焦输入框、刷新数据等）
    void OnActivated();
}
