using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LiveChatDanmakuViewer.Models;

namespace LiveChatDanmakuViewer.Services
{
    /// <summary>
    /// Douyu 协议客户端。
    /// 链路：控制通道登录 -> 获取转发节点 -> 弹幕通道登录与订阅 -> 双通道心跳。
    /// </summary>
    public sealed class DouyuPlatformClient : PlatformClientBase
    {
        private const string RoomIdPattern = "\\$ROOM\\.room_id\\D+(\\d+)";
        private const string VkSecret = "r5*^5;}2#${XF[h+;'./.Q'1;,-]f'p[";
        private const short SendMessageType = 689;
        private const short ReceiveMessageType = 690;
        private static readonly Random SharedRandom = new Random();
        private static readonly object RandomLock = new object();

        private readonly HttpClient _apiClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        });

        private ClientWebSocket? _gatewaySocket;
        private ClientWebSocket? _danmuSocket;
        private CancellationTokenSource? _runCts;
        private Task? _gatewayReceiveTask;
        private Task? _danmuReceiveTask;
        private Task? _gatewayHeartbeatTask;
        private Task? _danmuHeartbeatTask;
        private TaskCompletionSource<DouyuEndpoint>? _proxyEndpointSource;
        private TaskCompletionSource<bool>? _danmuReadySource;
        private string? _currentRoomId;
        private long _realRoomId;
        private string? _deviceId;

        public override ViewerPlatform Platform
        {
            get { return ViewerPlatform.Douyu; }
        }

        /// <summary>
        /// 建立斗鱼双通道连接（控制通道 + 弹幕通道）。
        /// </summary>
        protected override async Task ConnectCoreAsync(ClientOptions options, CancellationToken cancellationToken)
        {
            long roomId;
            if (_gatewaySocket != null || _danmuSocket != null)
            {
                await DisconnectCoreAsync(cancellationToken);
            }

            if (!long.TryParse(options.RoomId, NumberStyles.None, CultureInfo.InvariantCulture, out roomId))
            {
                throw new InvalidOperationException("Douyu 房间号必须是数字。");
            }

            PublishState(ConnectionState.Connecting, "正在获取斗鱼控制通道和弹幕节点...");
            _currentRoomId = options.RoomId;
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _proxyEndpointSource = new TaskCompletionSource<DouyuEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
            _danmuReadySource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                _realRoomId = await ResolveRealRoomIdAsync(roomId, options.Cookie, cancellationToken);
                Uri gatewayEndpoint = await GetGatewayEndpointAsync(roomId, options.Cookie, cancellationToken);

                _gatewaySocket = CreateWebSocket(options.Cookie);
                await _gatewaySocket.ConnectAsync(gatewayEndpoint, cancellationToken);
                PublishLog(LogSeverity.Info, "Douyu 控制通道已连接：" + gatewayEndpoint);

                _gatewayReceiveTask = Task.Run(() => RunReceiveLoopAsync(_gatewaySocket, true, _runCts.Token), _runCts.Token);
                await SendBytesAsync(_gatewaySocket, DouyuSttCodec.EncodePacket(BuildGatewayLoginPayload()), cancellationToken);

                DouyuEndpoint proxyEndpoint = await WaitWithTimeoutAsync(_proxyEndpointSource.Task, TimeSpan.FromSeconds(10), cancellationToken);
                Uri danmuEndpoint = new Uri(string.Format(CultureInfo.InvariantCulture, "wss://{0}:{1}/", proxyEndpoint.Host, proxyEndpoint.Port));

                _danmuSocket = CreateWebSocket(options.Cookie);
                await _danmuSocket.ConnectAsync(danmuEndpoint, cancellationToken);
                PublishLog(LogSeverity.Info, "Douyu 弹幕通道已连接：" + danmuEndpoint);

                _danmuReceiveTask = Task.Run(() => RunReceiveLoopAsync(_danmuSocket, false, _runCts.Token), _runCts.Token);
                await SendBytesAsync(_danmuSocket, DouyuSttCodec.EncodePacket(BuildDanmuLoginPayload()), cancellationToken);
                await WaitWithTimeoutAsync(_danmuReadySource.Task, TimeSpan.FromSeconds(10), cancellationToken);

                _gatewayHeartbeatTask = Task.Run(() => RunGatewayHeartbeatLoopAsync(_runCts.Token), _runCts.Token);
                _danmuHeartbeatTask = Task.Run(() => RunDanmuHeartbeatLoopAsync(_runCts.Token), _runCts.Token);
                PublishState(ConnectionState.Connected, string.Format(CultureInfo.InvariantCulture, "已连接 Douyu 房间 {0}", options.RoomId));
            }
            catch (Exception ex)
            {
                PublishLog(LogSeverity.Error, "Douyu 连接失败：" + ex.Message);
                PublishState(ConnectionState.Faulted, ex.Message);
                await CleanupAsync(false);
                throw;
            }
        }

        /// <summary>
        /// 断开斗鱼连接并清理资源。
        /// </summary>
        protected override async Task DisconnectCoreAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CleanupAsync(true);
        }

        /// <summary>
        /// 清理 socket、后台任务和等待源。
        /// </summary>
        private async Task CleanupAsync(bool publishDisconnected)
        {
            if (_runCts != null && !_runCts.IsCancellationRequested)
            {
                _runCts.Cancel();
            }

            await CloseSocketAsync(_danmuSocket);
            await CloseSocketAsync(_gatewaySocket);
            _danmuSocket = null;
            _gatewaySocket = null;
            await SafeAwaitAsync(_gatewayReceiveTask);
            await SafeAwaitAsync(_danmuReceiveTask);
            await SafeAwaitAsync(_gatewayHeartbeatTask);
            await SafeAwaitAsync(_danmuHeartbeatTask);
            _gatewayReceiveTask = null;
            _danmuReceiveTask = null;
            _gatewayHeartbeatTask = null;
            _danmuHeartbeatTask = null;

            if (_runCts != null)
            {
                _runCts.Dispose();
                _runCts = null;
            }

            _proxyEndpointSource = null;
            _danmuReadySource = null;
            if (publishDisconnected)
            {
                PublishState(ConnectionState.Disconnected, "Douyu 连接已断开");
            }
        }

        /// <summary>
        /// 解析真实房间号，处理重定向和页面兜底提取。
        /// </summary>
        private async Task<long> ResolveRealRoomIdAsync(long roomId, string? cookie, CancellationToken cancellationToken)
        {
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "https://www.douyu.com/" + roomId.ToString(CultureInfo.InvariantCulture)))
            {
                AddCommonHeaders(request);
                AddCookieHeader(request, cookie);
                using (HttpResponseMessage response = await _apiClient.SendAsync(request, cancellationToken))
                {
                    int statusCode = (int)response.StatusCode;
                    if (statusCode >= 300 && statusCode < 400)
                    {
                        string location = response.Headers.Location != null ? response.Headers.Location.ToString() : string.Empty;
                        int queryIndex = location.IndexOf('?');
                        string rid = ParseQueryValue(queryIndex >= 0 ? location.Substring(queryIndex) : string.Empty, "rid");
                        long redirectedRoomId;
                        if (long.TryParse(rid, NumberStyles.None, CultureInfo.InvariantCulture, out redirectedRoomId))
                        {
                            return redirectedRoomId;
                        }
                    }

                    string body = await ReadAsStringAsync(response);
                    Match match = Regex.Match(body, RoomIdPattern);
                    long realRoomId;
                    return match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out realRoomId)
                        ? realRoomId
                        : roomId;
                }
            }
        }

        /// <summary>
        /// 调 gateway 接口获取控制通道可用 wss 节点。
        /// </summary>
        private async Task<Uri> GetGatewayEndpointAsync(long roomId, string? cookie, CancellationToken cancellationToken)
        {
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "https://www.douyu.com/lapi/live/gateway/web/" + roomId.ToString(CultureInfo.InvariantCulture) + "?isH5=1"))
            {
                AddCommonHeaders(request);
                AddCookieHeader(request, cookie);
                request.Content = new StringContent(string.Empty, Encoding.UTF8, "application/x-www-form-urlencoded");
                using (HttpResponseMessage response = await _apiClient.SendAsync(request, cancellationToken))
                {
                    response.EnsureSuccessStatusCode();
                    Dictionary<string, object> root = LegacyJsonHelper.DeserializeObject(await ReadAsStringAsync(response));
                    if (LegacyJsonHelper.GetInt(root, "error") != 0)
                    {
                        string message = LegacyJsonHelper.GetString(root, "msg");
                        throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "Douyu gateway 接口返回失败。" : message);
                    }

                    if (!LegacyJsonHelper.TryGetDictionary(root, "data", out Dictionary<string, object>? data)
                        || data == null
                        || !LegacyJsonHelper.TryGetList(data, "wss", out IList<object>? nodes)
                        || nodes == null
                        || nodes.Count == 0)
                    {
                        throw new InvalidOperationException("Douyu 未返回可用的 WSS 节点。");
                    }

                    for (int i = 0; i < nodes.Count; i++)
                    {
                        if (!(nodes[(NextRandom(nodes.Count) + i) % nodes.Count] is Dictionary<string, object> node))
                        {
                            continue;
                        }

                        string domain = LegacyJsonHelper.GetString(node, "domain");
                        int port = LegacyJsonHelper.GetInt(node, "port");
                        if (!string.IsNullOrWhiteSpace(domain) && port > 0)
                        {
                            return new Uri(string.Format(CultureInfo.InvariantCulture, "wss://{0}:{1}/", domain, port));
                        }
                    }

                    throw new InvalidOperationException("Douyu 节点字段不完整。");
                }
            }
        }

        /// <summary>
        /// 控制通道心跳：发送 keeplive。
        /// </summary>
        private async Task RunGatewayHeartbeatLoopAsync(CancellationToken cancellationToken)
        {
            if (_gatewaySocket == null)
            {
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
                while (!cancellationToken.IsCancellationRequested && _gatewaySocket.State == WebSocketState.Open)
                {
                    await SendBytesAsync(_gatewaySocket, DouyuSttCodec.EncodePacket(BuildKeeplivePayload()), cancellationToken);
                    await Task.Delay(TimeSpan.FromSeconds(25), cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>
        /// 弹幕通道心跳：发送 mrkl。
        /// </summary>
        private async Task RunDanmuHeartbeatLoopAsync(CancellationToken cancellationToken)
        {
            if (_danmuSocket == null)
            {
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
                while (!cancellationToken.IsCancellationRequested && _danmuSocket.State == WebSocketState.Open)
                {
                    await SendBytesAsync(_danmuSocket, DouyuSttCodec.EncodePacket(BuildMrklPayload()), cancellationToken);
                    await Task.Delay(TimeSpan.FromSeconds(25), cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>
        /// 通用收包循环：按通道类型分发处理逻辑。
        /// </summary>
        private async Task RunReceiveLoopAsync(ClientWebSocket socket, bool isGatewayChannel, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    byte[]? rawMessage = await ReceiveWebSocketMessageAsync(socket, cancellationToken);
                    if (rawMessage == null)
                    {
                        break;
                    }

                    IReadOnlyList<DouyuPacket> packets = DouyuSttCodec.DecodePackets(rawMessage);
                    for (int i = 0; i < packets.Count; i++)
                    {
                        await HandleDouyuPacketAsync(packets[i], isGatewayChannel, cancellationToken);
                    }
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    PublishLog(LogSeverity.Warning, isGatewayChannel ? "Douyu 控制通道已关闭。" : "Douyu 弹幕通道已关闭。");
                    PublishState(ConnectionState.Faulted, "Douyu 连接中断");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                PublishLog(LogSeverity.Error, "Douyu 收包异常：" + ex.Message);
                PublishState(ConnectionState.Faulted, ex.Message);
            }
        }

        /// <summary>
        /// 处理斗鱼协议包：
        /// 登录应答、转发节点列表、弹幕/入场/礼物等业务消息。
        /// </summary>
        private async Task HandleDouyuPacketAsync(DouyuPacket packet, bool isGatewayChannel, CancellationToken cancellationToken)
        {
            switch (packet.Command)
            {
                case "loginres":
                case "loginresp":
                    if (isGatewayChannel && _gatewaySocket != null)
                    {
                        await SendBytesAsync(_gatewaySocket, DouyuSttCodec.EncodePacket(BuildKeeplivePayload()), cancellationToken);
                    }

                    if (!isGatewayChannel && _danmuSocket != null)
                    {
                        await SendBytesAsync(_danmuSocket, DouyuSttCodec.EncodePacket(BuildJoinGroupPayload()), cancellationToken);
                        await SendBytesAsync(_danmuSocket, DouyuSttCodec.EncodePacket(BuildMrklPayload()), cancellationToken);
                        await SendBytesAsync(_danmuSocket, DouyuSttCodec.EncodePacket(BuildSubPayload()), cancellationToken);
                        if (_danmuReadySource != null)
                        {
                            _danmuReadySource.TrySetResult(true);
                        }
                    }
                    return;
                case "msgrepeaterproxylist":
                    if (packet.Payload.TryGetValue("list", out object? listValue) && listValue != null)
                    {
                        List<DouyuEndpoint> endpoints = new List<DouyuEndpoint>();
                        ExtractEndpoints(listValue, endpoints);
                        if (endpoints.Count > 0 && _proxyEndpointSource != null)
                        {
                            _proxyEndpointSource.TrySetResult(endpoints[NextRandom(endpoints.Count)]);
                        }
                    }
                    return;
                case "chatmsg":
                    PublishMessage(new LiveMessage(DateTimeOffset.Now, Platform.ToDisplayName(), _currentRoomId ?? string.Empty, "Danmaku", packet.GetString("nn"), packet.GetString("uid"), FormatDouyuBadge(packet.GetString("bl"), packet.GetString("bnn")), packet.GetString("txt"), packet.Command));
                    return;
                case "uenter":
                    PublishMessage(new LiveMessage(DateTimeOffset.Now, Platform.ToDisplayName(), _currentRoomId ?? string.Empty, "Event", packet.GetString("nn"), packet.GetString("uid"), string.Empty, "进入直播间", packet.Command));
                    return;
                case "dgb":
                    PublishMessage(new LiveMessage(DateTimeOffset.Now, Platform.ToDisplayName(), _currentRoomId ?? string.Empty, "Gift", packet.GetString("nn"), packet.GetString("uid"), FormatDouyuBadge(packet.GetString("bl"), packet.GetString("bnn")), "赠送礼物 x" + packet.GetString("gfcnt"), packet.Command));
                    return;
            }
        }

        /// <summary>
        /// 构造控制通道登录包（含 vk 签名）。
        /// </summary>
        private string BuildGatewayLoginPayload()
        {
            _deviceId = Guid.NewGuid().ToString("N");
            string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            return DouyuSttCodec.BuildOrderedPayload(new[]
            {
                Pair("type", "loginreq"),
                Pair("roomid", _realRoomId.ToString(CultureInfo.InvariantCulture)),
                Pair("dfl", string.Empty),
                Pair("username", string.Empty),
                Pair("password", string.Empty),
                Pair("ltkid", string.Empty),
                Pair("biz", string.Empty),
                Pair("stk", string.Empty),
                Pair("devid", _deviceId),
                Pair("ct", "0"),
                Pair("pt", "2"),
                Pair("cvr", "0"),
                Pair("tvr", "7"),
                Pair("apd", string.Empty),
                Pair("rt", timestamp),
                Pair("vk", BuildVk(timestamp, _deviceId)),
                Pair("ver", "20220825"),
                Pair("aver", "218101901"),
                Pair("dmbt", "chrome"),
                Pair("dmbv", "116"),
            });
        }

        /// <summary>
        /// 构造弹幕通道登录包（访客身份）。
        /// </summary>
        private string BuildDanmuLoginPayload()
        {
            return DouyuSttCodec.BuildOrderedPayload(new[]
            {
                Pair("type", "loginreq"),
                Pair("roomid", _realRoomId.ToString(CultureInfo.InvariantCulture)),
                Pair("dfl", string.Empty),
                Pair("username", "visitor" + NextRandom(10000000, 20000000).ToString(CultureInfo.InvariantCulture)),
                Pair("uid", NextRandom(10000000, 20000000).ToString(CultureInfo.InvariantCulture)),
                Pair("ver", "20220825"),
                Pair("aver", "218101901"),
                Pair("ct", "0"),
            });
        }

        /// <summary>
        /// 构造 keeplive 心跳包。
        /// </summary>
        private string BuildKeeplivePayload()
        {
            return DouyuSttCodec.BuildOrderedPayload(new[]
            {
                Pair("type", "keeplive"),
                Pair("vbw", "0"),
                Pair("cdn", string.Empty),
                Pair("tick", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
                Pair("kd", string.Empty),
            });
        }

        /// <summary>
        /// 构造 mrkl 心跳包。
        /// </summary>
        private string BuildMrklPayload()
        {
            return DouyuSttCodec.BuildOrderedPayload(new[] { Pair("type", "mrkl") });
        }

        /// <summary>
        /// 构造加入分组包。
        /// </summary>
        private string BuildJoinGroupPayload()
        {
            return DouyuSttCodec.BuildOrderedPayload(new[]
            {
                Pair("type", "joingroup"),
                Pair("rid", _realRoomId.ToString(CultureInfo.InvariantCulture)),
                Pair("gid", "-9999"),
            });
        }

        /// <summary>
        /// 构造订阅包。
        /// </summary>
        private string BuildSubPayload()
        {
            return DouyuSttCodec.BuildOrderedPayload(new[]
            {
                Pair("type", "sub"),
                Pair("mt", "dayrk"),
            });
        }

        /// <summary>
        /// 计算斗鱼登录所需的 vk 签名。
        /// </summary>
        private static string BuildVk(string timestamp, string? deviceId)
        {
            byte[] input = Encoding.UTF8.GetBytes(timestamp + VkSecret + deviceId);
            using (MD5 md5 = MD5.Create())
            {
                return BitConverter.ToString(md5.ComputeHash(input)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        /// <summary>
        /// 递归提取转发节点列表。
        /// </summary>
        private static void ExtractEndpoints(object value, List<DouyuEndpoint> endpoints)
        {
            if (value is Dictionary<string, object> dictionary)
            {
                string host = LegacyJsonHelper.GetString(dictionary, "ip");
                int port = LegacyJsonHelper.GetInt(dictionary, "port");
                if (!string.IsNullOrWhiteSpace(host) && port > 0)
                {
                    endpoints.Add(new DouyuEndpoint(host, port));
                }

                return;
            }

            try
            {
                IList<object> list = LegacyJsonHelper.AsList(value);
                for (int i = 0; i < list.Count; i++)
                {
                    ExtractEndpoints(list[i], endpoints);
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// 等待任务结果并附带超时保护。
        /// </summary>
        private static async Task<T> WaitWithTimeoutAsync<T>(Task<T> task, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using (CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Task completed = await Task.WhenAny(task, Task.Delay(timeout, timeoutSource.Token));
                if (completed != task)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException("等待 Douyu 通道响应超时。");
                }

                timeoutSource.Cancel();
                return await task;
            }
        }

        private static int NextRandom(int maxValue)
        {
            lock (RandomLock)
            {
                return SharedRandom.Next(maxValue);
            }
        }

        private static int NextRandom(int minValue, int maxValue)
        {
            lock (RandomLock)
            {
                return SharedRandom.Next(minValue, maxValue);
            }
        }

        private static KeyValuePair<string, string?> Pair(string key, string? value)
        {
            return new KeyValuePair<string, string?>(key, value);
        }

        private static string ParseQueryValue(string query, string key)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return string.Empty;
            }

            string[] items = query.TrimStart('?').Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < items.Length; i++)
            {
                string[] parts = items[i].Trim().Split(new[] { '=' }, 2);
                if (parts.Length == 2 && parts[0].Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(parts[1]);
                }
            }

            return string.Empty;
        }

        private static string FormatDouyuBadge(string badgeLevel, string badgeName)
        {
            byte level;
            return byte.TryParse(badgeLevel, NumberStyles.Integer, CultureInfo.InvariantCulture, out level) && level > 0 && !string.IsNullOrWhiteSpace(badgeName)
                ? level.ToString(CultureInfo.InvariantCulture) + badgeName
                : string.Empty;
        }

        private static async Task SafeAwaitAsync(Task? task)
        {
            if (task == null)
            {
                return;
            }

            try
            {
                await task;
            }
            catch
            {
            }
        }

        private sealed class DouyuEndpoint
        {
            public DouyuEndpoint(string host, int port)
            {
                Host = host;
                Port = port;
            }

            public string Host { get; private set; }

            public int Port { get; private set; }
        }

        private sealed class DouyuPacket
        {
            public DouyuPacket(short messageType, Dictionary<string, object> payload)
            {
                MessageType = messageType;
                Payload = payload;
                Command = payload.TryGetValue("type", out object? typeValue) ? Convert.ToString(typeValue, CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;
            }

            public short MessageType { get; private set; }

            public string Command { get; private set; }

            public Dictionary<string, object> Payload { get; private set; }

            public string GetString(string key)
            {
                return Payload.TryGetValue(key, out object? value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;
            }
        }

        /// <summary>
        /// Douyu STT 文本协议编解码器。
        /// </summary>
        private static class DouyuSttCodec
        {
            private const int HeaderContentLength = 8;

            /// <summary>
            /// 将 STT 文本封装为斗鱼二进制传输帧。
            /// </summary>
            public static byte[] EncodePacket(string payload)
            {
                byte[] body = Encoding.UTF8.GetBytes(payload + "\0");
                int packetLength = body.Length + HeaderContentLength;
                byte[] packet = new byte[packetLength + 4];
                WriteInt32(packet, 0, packetLength);
                WriteInt32(packet, 4, packetLength);
                WriteInt16(packet, 8, SendMessageType);
                packet[10] = 0;
                packet[11] = 0;
                Buffer.BlockCopy(body, 0, packet, 12, body.Length);
                return packet;
            }

            /// <summary>
            /// 将 WebSocket 二进制消息拆解为斗鱼协议包。
            /// </summary>
            public static IReadOnlyList<DouyuPacket> DecodePackets(byte[] bytes)
            {
                List<DouyuPacket> packets = new List<DouyuPacket>();
                int offset = 0;
                while (offset + 12 <= bytes.Length)
                {
                    int packetLength = ReadInt32(bytes, offset);
                    int frameLength = packetLength + 4;
                    if (packetLength <= HeaderContentLength || offset + frameLength > bytes.Length)
                    {
                        break;
                    }

                    if (ReadInt16(bytes, offset + 8) == ReceiveMessageType)
                    {
                        int bodyLength = packetLength - HeaderContentLength;
                        string body = Encoding.UTF8.GetString(bytes, offset + 12, Math.Max(bodyLength - 1, 0));
                        packets.Add(new DouyuPacket(ReceiveMessageType, ParseToMap(body)));
                    }

                    offset += frameLength;
                }

                return packets;
            }

            /// <summary>
            /// 按顺序构造 STT 文本字段。
            /// </summary>
            public static string BuildOrderedPayload(IEnumerable<KeyValuePair<string, string?>> fields)
            {
                StringBuilder builder = new StringBuilder();
                foreach (KeyValuePair<string, string?> field in fields)
                {
                    builder.Append(Escape(field.Key));
                    builder.Append("@=");
                    builder.Append(Escape(field.Value ?? string.Empty));
                    builder.Append('/');
                }

                return builder.ToString();
            }

            private static Dictionary<string, object> ParseToMap(string payload)
            {
                Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.Ordinal);
                string[] segments = payload.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < segments.Length; i++)
                {
                    string[] entry = segments[i].Split(new[] { "@=" }, 2, StringSplitOptions.None);
                    string key = Unescape(entry[0]);
                    string value = entry.Length > 1 ? Unescape(entry[1]) : string.Empty;
                    map[key] = ParseValue(value);
                }

                return map;
            }

            private static object ParseValue(string value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return string.Empty;
                }

                bool containsMap = value.IndexOf("@=", StringComparison.Ordinal) >= 0;
                bool containsList = value.IndexOf('/', StringComparison.Ordinal) >= 0;
                if (containsMap && containsList)
                {
                    return ParseToMap(value);
                }

                if (!containsMap && containsList)
                {
                    string[] items = value.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    List<object> list = new List<object>(items.Length);
                    for (int i = 0; i < items.Length; i++)
                    {
                        list.Add(ParseValue(Unescape(items[i])));
                    }

                    return list;
                }

                return value;
            }

            private static string Escape(string value)
            {
                return value.Replace("@", "@A").Replace("/", "@S");
            }

            private static string Unescape(string value)
            {
                return value.Replace("@S", "/").Replace("@A", "@");
            }

            private static void WriteInt32(byte[] buffer, int offset, int value)
            {
                buffer[offset] = (byte)(value & 255);
                buffer[offset + 1] = (byte)((value >> 8) & 255);
                buffer[offset + 2] = (byte)((value >> 16) & 255);
                buffer[offset + 3] = (byte)((value >> 24) & 255);
            }

            private static void WriteInt16(byte[] buffer, int offset, short value)
            {
                buffer[offset] = (byte)(value & 255);
                buffer[offset + 1] = (byte)((value >> 8) & 255);
            }

            private static int ReadInt32(byte[] buffer, int offset)
            {
                return buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24);
            }

            private static short ReadInt16(byte[] buffer, int offset)
            {
                return (short)(buffer[offset] | (buffer[offset + 1] << 8));
            }
        }
    }
}
