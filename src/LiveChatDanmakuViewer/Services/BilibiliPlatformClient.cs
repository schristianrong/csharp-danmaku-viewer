using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiveChatDanmakuViewer.Models;

namespace LiveChatDanmakuViewer.Services
{
    /// <summary>
    /// Bilibili 协议客户端。
    /// 链路：HTTP 拿房间和鉴权参数 -> WebSocket 鉴权 -> 心跳 -> 业务帧解码。
    /// </summary>
    public sealed class BilibiliPlatformClient : PlatformClientBase
    {
        private const short HeaderLength = 16;
        private const short ProtoverZlib = 2;
        private const short ProtoverBrotli = 3;
        private const int OperationHeartbeat = 2;
        private const int OperationHeartbeatReply = 3;
        private const int OperationMessage = 5;
        private const int OperationAuth = 7;
        private const int OperationAuthReply = 8;

        private readonly HttpClient _httpClient = new HttpClient();
        private ClientWebSocket? _socket;
        private CancellationTokenSource? _runCts;
        private Task? _receiveTask;
        private Task? _heartbeatTask;
        private int _sequence;
        private string? _currentRoomId;

        public override ViewerPlatform Platform
        {
            get { return ViewerPlatform.Bilibili; }
        }

        /// <summary>
        /// 建立 Bilibili 连接并启动收包与心跳任务。
        /// </summary>
        protected override async Task ConnectCoreAsync(ClientOptions options, CancellationToken cancellationToken)
        {
            long roomId;
            if (_socket != null)
            {
                await DisconnectCoreAsync(cancellationToken);
            }

            if (!long.TryParse(options.RoomId, NumberStyles.None, CultureInfo.InvariantCulture, out roomId))
            {
                throw new InvalidOperationException("Bilibili 房间号必须是数字。");
            }

            PublishState(ConnectionState.Connecting, "正在解析房间信息和鉴权参数...");
            _currentRoomId = options.RoomId;
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            try
            {
                BilibiliSessionInfo sessionInfo = await CreateSessionInfoAsync(roomId, options.Cookie, cancellationToken);
                _socket = CreateWebSocket(options.Cookie);
                await _socket.ConnectAsync(sessionInfo.Endpoint, cancellationToken);

                PublishLog(LogSeverity.Info, string.Format(CultureInfo.InvariantCulture, "Bilibili 已连接 WebSocket：{0}", sessionInfo.Endpoint));
                await SendBytesAsync(_socket, BuildPacket(ProtoverZlib, OperationAuth, BuildAuthPayload(sessionInfo)), cancellationToken);

                _receiveTask = Task.Run(() => RunReceiveLoopAsync(_socket, _runCts.Token), _runCts.Token);
                _heartbeatTask = Task.Run(() => RunHeartbeatLoopAsync(_socket, _runCts.Token), _runCts.Token);

                PublishState(ConnectionState.Connected, string.Format(CultureInfo.InvariantCulture, "已连接 Bilibili 房间 {0}", options.RoomId));
            }
            catch (Exception ex)
            {
                PublishLog(LogSeverity.Error, "Bilibili 连接失败：" + ex.Message);
                PublishState(ConnectionState.Faulted, ex.Message);
                await CleanupAsync(false);
                throw;
            }
        }

        /// <summary>
        /// 断开 Bilibili 连接。
        /// </summary>
        protected override async Task DisconnectCoreAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CleanupAsync(true);
        }

        /// <summary>
        /// 清理 socket 与后台任务。
        /// </summary>
        private async Task CleanupAsync(bool publishDisconnected)
        {
            if (_runCts != null && !_runCts.IsCancellationRequested)
            {
                _runCts.Cancel();
            }

            await CloseSocketAsync(_socket);
            _socket = null;

            await SafeAwaitAsync(_receiveTask);
            await SafeAwaitAsync(_heartbeatTask);
            _receiveTask = null;
            _heartbeatTask = null;

            if (_runCts != null)
            {
                _runCts.Dispose();
                _runCts = null;
            }

            if (publishDisconnected)
            {
                PublishState(ConnectionState.Disconnected, "Bilibili 连接已断开");
            }
        }

