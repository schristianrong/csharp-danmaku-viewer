using System;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiveChatDanmakuViewer.Models;

namespace LiveChatDanmakuViewer.Services
{
    /// <summary>
    /// 平台客户端基类。
    /// 封装连接生命周期、事件发布和常用网络辅助方法，减少各平台重复代码。
    /// </summary>
    public abstract class PlatformClientBase : IPlatformClient
    {
        private const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";
        private int _disposed;

        /// <summary>
        /// 当前实现对应的平台。
        /// </summary>
        public abstract ViewerPlatform Platform { get; }

        /// <summary>
        /// 归一化消息事件。
        /// </summary>
        public event Action<LiveMessage>? MessageReceived;

        /// <summary>
        /// 日志事件。
        /// </summary>
        public event Action<ViewerLogEntry>? LogReceived;

        /// <summary>
        /// 连接状态变化事件。
        /// </summary>
        public event Action<ConnectionState, string?>? ConnectionStateChanged;

        /// <summary>
        /// 对外连接入口：先校验对象生命周期，再委托到平台实现。
        /// </summary>
        public Task ConnectAsync(ClientOptions options, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return ConnectCoreAsync(options, cancellationToken);
        }

        /// <summary>
        /// 对外断开入口：已释放对象直接忽略，避免重复抛错影响 UI。
        /// </summary>
        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            if (_disposed != 0)
            {
                return Task.CompletedTask;
            }

            return DisconnectCoreAsync(cancellationToken);
        }

        /// <summary>
        /// 统一释放资源：保证只执行一次，并在释放阶段吞掉清理异常。
        /// </summary>
        public async Task DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await DisconnectCoreAsync(CancellationToken.None);
            }
            catch
            {
                // ignore dispose failures
            }
        }

        /// <summary>
        /// 平台自定义连接逻辑。
        /// </summary>
        protected abstract Task ConnectCoreAsync(ClientOptions options, CancellationToken cancellationToken);

        /// <summary>
        /// 平台自定义断开逻辑。
        /// </summary>
        protected abstract Task DisconnectCoreAsync(CancellationToken cancellationToken);

        /// <summary>
        /// 发布归一化消息。
        /// </summary>
        protected void PublishMessage(LiveMessage message)
        {
            var handler = MessageReceived;
            if (handler != null)
            {
                handler(message);
            }
        }

        /// <summary>
        /// 发布日志。
        /// </summary>
        protected void PublishLog(LogSeverity severity, string message)
        {
            var handler = LogReceived;
            if (handler != null)
            {
                handler(new ViewerLogEntry(
                    DateTimeOffset.Now,
                    severity.ToString().ToUpperInvariant(),
                    message));
            }
        }

        /// <summary>
        /// 发布连接状态。
        /// </summary>
        protected void PublishState(ConnectionState state, string? detail)
        {
            var handler = ConnectionStateChanged;
            if (handler != null)
            {
                handler(state, detail);
            }
        }

        /// <summary>
        /// 创建带通用请求头的 WebSocket 客户端。
        /// </summary>
        protected static ClientWebSocket CreateWebSocket(string? cookie)
        {
            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.Zero;
            socket.Options.SetRequestHeader("User-Agent", DefaultUserAgent);
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                socket.Options.SetRequestHeader("Cookie", cookie);
            }

            return socket;
        }

        /// <summary>
        /// 向 HTTP 请求补充 Cookie 头。
        /// </summary>
        protected static void AddCookieHeader(HttpRequestMessage request, string? cookie)
        {
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
            }
        }

        /// <summary>
        /// 向 HTTP 请求补充通用浏览器请求头。
        /// </summary>
        protected static void AddCommonHeaders(HttpRequestMessage request)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        }

        /// <summary>
        /// 从整段 Cookie 文本中按 key 读取值。
        /// </summary>
        protected static string? GetCookieValue(string? cookieHeader, string name)
        {
            if (string.IsNullOrWhiteSpace(cookieHeader))
            {
                return null;
            }

            var segments = cookieHeader.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawSegment in segments)
            {
                var segment = rawSegment.Trim();
                var parts = segment.Split(new[] { '=' }, 2);
                if (parts.Length == 2 && parts[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return parts[1].Trim();
                }
            }

            return null;
        }

        /// <summary>
        /// 接收一条完整 WebSocket 消息，自动处理分片。
        /// 返回 null 表示对端关闭连接。
        /// </summary>
        protected static async Task<byte[]?> ReceiveWebSocketMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            using (var stream = new MemoryStream())
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return null;
                    }

                    stream.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage)
                    {
                        return stream.ToArray();
                    }
                }
            }
        }

        /// <summary>
        /// 发送二进制帧。
        /// </summary>
        protected static Task SendBytesAsync(ClientWebSocket socket, byte[] payload, CancellationToken cancellationToken)
        {
            return socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Binary, true, cancellationToken);
        }

        /// <summary>
        /// 安全关闭并释放 WebSocket。
        /// </summary>
        protected static async Task CloseSocketAsync(ClientWebSocket? socket)
        {
            if (socket == null)
            {
                return;
            }

            try
            {
                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
                }
            }
            catch
            {
                // ignore socket close failures
            }
            finally
            {
                socket.Dispose();
            }
        }

        /// <summary>
        /// 使用 UTF-8 读取 HTTP 响应正文。
        /// </summary>
        protected static async Task<string> ReadAsStringAsync(HttpResponseMessage response)
        {
            using (var stream = await response.Content.ReadAsStreamAsync())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return await reader.ReadToEndAsync();
            }
        }

        /// <summary>
        /// 已释放对象防御检查。
        /// </summary>
        protected void ThrowIfDisposed()
        {
            if (_disposed != 0)
            {
                throw new ObjectDisposedException(GetType().Name);
            }
        }
    }
}
