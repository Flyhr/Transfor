using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Transfor;

// 轻量 CDP 连接：ClientWebSocket 传输 + 自增 id 与 TaskCompletionSource 匹配；
// 支持 sessionId 透传；CDP 错误（{id, error}）转换为异常；事件按 (方法, 参数, sessionId) 分发；
// 断开时取消所有挂起命令
internal sealed class CdpConnection : IAsyncDisposable
{
    private const int DefaultCommandTimeoutSeconds = 60;

    private readonly string webSocketUrl;
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly Dictionary<int, TaskCompletionSource<JsonNode?>> pending = new();
    private readonly object sync = new();
    private ClientWebSocket? ws;
    private int nextId;
    private bool disposed;

    public CdpConnection(string webSocketUrl)
    {
        this.webSocketUrl = webSocketUrl ?? throw new ArgumentNullException(nameof(webSocketUrl));
    }

    // CDP 事件：方法名、参数、所属 session（浏览器级事件 sessionId 为 null）
    public event Action<string, JsonNode?, string?>? EventReceived;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(webSocketUrl), cancellationToken);
        _ = Task.Run(ReceiveLoopAsync);
    }

    public async Task<JsonNode?> CommandAsync(
        string method,
        object? parameters,
        string? sessionId,
        CancellationToken cancellationToken,
        int timeoutSeconds = DefaultCommandTimeoutSeconds)
    {
        var id = Interlocked.Increment(ref nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            pending[id] = tcs;
        }

        var message = new JsonObject { ["id"] = id, ["method"] = method };
        if (parameters is not null)
        {
            message["params"] = JsonSerializerNode(parameters);
        }
        if (sessionId is not null)
        {
            message["sessionId"] = sessionId;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
            await sendGate.WaitAsync(cancellationToken);
            try
            {
                if (ws is null || ws.State != WebSocketState.Open)
                {
                    throw new InvalidOperationException("CDP 连接已断开。");
                }
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            }
            finally
            {
                sendGate.Release();
            }

            // 超时抛 TimeoutException；用户取消抛 TaskCanceledException
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);
        }
        catch
        {
            lock (sync)
            {
                pending.Remove(id, out _);
            }
            throw;
        }
    }

    private static JsonNode? JsonSerializerNode(object value) =>
        JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(value));

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var socket = ws;
            if (socket is null || socket.State != WebSocketState.Open)
            {
                break;
            }

            try
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var node = JsonNode.Parse(Encoding.UTF8.GetString(ms.ToArray()));
                if (node is null)
                {
                    continue;
                }

                var id = node["id"]?.GetValue<int>();
                if (id is not null)
                {
                    ResolveCommand(id.Value, node);
                }
                else
                {
                    var method = node["method"]?.GetValue<string>();
                    var sessionId = node["sessionId"]?.GetValue<string>();
                    if (method is not null)
                    {
                        EventReceived?.Invoke(method, node["params"], sessionId);
                    }
                }
            }
            catch
            {
                break;
            }
        }

        lock (sync)
        {
            foreach (var tcs in pending.Values)
            {
                tcs.TrySetCanceled();
            }
            pending.Clear();
        }
    }

    private void ResolveCommand(int id, JsonNode node)
    {
        TaskCompletionSource<JsonNode?>? tcs;
        lock (sync)
        {
            pending.Remove(id, out tcs);
        }
        if (tcs is null)
        {
            return;
        }

        if (node["error"] is JsonObject error)
        {
            var message = error["message"]?.GetValue<string>() ?? "未知 CDP 错误";
            tcs.TrySetException(new InvalidOperationException($"CDP 命令失败：{message}"));
            return;
        }

        tcs.TrySetResult(node["result"]);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;

        lock (sync)
        {
            foreach (var tcs in pending.Values)
            {
                tcs.TrySetCanceled();
            }
            pending.Clear();
        }

        var socket = ws;
        ws = null;
        if (socket is not null)
        {
            try
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closeTimeout.Token);
            }
            catch
            {
                // 服务端未回 close 帧或已断开：直接释放
            }
            socket.Dispose();
        }
    }
}
