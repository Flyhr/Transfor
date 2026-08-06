using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Transfor;

// 专用 Edge 进程管理：启动有头 Edge（独立持久化配置目录、随机调试端口、最小化运行），
// 轮询 /json/version 就绪；进程崩溃后按需重启；退出时仅结束自己启动的实例；
// 默认全面直连（抖音为 CN 服务，直连最优）；useProxy=true 时经 --proxy-server 走环境变量代理
internal sealed class EdgeProcessManager : IAsyncDisposable
{
    private const int ReadyTimeoutMilliseconds = 30_000;
    private const int PollIntervalMilliseconds = 500;

    // 启动参数版本标记：启动策略（代理开关等）变更时递增，
    // 复用检查要求旧实例含同标记，避免复用旧参数策略的实例
    private const string EditionMarker = "--transfor-edition=3";

    private readonly string profileDirectory;
    private readonly string edgeExecutable;
    private readonly bool useProxy;
    private readonly SemaphoreSlim startGate = new(1, 1);
    private Process? process;
    private string? browserWsUrl;
    private string? browserVersion;
    private int debuggingPort;
    private bool disposed;

    public EdgeProcessManager(string profileDirectory, bool useProxy = false)
    {
        this.profileDirectory = profileDirectory ?? throw new ArgumentNullException(nameof(profileDirectory));
        this.useProxy = useProxy;
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

            if (TryFindExistingProcess(out var existing, out var existingPort, out var existingEdition, out var existingHasProxy))
            {
                // 仅当启动参数版本一致且代理开关一致时才复用（避免复用旧参数策略的实例）
                if (string.Equals(existingEdition, EditionMarker, StringComparison.Ordinal)
                    && existingHasProxy == useProxy)
                {
                    // 参数策略一致：复用已运行实例（残留进程同样复用，保持会话温暖）
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
                else
                {
                    // 代理开关或启动参数策略已变化：结束旧实例，按当前配置启动新实例
                    TryKill(existing);
                    process = null;
                }
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
                EditionMarker,
            };
            // 显式开启代理时才传 --proxy-server（环境变量代理；不 bypass，全走代理）
            if (useProxy)
            {
                var proxy = ReadProxyServer();
                if (proxy is not null)
                {
                    arguments.Add($"--proxy-server={proxy}");
                }
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
                // 结束所有使用本应用 profile 的 msedge 主进程（覆盖句柄失效/复用实例未记录的情况）
                KillAllByProfile();
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

    // 结束所有使用本应用 profile 的 msedge 主进程；逐个 Kill 并确认退出，
    // 失败时二次强制（避免 Kill 异常被吞后进程残留）
    private void KillAllByProfile()
    {
        while (TryFindExistingProcess(out var existing, out _, out _, out _))
        {
            TryKill(existing);
            existing?.Dispose();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
            if (!process.WaitForExit(5_000))
            {
                // 一次 Kill 未落定：二次强制
                process.Kill();
                process.WaitForExit(3_000);
            }
        }
        catch
        {
            // 进程已退出或权限受限等场景忽略
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
    // 返回主进程句柄、调试端口、启动版本标记与是否带代理参数
    private bool TryFindExistingProcess(out Process? existing, out int port, out string? existingEdition, out bool existingHasProxy)
    {
        existing = null;
        port = 0;
        existingEdition = null;
        existingHasProxy = false;
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

                var portMatch = System.Text.RegularExpressions.Regex.Match(commandLine, "--remote-debugging-port=(\\d+)");
                if (!portMatch.Success)
                {
                    continue;
                }

                var editionMatch = System.Text.RegularExpressions.Regex.Match(commandLine, "--transfor-edition=(\\d+)");

                var pid = Convert.ToInt32(obj["ProcessId"]);
                existing = Process.GetProcessById(pid);
                port = int.Parse(portMatch.Groups[1].Value);
                existingEdition = editionMatch.Success ? editionMatch.Groups[0].Value : null;
                existingHasProxy = commandLine.Contains("--proxy-server=", StringComparison.OrdinalIgnoreCase);
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
