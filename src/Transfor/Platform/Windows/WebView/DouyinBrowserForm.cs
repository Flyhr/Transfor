using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Transfor;

// 登录/验证码时显示给用户的浏览器窗口：
// 用户自行完成登录，不自动识别或绕过验证码；
// 保持消息循环，通过异步完成信号返回结果，不阻塞 UI 线程
internal sealed class DouyinBrowserForm : Form
{
    private readonly CoreWebView2Environment environment;
    private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WebView2 webView;
    private string? pendingUrl;

    public DouyinBrowserForm(CoreWebView2Environment environment, string initialUrl)
    {
        this.environment = environment;
        Text = "Transfor 浏览器登录";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1024, 720);
        webView = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(webView);
        FormClosing += DouyinBrowserForm_FormClosing;
        pendingUrl = initialUrl;
    }

    // 等待用户完成操作后关闭窗口
    public Task<bool> CompletionTask => completion.Task;

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            await webView.EnsureCoreWebView2Async(environment);
            if (pendingUrl is not null)
            {
                webView.CoreWebView2.Navigate(pendingUrl);
                pendingUrl = null;
            }
        }
        catch
        {
            completion.TrySetResult(false);
            Close();
        }
    }

    private void DouyinBrowserForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        completion.TrySetResult(DialogResult == DialogResult.Cancel ? false : true);
    }

    // 从外部请求关闭（解析流程完成后由调用方收起）
    public void CloseWithResult(bool succeeded)
    {
        if (!succeeded)
        {
            DialogResult = DialogResult.Cancel;
        }
        completion.TrySetResult(succeeded);
        Close();
    }
}
