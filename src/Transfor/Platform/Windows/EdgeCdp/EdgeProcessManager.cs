using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Transfor;

// 专用 Edge 进程管理：启动有头 Edge（独立持久化配置目录、随机调试端口、最小化运行），
// 轮询 /json/version 就绪；进程崩溃后按需重启；退出时结束所有本应用 profile 的 Edge；
// 网络模式三态：Direct 强制直连（--no-proxy-server，不读系统代理）；
// System 不指定代理参数（Edge 读 Windows 系统代理）；CustomProxy 使用指定代理地址
internal sealed class EdgeProcessManager : IAsyncDisposable
{
    private const int ReadyTimeoutMilliseconds = 30_000;
    private const int PollIntervalMilliseconds = 500;

    // 启动参数版本标记：启动策略（网络模式等）变更时递增，
    // 复用检查要求旧实例含同标记，避免复用旧参数策略的实例
    private const string EditionMarker = "--transfor-edition=5";

    private readonly string profileDirectory;
    private readonly string edgeExecutable;
    private readonly MediaNetworkMode networkMode;
    private readonly string? proxyAddress;
    private readonly SemaphoreSlim startGate = new(1, 1);
    private Process? process;
    private string? browserWsUrl;
    private string? browserVersion;
    private int debuggingPort;
    private bool disposed;

    public EdgeProcessManager(
        string profileDirectory,
        MediaNetworkMode networkMode = MediaNetworkMode.Direct,
        string? proxyAddress = null)
    {
        this.profileDirectory = profileDirectory ?? throw new ArgumentNullException(nameof(profileDirectory));
        this.networkMode = networkMode;
        this.proxyAddress = proxyAddress;
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

            var expectedProxy = ResolveExpectedProxyArgument();
            if (TryFindExistingProcess(out var existing, out var existingPort, out var existingEdition, out var existingProxy))
            {
                // 仅当启动参数版本一致且代理配置一致时才复用（避免复用旧参数策略的实例）
                if (string.Equals(existingEdition, EditionMarker, StringComparison.Ordinal)
                    && ProxyEquals(existingProxy, expectedProxy))
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
                    // 网络模式或代理配置已变化：结束旧实例，按当前配置启动新实例
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
            // 网络模式三态：Direct 强制直连（不读系统代理）；System 不指定参数（读系统代理）；
            // CustomProxy 使用指定代理地址（无效地址启动即报错，不静默直连）
            switch (networkMode)
            {
                case MediaNetworkMode.Direct:
                    arguments.Add("--no-proxy-server");
                    // 系统 DNS 对抖音域不返回 AAAA（IPv6 记录被本地递归 DNS 过滤），
                    // 而 IPv4 路径可能被服务端封锁——启用 DoH（阿里公共 DNS）动态取得
                    // AAAA 记录，使 Edge 走 IPv6 路径；DoH 不可用时自动回退系统 DNS
                    arguments.Add("--enable-features=\"dns-over-https<DoHTrial\"");
                    arguments.Add("--force-fieldtrials=\"DoHTrial/Group1\"");
                    arguments.Add("--force-fieldtrial-params=\"DoHTrial.Group1:server/https%3A%2F%2Fdns.alidns.com%2Fdns-query/method/POST\"");
                    break;

                case MediaNetworkMode.System:
                    break;

                case MediaNetworkMode.CustomProxy:
                    if (string.IsNullOrWhiteSpace(proxyAddress)
                        || !Uri.TryCreate(proxyAddress.Trim(), UriKind.Absolute, out var proxyUri)
                        || proxyUri.Scheme is not ("http" or "https" or "socks4" or "socks5")
                        || string.IsNullOrEmpty(proxyUri.Host))
                    {
                        throw new InvalidOperationException(
                            $"已启用指定代理，但代理地址无效：{proxyAddress ?? "(空)"}");
                    }
                    arguments.Add($"--proxy-server={proxyUri}");
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(networkMode));
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
            await ShutdownAsync();
        }
        catch
        {
            // 释放失败不掩盖退出流程
        }
    }

    // 可恢复关闭：结束所有使用本应用 profile 的 msedge 主进程，
    // 不置终结态——下次 EnsureStartedAsync 会按当前配置重新启动（会话 Cookie 保留）
    public async ValueTask ShutdownAsync()
    {
        await startGate.WaitAsync(CancellationToken.None);
        try
        {
            KillAllByProfile();
            process = null;
            browserWsUrl = null;
        }
        finally
        {
            startGate.Release();
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

    private static void TryKill(Process? process)
    {
        if (process is null)
        {
            return;
        }

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
    // 返回主进程句柄、调试端口、启动版本标记与实际代理地址
    private bool TryFindExistingProcess(out Process? existing, out int port, out string? existingEdition, out string? existingProxy)
    {
        existing = null;
        port = 0;
        existingEdition = null;
        existingProxy = null;
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
                var proxyMatch = System.Text.RegularExpressions.Regex.Match(
                    commandLine, "--proxy-server=\"?([^\\s\"]+)\"?");

                var pid = Convert.ToInt32(obj["ProcessId"]);
                existing = Process.GetProcessById(pid);
                port = int.Parse(portMatch.Groups[1].Value);
                existingEdition = editionMatch.Success ? editionMatch.Groups[0].Value : null;
                existingProxy = proxyMatch.Success ? proxyMatch.Groups[1].Value : null;
                return true;
            }
        }
        catch
        {
            // WMI 不可用或进程已退出：回退到启动新实例
        }
        return false;
    }

    // 当前配置应使用的代理参数：CustomProxy 返回指定地址；Direct/System 不使用显式代理
    private string? ResolveExpectedProxyArgument()
        => networkMode == MediaNetworkMode.CustomProxy ? proxyAddress?.Trim() : null;

    // 代理配置是否一致：按 scheme/host/port 规范化比较（忽略参数顺序与引号差异）
    internal static bool ProxyEquals(string? existing, string? expected)
    {
        if (string.IsNullOrWhiteSpace(existing) && string.IsNullOrWhiteSpace(expected))
        {
            return true;
        }
        if (existing is null || expected is null)
        {
            return false;
        }

        if (Uri.TryCreate(existing.Trim(), UriKind.Absolute, out var a)
            && Uri.TryCreate(expected.Trim(), UriKind.Absolute, out var b))
        {
            return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
                && a.Port == b.Port;
        }

        return string.Equals(existing.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