        /// <summary>
        /// 通过两个 HTTP 接口拼装会话参数：
        /// 实际房间号、token、wss 节点、uid、buvid。
        /// </summary>
        private async Task<BilibiliSessionInfo> CreateSessionInfoAsync(long roomId, string? cookie, CancellationToken cancellationToken)
        {
            Dictionary<string, object> roomInit = await GetBilibiliDataAsync(
                "https://api.live.bilibili.com/room/v1/Room/room_init?id=" + roomId.ToString(CultureInfo.InvariantCulture),
                cookie,
                cancellationToken);
            Dictionary<string, object> danmuInfo = await GetBilibiliDataAsync(
                "https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?id=" + roomId.ToString(CultureInfo.InvariantCulture) + "&type=0",
                cookie,
                cancellationToken);

            long realRoomId = LegacyJsonHelper.GetLong(roomInit, "room_id");
            string token = LegacyJsonHelper.GetString(danmuInfo, "token");
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("Bilibili token 为空。");
            }

            string host = "broadcastlv.chat.bilibili.com";
            int port = 443;
            if (LegacyJsonHelper.TryGetList(danmuInfo, "host_list", out IList<object>? hostList) && hostList != null && hostList.Count > 0)
            {
                if (TryAsDictionary(hostList[0], out Dictionary<string, object>? hostInfo) && hostInfo != null)
                {
                    string parsedHost = LegacyJsonHelper.GetString(hostInfo, "host");
                    int parsedPort = LegacyJsonHelper.GetInt(hostInfo, "wss_port");
                    if (!string.IsNullOrWhiteSpace(parsedHost))
                    {
                        host = parsedHost;
                    }

                    if (parsedPort > 0)
                    {
                        port = parsedPort;
                    }
                }
            }

            Uri endpoint = new Uri(string.Format(CultureInfo.InvariantCulture, "wss://{0}:{1}/sub", host, port));
            string? uidValue = GetCookieValue(cookie, "DedeUserID");
            long userId;
            if (!long.TryParse(uidValue, NumberStyles.None, CultureInfo.InvariantCulture, out userId))
            {
                userId = 0L;
            }

