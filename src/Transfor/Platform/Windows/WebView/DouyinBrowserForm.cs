using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Transfor;

// 单实例浏览器宿主：整个浏览器会话只使用这一个 WebView2；
// 非交互（隐藏）与交互（登录）模式共用同一实例；
// 导航完成后才返回；成功只能通过「完成并继续」按钮确认，
// 「取消」与右上角关闭一律视为取消；窗口只隐藏不关闭，实例跨会话复用
internal sealed class DouyinBrowserForm : Form
{
    // 伪装为完整版本号的桌面版 Edge（WebView2 默认 UA 的 .0.0.0 是嵌入式特征）
    private const string EdgeLikeUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.4078.48 Safari/537.36 Edg/150.0.4078.48";

    private TaskCompletionSource<bool>? pending;

    public DouyinBrowserForm()
    {
        Text = "Transfor 浏览器登录";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1024, 720);

        var webView = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(webView);
        WebView = webView;

        // 明确的操作按钮：成功只能通过「完成并继续」，其余一律视为取消
        var done = new Button { Text = "完成并继续", AutoSize = true };
        var cancel = new Button { Text = "取消", AutoSize = true };
        done.Click += (_, _) => Complete(completed: true);
        cancel.Click += (_, _) => Complete(completed: false);
        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        bottom.Controls.AddRange(new Control[] { done, cancel });
        Controls.Add(bottom);

        // 右上角关闭视为取消：取消关闭动作并隐藏窗口，保持实例存活
        FormClosing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
            pending?.TrySetResult(false);
        };
    }

    public WebView2 WebView { get; }

    public CoreWebView2? CoreWebView2 { get; private set; }

    // 初始化底层 WebView2（隐藏模式下也需要句柄，因此强制创建控件）
    public async Task<bool> InitializeAsync(CoreWebView2Environment environment, CancellationToken cancellationToken)
    {
        if (CoreWebView2 is not null)
        {
            return true;
        }

        try
        {
            if (!IsHandleCreated)
            {
                CreateControl();
            }
            await WebView.EnsureCoreWebView2Async(environment);
            CoreWebView2 = WebView.CoreWebView2;
            // 覆盖为完整版本号的桌面 Edge UA：默认 UA 的 151.0.0.0 是嵌入式
            // WebView2 的典型特征，抖音风控据此返回验证页
            CoreWebView2.Settings.UserAgent = EdgeLikeUserAgent;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // 导航并等待 NavigationCompleted；成功返回 true，失败/超时返回 false
    public async Task<bool> NavigateAsync(Uri url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var core = CoreWebView2;
        if (core is null)
        {
            return false;
        }

        var navigated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNavigated(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            navigated.TrySetResult(args.IsSuccess);
        }

        core.NavigationCompleted += OnNavigated;
        try
        {
            core.Navigate(url.ToString());
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var winner = await Task.WhenAny(navigated.Task, timeoutTask).ConfigureAwait(true);
            if (winner != navigated.Task)
            {
                // 取消时向上传播；否则视为超时
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }
            return await navigated.Task.ConfigureAwait(true);
        }
        finally
        {
            core.NavigationCompleted -= OnNavigated;
        }
    }

    // 以可见窗口显示供用户登录；每次调用创建新的完成信号，
    // 返回 true 表示点击「完成并继续」，false 表示取消或关闭
    public async Task<bool> ShowForLoginAsync(Control owner, CancellationToken cancellationToken)
    {
        pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Show(owner);
        return await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(true);
    }

    private void Complete(bool completed)
    {
        pending?.TrySetResult(completed);
        Hide();
    }
}
