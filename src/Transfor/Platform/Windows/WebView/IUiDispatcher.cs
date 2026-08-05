namespace Transfor;

// UI 线程调度抽象：把后台线程的 WebView2/Cookie 调用切回创建它们的 STA UI 线程；
// 测试使用 Fake dispatcher，不创建真实 WebView2
internal interface IUiDispatcher
{
    Task<T> InvokeAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken);
}
