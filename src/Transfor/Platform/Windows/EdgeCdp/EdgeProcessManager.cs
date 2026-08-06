using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Transfor;

// 专用 Edge 进程管理：启动有头 Edge（独立持久化配置目录、随机调试端口、最小化运行），
// 轮询 /json/version 就绪；进程崩溃后按需重启；退出时仅结束自己启动的实例；
// 环境变量代理存在时通过 --proxy-server 传给 Edge（与 SocketsHttpHandler/WebView2 一致）
internal sealed class EdgeProcessManager : IAsyncDisposable
{
    private const int ReadyTimeoutMilliseconds = 30_000;
    private const int PollIntervalMilliseconds = 500;

    private readonly string profileDirectory;
    private readonly string edgeExecutable;
    private readonly SemaphoreSlim startGate = new(1, 1);
    private Process? process;
    private string? browserWsUrl;
    private string? browserVersion;
    private int debuggingPort;
    private bool disposed;

    public EdgeProcessManager(string profileDirectory)
    {
        this.profileDirectory = profileDirectory ?? throw new ArgumentNullException(nameof(profileDirectory));
        edgeExecutable = EdgeExecutableLocator.TryLocate()
            ?? throw new InvalidOperationException("未找到 Microsoft Edge。");
    }

    public bool IsReady => process is not null && !process.HasExited && browserWsUrl is not null;

    public string? BrowserWsUrl => browserWsUrl;

    public string? BrowserVersion => browserVersion;

    // 启动（或重启已退出的）Edge 并等待调试端点就绪；幂等；
    // 优先复用已运行的同 profile 实例（前次会话残留或仍被占用的 Edge），
    // 复用失败或不存在时才启动新实例（避免 profile 被锁导致启动失败）
    public async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        await startGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (IsReady)
            {
                return;
            }

            if (TryFindExistingProcess(out var existing, out var existingPort))
            {
                process = existing;
                debuggingPort = existingPort;
                browserWsUrl = await WaitForDebuggerEndpointAsync(existingPort, cancellationToken);
                if (browserWsUrl is not null)
                {
                    return;
                }
                process = null;
                browserWsUrl = null;
            }

            Directory.CreateDirectory(profileDirectory);
            debuggingPort = FindFreePort();
            var arguments = new List<string>
            {
                $"--remote-debugging-port={debuggingPort}",
                $"--remote-allow-origins=*",
                $"--user-data-dir=\"{profileDirectory}\"",
                "--no-first-run",
                "--no-default-browser-check",
            };
            var proxy = ReadProxyServer();
            if (proxy is not null)
            {
                arguments.Add($"--proxy-server={proxy}");
            }
            arguments.Add("about:blank");

            process = Process.Start(new ProcessStartInfo
            {
                FileName = edgeExecutable,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized,
                Arguments = string.Join(' ', arguments),
            });
            if (process is null)
            {
                throw new InvalidOperationException("Edge 启动失败。");
            }

            browserWsUrl = await WaitForDebuggerEndpointAsync(debuggingPort, cancellationToken);
        }
        finally
        {
            startGate.Release();
        }
    }

    // 把专用 Edge 窗口恢复到前台（交互登录/风控时调用）
    public void Foreground()
    {
        if (process is null)
        {
            return;
        }

        try
        {
            process.Refresh();
            var handle = process.MainWindowHandle;
            if (handle == 0)
            {
                // Edge 主窗口可能尚未创建，稍等再取一次
                Thread.Sleep(300);
                process.Refresh();
                handle = process.MainWindowHandle;
            }

            if (handle != 0)
            {
                WindowsNative.ShowWindow(handle, WindowsNative.SwRestore);
                WindowsNative.SetForegroundWindow(handle);
            }
        }
        catch
        {
            // 前台操作失败不阻断流程
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;

        try
        {
            await startGate.WaitAsync(CancellationToken.None);
            try
            {
                if (process is not null && !process.HasExited)
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(5_000);
                    }
                    catch
                    {
                        // 进程已退出等场景忽略
                    }
                }
                process = null;
                browserWsUrl = null;
            }
            finally
            {
                startGate.Release();
            }
        }
        catch
        {
            // 释放失败不掩盖退出流程
        }
    }

    // 轮询 http://127.0.0.1:{port}/json/version 直到返回 webSocketDebuggerUrl
    private async Task<string?> WaitForDebuggerEndpointAsync(int port, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(ReadyTimeoutMilliseconds);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var version = JsonNode.Parse(await client.GetStringAsync($"http://127.0.0.1:{port}/json/version", cancellationToken));
                var wsUrl = version?["webSocketDebuggerUrl"]?.GetValue<string>();
                browserVersion = version?["Browser"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(wsUrl))
                {
                    return wsUrl;
                }
            }
            catch
            {
                // 端点尚未就绪
            }
            await Task.Delay(PollIntervalMilliseconds, cancellationToken);
        }
        return null;
    }

    // 查找已运行的专用 Edge 主进程（同 profile + 调试端口，且非子进程）；
    // 返回主进程句柄与调试端口
    private bool TryFindExistingProcess(out Process? existing, out int port)
    {
        existing = null;
        port = 0;
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'msedge.exe'");
            foreach (var obj in searcher.Get())
            {
                var commandLine = obj["CommandLine"] as string;
                if (commandLine is null)
                {
                    continue;
                }
                // 仅匹配主进程（子进程带 --type=）且使用本应用 profile 与调试端口
                if (commandLine.Contains("--type=", StringComparison.OrdinalIgnoreCase)
                    || !commandLine.Contains(profileDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var match = System.Text.RegularExpressions.Regex.Match(commandLine, "--remote-debugging-port=(\\d+)");
                if (!match.Success)
                {
                    continue;
                }

                var pid = Convert.ToInt32(obj["ProcessId"]);
                existing = Process.GetProcessById(pid);
                port = int.Parse(match.Groups[1].Value);
                return true;
            }
        }
        catch
        {
            // WMI 不可用或进程已退出：回退到启动新实例
        }
        return false;
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string? ReadProxyServer()
    {
        foreach (var name in new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var trimmed = value.Trim();
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                && uri.Scheme is ("http" or "https" or "socks4" or "socks5")
                && !string.IsNullOrEmpty(uri.Host))
            {
                return trimmed;
            }
        }

        return null;
    }
}
