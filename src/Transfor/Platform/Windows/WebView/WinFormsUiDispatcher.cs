using Microsoft.Web.WebView2.Core;

namespace Transfor;

// WinForms 的 IUiDispatcher 实现：基于 Control.BeginInvoke 的异步 UI 调度；
// 检查控件销毁状态、支持取消、处理投递失败，保证任务在有限时间内结束
internal sealed class WinFormsUiDispatcher : IUiDispatcher
{
    private readonly Control uiOwner;

    public WinFormsUiDispatcher(Control uiOwner)
    {
        this.uiOwner = uiOwner ?? throw new ArgumentNullException(nameof(uiOwner));
    }

    public Task<T> InvokeAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        if (uiOwner.IsDisposed || uiOwner.Disposing || !uiOwner.IsHandleCreated)
        {
            throw new InvalidOperationException("浏览器窗口已经关闭。");
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = cancellationToken.Register(() =>
            completion.TrySetCanceled(cancellationToken));

        try
        {
            uiOwner.BeginInvoke(async () =>
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    completion.TrySetResult(await action(cancellationToken));
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
                finally
                {
                    registration.Dispose();
                }
            });
        }
        catch
        {
            registration.Dispose();
            throw;
        }

        return completion.Task;
    }
}