            string buvid = GetCookieValue(cookie, "buvid3") ?? Guid.NewGuid().ToString();
            return new BilibiliSessionInfo(realRoomId, userId, buvid, token, endpoint);
        }

        /// <summary>
        /// 请求 Bilibili 接口并返回 data 节点。
        /// </summary>
        private async Task<Dictionary<string, object>> GetBilibiliDataAsync(string url, string? cookie, CancellationToken cancellationToken)
        {
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                AddCommonHeaders(request);
                AddCookieHeader(request, cookie);

                using (HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken))
                {
                    response.EnsureSuccessStatusCode();
                    string body = await ReadAsStringAsync(response);
                    Dictionary<string, object> root = LegacyJsonHelper.DeserializeObject(body);
                    if (LegacyJsonHelper.GetInt(root, "code") != 0)
                    {
                        string message = LegacyJsonHelper.GetString(root, "message");
                        if (string.IsNullOrWhiteSpace(message))
                        {
                            message = LegacyJsonHelper.GetString(root, "msg");
                        }

                        throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "Bilibili 接口返回失败。" : message);
                    }

                    if (!LegacyJsonHelper.TryGetDictionary(root, "data", out Dictionary<string, object>? data) || data == null)
                    {
                        throw new InvalidOperationException("Bilibili 接口未返回 data 节点。");
                    }

                    return data;
                }
            }
        }

        /// <summary>
        /// 心跳任务：首包延迟 15 秒，之后每 25 秒发送一次。
        /// </summary>
        private async Task RunHeartbeatLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
                while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    await SendBytesAsync(socket, BuildPacket(ProtoverZlib, OperationHeartbeat, new byte[0]), cancellationToken);
                    await Task.Delay(TimeSpan.FromSeconds(25), cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
        }

        /// <summary>
        /// 收包任务：持续读取 WebSocket 消息并解析为业务帧。
        /// </summary>
        private async Task RunReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
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

                    IReadOnlyList<BilibiliFrame> frames = DecodeFrames(rawMessage);
                    for (int i = 0; i < frames.Count; i++)
                    {
                        HandleFrame(frames[i]);
                    }
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    PublishLog(LogSeverity.Warning, "Bilibili 连接已被远端关闭。");
                    PublishState(ConnectionState.Faulted, "Bilibili 连接中断");
                }
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception ex)
            {
                PublishLog(LogSeverity.Error, "Bilibili 收包异常：" + ex.Message);
                PublishState(ConnectionState.Faulted, ex.Message);
            }
        }

        /// <summary>
        /// 按 op 处理单帧。
        /// </summary>
        private void HandleFrame(BilibiliFrame frame)
        {
            if (frame.Version == ProtoverBrotli && frame.Operation == OperationMessage)
            {
                PublishLog(LogSeverity.Warning, "Bilibili 返回了 Brotli 压缩帧，net48 版本当前未解码。");
                return;
            }

            switch (frame.Operation)
            {
                case OperationHeartbeatReply:
                    if (frame.Body.Length >= 4)
                    {
                        int popularity = ReadInt32BigEndian(frame.Body, 0);
                        PublishLog(LogSeverity.Info, "Bilibili 心跳回复，人气值 " + popularity.ToString(CultureInfo.InvariantCulture));
                    }

                    break;
                case OperationAuthReply:
                    PublishLog(LogSeverity.Info, "Bilibili 鉴权回复：" + Encoding.UTF8.GetString(frame.Body));
                    break;
                case OperationMessage:
                    HandleMessageFrame(frame.Body);
                    break;
            }
        }

        /// <summary>
        /// 处理业务消息帧，当前仅映射 DANMU_MSG。
        /// </summary>
        private void HandleMessageFrame(byte[] body)
        {
            Dictionary<string, object> root = LegacyJsonHelper.DeserializeObject(Encoding.UTF8.GetString(body));
            string command = LegacyJsonHelper.GetString(root, "cmd");
            if (!command.StartsWith("DANMU_MSG", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!LegacyJsonHelper.TryGetList(root, "info", out IList<object>? infoArray) || infoArray == null)
            {
                return;
            }

            string content = GetListString(infoArray, 1);
            string userId = string.Empty;
            string userName = string.Empty;
            if (TryGetNestedList(infoArray, 2, out IList<object>? userArray) && userArray != null)
            {
                userId = GetListString(userArray, 0);
                userName = GetListString(userArray, 1);
            }

            string badge = string.Empty;
            if (TryGetNestedList(infoArray, 3, out IList<object>? badgeArray) && badgeArray != null && badgeArray.Count > 1)
            {
                int badgeLevel = ToInt(badgeArray[0]);
                string badgeName = GetListString(badgeArray, 1);
                if (badgeLevel > 0 && !string.IsNullOrWhiteSpace(badgeName))
                {
                    badge = badgeLevel.ToString(CultureInfo.InvariantCulture) + badgeName;
                }
            }

            PublishMessage(new LiveMessage(
                DateTimeOffset.Now,
                Platform.ToDisplayName(),
                _currentRoomId ?? string.Empty,
                "Danmaku",
                userName,
                userId,
                badge,
                content,
                command));
        }

        /// <summary>
        /// 构造 Bilibili 鉴权 JSON 负载。
        /// </summary>
        private static byte[] BuildAuthPayload(BilibiliSessionInfo session)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append('{');
            builder.Append("\"uid\":");
            builder.Append(session.UserId.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"roomid\":");
            builder.Append(session.RealRoomId.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"protover\":");
            builder.Append(ProtoverZlib.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"platform\":\"web\"");
            builder.Append(",\"type\":2");
            builder.Append(",\"buvid\":\"");
            builder.Append(EscapeJson(session.Buvid));
            builder.Append('"');
            builder.Append(",\"key\":\"");
            builder.Append(EscapeJson(session.Token));
            builder.Append("\"}");
            return Encoding.UTF8.GetBytes(builder.ToString());
        }

        /// <summary>
        /// 封装二进制协议包（16 字节头 + body）。
        /// </summary>
        private byte[] BuildPacket(short version, int operation, byte[] body)
        {
            int sequence = Interlocked.Increment(ref _sequence);
            byte[] packet = new byte[HeaderLength + body.Length];
            WriteInt32BigEndian(packet, 0, packet.Length);
            WriteInt16BigEndian(packet, 4, HeaderLength);
            WriteInt16BigEndian(packet, 6, version);
            WriteInt32BigEndian(packet, 8, operation);
            WriteInt32BigEndian(packet, 12, sequence);
            if (body.Length > 0)
            {
                Buffer.BlockCopy(body, 0, packet, HeaderLength, body.Length);
            }

            return packet;
        }

        /// <summary>
        /// 解码一批帧，内部会递归处理压缩帧。
        /// </summary>
        private static IReadOnlyList<BilibiliFrame> DecodeFrames(byte[] bytes)
        {
            List<BilibiliFrame> frames = new List<BilibiliFrame>();
            DecodeRecursive(bytes, frames);
            return frames;
        }

        /// <summary>
        /// 递归解码：普通帧直接入列，zlib 压缩业务帧先解压再继续解析。
        /// </summary>
        private static void DecodeRecursive(byte[] bytes, List<BilibiliFrame> frames)
        {
            int offset = 0;
            while (offset + HeaderLength <= bytes.Length)
            {
                int packetLength = ReadInt32BigEndian(bytes, offset);
                if (packetLength <= HeaderLength || offset + packetLength > bytes.Length)
                {
                    break;
                }

                short headerLength = ReadInt16BigEndian(bytes, offset + 4);
                if (headerLength < HeaderLength || offset + headerLength > bytes.Length)
                {
                    break;
                }

                short version = ReadInt16BigEndian(bytes, offset + 6);
                int operation = ReadInt32BigEndian(bytes, offset + 8);
                int bodyLength = packetLength - headerLength;
                if (bodyLength < 0)
                {
                    break;
                }

                byte[] body = new byte[bodyLength];
                if (bodyLength > 0)
                {
                    Buffer.BlockCopy(bytes, offset + headerLength, body, 0, bodyLength);
                }

                if (version == ProtoverZlib && operation == OperationMessage)
                {
                    DecodeRecursive(DecompressZlib(body), frames);
                }
                else
                {
                    frames.Add(new BilibiliFrame(operation, version, body));
                }

                offset += packetLength;
            }
        }

        /// <summary>
        /// zlib 解压，去掉头尾包装字节后使用 DeflateStream。
        /// </summary>
        private static byte[] DecompressZlib(byte[] bytes)
        {
            if (bytes.Length <= 6)
            {
                return new byte[0];
            }

            using (MemoryStream input = new MemoryStream(bytes, 2, bytes.Length - 6))
            using (DeflateStream compressionStream = new DeflateStream(input, CompressionMode.Decompress))
            using (MemoryStream output = new MemoryStream())
            {
                compressionStream.CopyTo(output);
                return output.ToArray();
            }
        }

        private static bool TryAsDictionary(object value, out Dictionary<string, object>? result)
        {
            if (value is Dictionary<string, object> dictionary)
            {
                result = dictionary;
                return true;
            }

            result = null;
            return false;
        }

        private static bool TryGetNestedList(IList<object> source, int index, out IList<object>? result)
        {
            result = null;
            if (index < 0 || index >= source.Count)
            {
                return false;
            }

            try
            {
                result = LegacyJsonHelper.AsList(source[index]);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string GetListString(IList<object> source, int index)
        {
            if (index < 0 || index >= source.Count || source[index] == null)
            {
                return string.Empty;
            }

            return Convert.ToString(source[index], CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static int ToInt(object value)
        {
            if (value == null)
            {
                return 0;
            }

            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        private static string EscapeJson(string value)
        {
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }

        private static void WriteInt32BigEndian(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)((value >> 24) & 0xFF);
            buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 3] = (byte)(value & 0xFF);
        }

        private static void WriteInt16BigEndian(byte[] buffer, int offset, short value)
        {
            buffer[offset] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 1] = (byte)(value & 0xFF);
        }

        private static int ReadInt32BigEndian(byte[] buffer, int offset)
        {
            return (buffer[offset] << 24)
                | (buffer[offset + 1] << 16)
                | (buffer[offset + 2] << 8)
                | buffer[offset + 3];
        }

        private static short ReadInt16BigEndian(byte[] buffer, int offset)
        {
            return (short)((buffer[offset] << 8) | buffer[offset + 1]);
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
                // ignore background task failures during cleanup
            }
        }

        private sealed class BilibiliSessionInfo
        {
            public BilibiliSessionInfo(long realRoomId, long userId, string buvid, string token, Uri endpoint)
            {
                RealRoomId = realRoomId;
                UserId = userId;
                Buvid = buvid;
                Token = token;
                Endpoint = endpoint;
            }

            public long RealRoomId { get; private set; }

            public long UserId { get; private set; }

            public string Buvid { get; private set; }

            public string Token { get; private set; }

            public Uri Endpoint { get; private set; }
        }

        private sealed class BilibiliFrame
        {
            public BilibiliFrame(int operation, short version, byte[] body)
            {
                Operation = operation;
                Version = version;
                Body = body;
            }

            public int Operation { get; private set; }

            public short Version { get; private set; }

            public byte[] Body { get; private set; }
        }
    }
}
