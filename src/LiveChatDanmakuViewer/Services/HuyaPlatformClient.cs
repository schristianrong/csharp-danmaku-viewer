using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LiveChatDanmakuViewer.Models;

namespace LiveChatDanmakuViewer.Services
{
    /// <summary>
    /// Huya 协议客户端。
    /// 通过 WebSocket + Tars/WUP 风格帧完成注册、分组订阅、心跳和推送解码。
    /// </summary>
    public sealed class HuyaPlatformClient : PlatformClientBase
    {
        private const string WebSocketEndpoint = "wss://cdnws.api.huya.com:443";
        private const string HuyaUaVersion = "2309271152";

        private const int OperationRegisterReq = 1;
        private const int OperationRegisterRsp = 2;
        private const int OperationHeartbeat = 5;
        private const int OperationHeartbeatAck = 6;
        private const int OperationPushMessage = 7;
        private const int OperationRegisterGroupReq = 16;
        private const int OperationRegisterGroupRsp = 17;
        private const int OperationPushMessageV2 = 22;

        private const long CommandMessageNotice = 1400;
        private const long CommandVipEnterBanner = 6110;
        private const long CommandSendItemSubBroadcastPacket = 6501;

        private static readonly Regex[] ChannelIdPatterns = new[]
        {
            new Regex("\"lChannelId\"\\D+(\\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            new Regex("\"lp\"\\D+(\\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        };

        private static readonly Regex[] SubChannelIdPatterns = new[]
        {
            new Regex("\"lSubChannelId\"\\D+(\\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            new Regex("\"lp\"\\D+(\\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        };

        private static readonly Regex[] AnchorUidPatterns = new[]
        {
            new Regex("\"yyid\"\\D+(\\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant),
        };

        private readonly HttpClient _httpClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        });

        private ClientWebSocket? _socket;
        private CancellationTokenSource? _runCts;
        private Task? _receiveTask;
        private Task? _heartbeatTask;
        private string? _currentRoomId;
        private long _channelId;

        public override ViewerPlatform Platform
        {
            get { return ViewerPlatform.Huya; }
        }

        /// <summary>
        /// 建立虎牙连接并发送 RegisterReq。
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
                throw new InvalidOperationException("Huya 房间号必须是数字。");
            }

            PublishState(ConnectionState.Connecting, "正在解析虎牙房间信息...");
            _currentRoomId = options.RoomId;
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            try
            {
                HuyaRoomInfo roomInfo = await ResolveRoomInfoAsync(roomId, options.Cookie, cancellationToken);
                _channelId = roomInfo.ChannelId;

                _socket = CreateWebSocket(options.Cookie);
                await _socket.ConnectAsync(new Uri(WebSocketEndpoint), cancellationToken);
                PublishLog(LogSeverity.Info, "Huya 已连接 WebSocket：" + WebSocketEndpoint);

                _receiveTask = Task.Run(() => RunReceiveLoopAsync(_socket, _runCts.Token), _runCts.Token);
                _heartbeatTask = Task.Run(() => RunHeartbeatLoopAsync(_socket, _runCts.Token), _runCts.Token);
                await SendBytesAsync(_socket, HuyaProtocol.EncodeRegisterRequest(roomInfo, HuyaUaVersion), cancellationToken);
                PublishLog(LogSeverity.Info, "Huya 已发送 RegisterReq，等待分组注册。");

                PublishState(ConnectionState.Connected, string.Format(CultureInfo.InvariantCulture, "已连接 Huya 房间 {0}", options.RoomId));
            }
            catch (Exception ex)
            {
                PublishLog(LogSeverity.Error, "Huya 连接失败：" + ex.Message);
                PublishState(ConnectionState.Faulted, ex.Message);
                await CleanupAsync(false);
                throw;
            }
        }

        /// <summary>
        /// 断开虎牙连接。
        /// </summary>
        protected override async Task DisconnectCoreAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CleanupAsync(true);
        }

        /// <summary>
        /// 清理 socket、后台任务与取消源。
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
                PublishState(ConnectionState.Disconnected, "Huya 连接已断开");
            }
        }

        /// <summary>
        /// 心跳任务：首次延迟后按固定周期发送心跳帧。
        /// </summary>
        private async Task RunHeartbeatLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
                while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    await SendBytesAsync(socket, HuyaProtocol.EncodeHeartbeat(), cancellationToken);
                    await Task.Delay(TimeSpan.FromSeconds(25), cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
        }

        /// <summary>
        /// 收包任务：读取并解析 Huya WebSocketCommand，再按 operation 分发。
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

                    HuyaWebSocketCommand command;
                    if (!HuyaProtocol.TryDecodeWebSocketCommand(rawMessage, out command))
                    {
                        PublishLog(LogSeverity.Warning, "Huya 收到无法解析的 WebSocketCommand。");
                        continue;
                    }

                    await HandleCommandAsync(socket, command, cancellationToken);
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    PublishLog(LogSeverity.Warning, "Huya 连接已被远端关闭。");
                    PublishState(ConnectionState.Faulted, "Huya 连接中断");
                }
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception ex)
            {
                PublishLog(LogSeverity.Error, "Huya 收包异常：" + ex.Message);
                PublishState(ConnectionState.Faulted, ex.Message);
            }
        }

        /// <summary>
        /// 按 operation 处理控制响应和业务推送。
        /// </summary>
        private async Task HandleCommandAsync(ClientWebSocket socket, HuyaWebSocketCommand command, CancellationToken cancellationToken)
        {
            switch (command.Operation)
            {
                case OperationRegisterRsp:
                    {
                        HuyaRegisterResponse registerResponse;
                        if (HuyaProtocol.TryDecodeRegisterResponse(command.Payload, out registerResponse))
                        {
                            if (registerResponse.Code == 0)
                            {
                                PublishLog(LogSeverity.Info, "Huya RegisterRsp 成功，开始注册分组。");
                                await SendBytesAsync(socket, HuyaProtocol.EncodeRegisterGroupRequest(_channelId), cancellationToken);
                            }
                            else
                            {
                                string message = string.IsNullOrWhiteSpace(registerResponse.Message)
                                    ? "未知错误"
                                    : registerResponse.Message;
                                PublishLog(LogSeverity.Warning, "Huya RegisterRsp 失败：" + message);
                            }
                        }
                        else
                        {
                            PublishLog(LogSeverity.Warning, "Huya RegisterRsp 解析失败。");
                        }

                        return;
                    }
                case OperationRegisterGroupRsp:
                    {
                        int registerGroupCode;
                        if (HuyaProtocol.TryDecodeRegisterGroupResponse(command.Payload, out registerGroupCode))
                        {
                            PublishLog(registerGroupCode == 0 ? LogSeverity.Info : LogSeverity.Warning,
                                registerGroupCode == 0
                                    ? "Huya RegisterGroupRsp 成功。"
                                    : "Huya RegisterGroupRsp 失败，code=" + registerGroupCode.ToString(CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            PublishLog(LogSeverity.Warning, "Huya RegisterGroupRsp 解析失败。");
                        }

                        return;
                    }
                case OperationPushMessage:
                    {
                        HuyaPushItem pushItem;
                        if (HuyaProtocol.TryDecodePushMessage(command.Payload, out pushItem))
                        {
                            HandlePushItem(pushItem);
                        }

                        return;
                    }
                case OperationPushMessageV2:
                    {
                        IReadOnlyList<HuyaPushItem> pushItems;
                        if (HuyaProtocol.TryDecodePushMessageV2(command.Payload, out pushItems))
                        {
                            for (int i = 0; i < pushItems.Count; i++)
                            {
                                HandlePushItem(pushItems[i]);
                            }
                        }

                        return;
                    }
                case OperationHeartbeatAck:
                    return;
                default:
                    return;
            }
        }

        /// <summary>
        /// 将虎牙推送消息映射为统一消息模型。
        /// </summary>
        private void HandlePushItem(HuyaPushItem pushItem)
        {
            switch (pushItem.Command)
            {
                case CommandMessageNotice:
                    {
                        HuyaMessageNotice messageNotice;
                        if (HuyaProtocol.TryDecodeMessageNotice(pushItem.Payload, out messageNotice))
                        {
                            PublishMessage(new LiveMessage(
                                DateTimeOffset.Now,
                                Platform.ToDisplayName(),
                                _currentRoomId ?? string.Empty,
                                "Danmaku",
                                messageNotice.UserName,
                                messageNotice.UserId,
                                string.Empty,
                                messageNotice.Content,
                                pushItem.Command.ToString(CultureInfo.InvariantCulture)));
                        }

                        return;
                    }
                case CommandSendItemSubBroadcastPacket:
                    {
                        HuyaGiftMessage giftMessage;
                        if (HuyaProtocol.TryDecodeGiftMessage(pushItem.Payload, out giftMessage))
                        {
                            string giftName = string.IsNullOrWhiteSpace(giftMessage.GiftName) ? "礼物" : giftMessage.GiftName;
                            int giftCount = giftMessage.Count <= 0 ? 1 : giftMessage.Count;
                            PublishMessage(new LiveMessage(
                                DateTimeOffset.Now,
                                Platform.ToDisplayName(),
                                _currentRoomId ?? string.Empty,
                                "Gift",
                                giftMessage.UserName,
                                giftMessage.UserId,
                                string.Empty,
                                "赠送 " + giftName + " x" + giftCount.ToString(CultureInfo.InvariantCulture),
                                pushItem.Command.ToString(CultureInfo.InvariantCulture)));
                        }

                        return;
                    }
                case CommandVipEnterBanner:
                    {
                        HuyaEnterMessage enterMessage;
                        if (HuyaProtocol.TryDecodeEnterMessage(pushItem.Payload, out enterMessage))
                        {
                            PublishMessage(new LiveMessage(
                                DateTimeOffset.Now,
                                Platform.ToDisplayName(),
                                _currentRoomId ?? string.Empty,
                                "Event",
                                enterMessage.UserName,
                                enterMessage.UserId,
                                string.Empty,
                                "进入直播间",
                                pushItem.Command.ToString(CultureInfo.InvariantCulture)));
                        }

                        return;
                    }
                default:
                    return;
            }
        }

        /// <summary>
        /// 解析房间页，提取 channelId / subChannelId / anchorUid。
        /// </summary>
        private async Task<HuyaRoomInfo> ResolveRoomInfoAsync(long roomId, string? cookie, CancellationToken cancellationToken)
        {
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "https://www.huya.com/" + roomId.ToString(CultureInfo.InvariantCulture)))
            {
                AddCommonHeaders(request);
                AddCookieHeader(request, cookie);

                using (HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken))
                {
                    response.EnsureSuccessStatusCode();
                    string html = await ReadAsStringAsync(response);

                    long channelId = MatchFirstLong(html, ChannelIdPatterns);
                    if (channelId <= 0)
                    {
                        throw new InvalidOperationException("Huya 房间初始化失败，未解析到频道 ID。");
                    }

                    long subChannelId = MatchFirstLong(html, SubChannelIdPatterns);
                    if (subChannelId <= 0)
                    {
                        subChannelId = channelId;
                    }

                    long anchorUid = MatchFirstLong(html, AnchorUidPatterns);
                    PublishLog(LogSeverity.Info, "Huya 房间解析结果：channel=" + channelId.ToString(CultureInfo.InvariantCulture));
                    return new HuyaRoomInfo(channelId, subChannelId, anchorUid);
                }
            }
        }

        /// <summary>
        /// 按候选正则顺序匹配第一个 long 值。
        /// </summary>
        private static long MatchFirstLong(string input, IEnumerable<Regex> patterns)
        {
            foreach (Regex pattern in patterns)
            {
                Match match = pattern.Match(input);
                if (!match.Success || match.Groups.Count < 2)
                {
                    continue;
                }

                long value;
                if (long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out value))
                {
                    return value;
                }
            }

            return 0L;
        }

        /// <summary>
        /// 安全等待后台任务结束，忽略异常。
        /// </summary>
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
                // ignore
            }
        }

        private sealed class HuyaRoomInfo
        {
            public HuyaRoomInfo(long channelId, long subChannelId, long anchorUid)
            {
                ChannelId = channelId;
                SubChannelId = subChannelId;
                AnchorUid = anchorUid;
            }

            public long ChannelId { get; private set; }

            public long SubChannelId { get; private set; }

            public long AnchorUid { get; private set; }
        }

        private sealed class HuyaWebSocketCommand
        {
            public HuyaWebSocketCommand(int operation, byte[] payload)
            {
                Operation = operation;
                Payload = payload;
            }

            public int Operation { get; private set; }

            public byte[] Payload { get; private set; }
        }

        private sealed class HuyaRegisterResponse
        {
            public HuyaRegisterResponse(int code, string message)
            {
                Code = code;
                Message = message;
            }

            public int Code { get; private set; }

            public string Message { get; private set; }
        }

        private sealed class HuyaPushItem
        {
            public HuyaPushItem(long command, byte[] payload)
            {
                Command = command;
                Payload = payload;
            }

            public long Command { get; private set; }

            public byte[] Payload { get; private set; }
        }

        private sealed class HuyaMessageNotice
        {
            public HuyaMessageNotice(string userId, string userName, string content)
            {
                UserId = userId;
                UserName = userName;
                Content = content;
            }

            public string UserId { get; private set; }

            public string UserName { get; private set; }

            public string Content { get; private set; }
        }

        private sealed class HuyaGiftMessage
        {
            public HuyaGiftMessage(string userId, string userName, string giftName, int count)
            {
                UserId = userId;
                UserName = userName;
                GiftName = giftName;
                Count = count;
            }

            public string UserId { get; private set; }

            public string UserName { get; private set; }

            public string GiftName { get; private set; }

            public int Count { get; private set; }
        }

        private sealed class HuyaEnterMessage
        {
            public HuyaEnterMessage(string userId, string userName)
            {
                UserId = userId;
                UserName = userName;
            }

            public string UserId { get; private set; }

            public string UserName { get; private set; }
        }

        /// <summary>
        /// Huya 业务协议编解码器（简化版 Tars 支持，仅覆盖当前使用命令）。
        /// </summary>
        private static class HuyaProtocol
        {
            private const byte TypeByte = 0;
            private const byte TypeShort = 1;
            private const byte TypeInt = 2;
            private const byte TypeLong = 3;
            private const byte TypeFloat = 4;
            private const byte TypeDouble = 5;
            private const byte TypeString1 = 6;
            private const byte TypeString4 = 7;
            private const byte TypeMap = 8;
            private const byte TypeList = 9;
            private const byte TypeStructBegin = 10;
            private const byte TypeStructEnd = 11;
            private const byte TypeZero = 12;
            private const byte TypeSimpleList = 13;

            /// <summary>
            /// 编码 RegisterReq，告知房间和客户端身份信息。
            /// </summary>
            public static byte[] EncodeRegisterRequest(HuyaRoomInfo roomInfo, string ver)
            {
                TarsWriter userInfoWriter = new TarsWriter();
                userInfoWriter.WriteLongField(0, roomInfo.AnchorUid);
                userInfoWriter.WriteBoolField(1, roomInfo.AnchorUid <= 0);
                userInfoWriter.WriteStringField(2, string.Empty);
                userInfoWriter.WriteStringField(3, string.Empty);
                userInfoWriter.WriteLongField(4, roomInfo.ChannelId);
                userInfoWriter.WriteLongField(5, roomInfo.SubChannelId);
                userInfoWriter.WriteLongField(6, roomInfo.AnchorUid > 0 ? roomInfo.AnchorUid : roomInfo.ChannelId);
                userInfoWriter.WriteLongField(7, 3);
                userInfoWriter.WriteStringField(8, string.Empty);
                userInfoWriter.WriteStringField(9, "webh5&" + ver + "&websocket");

                return EncodeWebSocketCommand(OperationRegisterReq, userInfoWriter.ToArray());
            }

            /// <summary>
            /// 编码 RegisterGroupReq，订阅 live/chat 分组。
            /// </summary>
            public static byte[] EncodeRegisterGroupRequest(long channelId)
            {
                TarsWriter registerGroupWriter = new TarsWriter();
                registerGroupWriter.WriteStringListField(0, new[]
                {
                    "live:" + channelId.ToString(CultureInfo.InvariantCulture),
                    "chat:" + channelId.ToString(CultureInfo.InvariantCulture),
                });
                registerGroupWriter.WriteStringField(1, string.Empty);

                return EncodeWebSocketCommand(OperationRegisterGroupReq, registerGroupWriter.ToArray());
            }

            /// <summary>
            /// 编码心跳帧。
            /// </summary>
            public static byte[] EncodeHeartbeat()
            {
                return EncodeWebSocketCommand(OperationHeartbeat, Array.Empty<byte>());
            }

            /// <summary>
            /// 解析 WebSocketCommand 外层结构。
            /// </summary>
            public static bool TryDecodeWebSocketCommand(byte[] bytes, out HuyaWebSocketCommand command)
            {
                command = new HuyaWebSocketCommand(0, Array.Empty<byte>());
                Dictionary<int, object?> map;
                if (!TryParseStruct(bytes, out map))
                {
                    return false;
                }

                long operation;
                byte[] payload;
                if (!TryGetLong(map, 0, out operation) || !TryGetBytes(map, 1, out payload))
                {
                    return false;
                }

                command = new HuyaWebSocketCommand((int)operation, payload);
                return true;
            }

            /// <summary>
            /// 解析 RegisterRsp。
            /// </summary>
            public static bool TryDecodeRegisterResponse(byte[] bytes, out HuyaRegisterResponse response)
            {
                response = new HuyaRegisterResponse(-1, string.Empty);
                Dictionary<int, object?> map;
                if (!TryParseStruct(bytes, out map))
                {
                    return false;
                }

                long code;
                if (!TryGetLong(map, 0, out code))
                {
                    return false;
                }

                string message;
                if (!TryGetString(map, 2, out message))
                {
                    message = string.Empty;
                }

                response = new HuyaRegisterResponse((int)code, message);
                return true;
            }

            /// <summary>
            /// 解析 RegisterGroupRsp。
            /// </summary>
            public static bool TryDecodeRegisterGroupResponse(byte[] bytes, out int code)
            {
                code = -1;
                Dictionary<int, object?> map;
                if (!TryParseStruct(bytes, out map))
                {
                    return false;
                }

                long parsedCode;
                if (!TryGetLong(map, 0, out parsedCode))
                {
                    return false;
                }

                code = (int)parsedCode;
                return true;
            }

            /// <summary>
            /// 解析 PushMessage（单条）。
            /// </summary>
            public static bool TryDecodePushMessage(byte[] bytes, out HuyaPushItem pushItem)
            {
                pushItem = new HuyaPushItem(0L, Array.Empty<byte>());
                Dictionary<int, object?> map;
                if (!TryParseStruct(bytes, out map))
                {
                    return false;
                }

                long command;
                byte[] payload;
                if (!TryGetLong(map, 1, out command) || !TryGetBytes(map, 2, out payload))
                {
                    return false;
                }

                pushItem = new HuyaPushItem(command, payload);
                return true;
            }

            /// <summary>
            /// 解析 PushMessageV2（批量）。
            /// </summary>
            public static bool TryDecodePushMessageV2(byte[] bytes, out IReadOnlyList<HuyaPushItem> pushItems)
            {
                List<HuyaPushItem> result = new List<HuyaPushItem>();
                Dictionary<int, object?> map;
                if (!TryParseStruct(bytes, out map))
                {
                    pushItems = result;
                    return false;
                }

                IList<object?> items;
                if (!TryGetList(map, 1, out items))
                {
                    pushItems = result;
                    return false;
                }

                for (int i = 0; i < items.Count; i++)
                {
                    Dictionary<int, object?>? item = items[i] as Dictionary<int, object?>;
                    if (item == null)
                    {
                        continue;
                    }

                    long command;
                    byte[] payload;
                    if (!TryGetLong(item, 0, out command) || !TryGetBytes(item, 1, out payload))
                    {
                        continue;
                    }

                    result.Add(new HuyaPushItem(command, payload));
                }

                pushItems = result;
                return result.Count > 0;
            }

            /// <summary>
            /// 解析弹幕消息体（MessageNotice）。
            /// </summary>
            public static bool TryDecodeMessageNotice(byte[] bytes, out HuyaMessageNotice messageNotice)
            {
                messageNotice = new HuyaMessageNotice(string.Empty, string.Empty, string.Empty);
                Dictionary<int, object?> map;
                if (!TryParseStruct(bytes, out map))
                {
                    return false;
                }

                string content;
                if (!TryGetString(map, 3, out content))
                {
                    return false;
                }

                string userId = string.Empty;
                string userName = string.Empty;
                Dictionary<int, object?> userInfo;
                if (TryGetStruct(map, 0, out userInfo))
                {
                    long uid;
                    if (TryGetLong(userInfo, 0, out uid))
                    {
                        userId = uid.ToString(CultureInfo.InvariantCulture);
                    }

                    string parsedName;
                    if (TryGetString(userInfo, 2, out parsedName))
                    {
                        userName = parsedName;
                    }
                }

                messageNotice = new HuyaMessageNotice(userId, userName, content);
                return true;
            }

            /// <summary>
            /// 解析礼物消息体。
            /// </summary>
            public static bool TryDecodeGiftMessage(byte[] bytes, out HuyaGiftMessage giftMessage)
            {
                giftMessage = new HuyaGiftMessage(string.Empty, string.Empty, string.Empty, 0);
                Dictionary<int, object?> map;
                if (!TryParseStruct(bytes, out map))
                {
                    return false;
                }

                long senderUid;
                if (!TryGetLong(map, 4, out senderUid))
                {
                    senderUid = 0;
                }

                string senderName;
                if (!TryGetString(map, 6, out senderName))
                {
                    senderName = string.Empty;
                }

                string giftName;
                if (!TryGetString(map, 20, out giftName))
                {
                    giftName = string.Empty;
                }

                long count;
                if (!TryGetLong(map, 2, out count))
                {
                    count = 1;
                }

                giftMessage = new HuyaGiftMessage(
                    senderUid.ToString(CultureInfo.InvariantCulture),
                    senderName,
                    giftName,
                    (int)Math.Max(1L, count));
                return true;
            }

            /// <summary>
            /// 解析入场消息体。
            /// </summary>
            public static bool TryDecodeEnterMessage(byte[] bytes, out HuyaEnterMessage enterMessage)
            {
                enterMessage = new HuyaEnterMessage(string.Empty, string.Empty);
                Dictionary<int, object?> map;
                if (!TryParseStruct(bytes, out map))
                {
                    return false;
                }

                long uid;
                if (!TryGetLong(map, 0, out uid))
                {
                    uid = 0;
                }

                string userName;
                if (!TryGetString(map, 1, out userName))
                {
                    userName = string.Empty;
                }

                enterMessage = new HuyaEnterMessage(uid.ToString(CultureInfo.InvariantCulture), userName);
                return true;
            }

            /// <summary>
            /// 编码 WebSocketCommand 外层结构。
            /// </summary>
            private static byte[] EncodeWebSocketCommand(int operation, byte[] payload)
            {
                TarsWriter writer = new TarsWriter();
                writer.WriteIntField(0, operation);
                writer.WriteBytesField(1, payload);
                writer.WriteLongField(2, 0);
                writer.WriteStringField(3, string.Empty);
                writer.WriteIntField(4, 0);
                writer.WriteLongField(5, 0);
                writer.WriteStringField(6, string.Empty);
                return writer.ToArray();
            }

            /// <summary>
            /// 尝试把 Tars 字节流解析为 tag-value 字典。
            /// </summary>
            private static bool TryParseStruct(byte[] bytes, out Dictionary<int, object?> map)
            {
                map = new Dictionary<int, object?>();
                try
                {
                    TarsParser parser = new TarsParser(bytes);
                    map = parser.ParseStruct();
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            private static bool TryGetLong(IDictionary<int, object?> map, int tag, out long value)
            {
                object? rawValue;
                if (!map.TryGetValue(tag, out rawValue))
                {
                    value = 0L;
                    return false;
                }

                if (rawValue is long)
                {
                    value = (long)rawValue;
                    return true;
                }

                if (rawValue is int)
                {
                    value = (int)rawValue;
                    return true;
                }

                if (rawValue is short)
                {
                    value = (short)rawValue;
                    return true;
                }

                if (rawValue is byte)
                {
                    value = (byte)rawValue;
                    return true;
                }

                value = 0L;
                return false;
            }

            private static bool TryGetString(IDictionary<int, object?> map, int tag, out string value)
            {
                object? rawValue;
                if (map.TryGetValue(tag, out rawValue) && rawValue is string)
                {
                    value = (string)rawValue;
                    return true;
                }

                value = string.Empty;
                return false;
            }

            private static bool TryGetBytes(IDictionary<int, object?> map, int tag, out byte[] value)
            {
                object? rawValue;
                if (map.TryGetValue(tag, out rawValue) && rawValue is byte[])
                {
                    value = (byte[])rawValue;
                    return true;
                }

                value = Array.Empty<byte>();
                return false;
            }

            private static bool TryGetStruct(IDictionary<int, object?> map, int tag, out Dictionary<int, object?> value)
            {
                object? rawValue;
                if (map.TryGetValue(tag, out rawValue) && rawValue is Dictionary<int, object?>)
                {
                    value = (Dictionary<int, object?>)rawValue;
                    return true;
                }

                value = new Dictionary<int, object?>();
                return false;
            }

            private static bool TryGetList(IDictionary<int, object?> map, int tag, out IList<object?> value)
            {
                object? rawValue;
                if (map.TryGetValue(tag, out rawValue) && rawValue is IList<object?>)
                {
                    value = (IList<object?>)rawValue;
                    return true;
                }

                value = Array.Empty<object?>();
                return false;
            }

            /// <summary>
            /// 最小 Tars 解析器：按 tag/type 逐字段读取。
            /// </summary>
            private sealed class TarsParser
            {
                private readonly byte[] _buffer;
                private int _position;
                private readonly int _end;

                public TarsParser(byte[] buffer)
                {
                    _buffer = buffer;
                    _position = 0;
                    _end = buffer.Length;
                }

                public Dictionary<int, object?> ParseStruct()
                {
                    Dictionary<int, object?> map = new Dictionary<int, object?>();
                    while (_position < _end)
                    {
                        int tag;
                        byte type;
                        if (!TryReadHeader(out tag, out type))
                        {
                            break;
                        }

                        if (type == TypeStructEnd)
                        {
                            break;
                        }

                        map[tag] = ReadValue(type);
                    }

                    return map;
                }

                /// <summary>
                /// 按类型码读取当前字段值。
                /// </summary>
                private object? ReadValue(byte type)
                {
                    switch (type)
                    {
                        case TypeByte:
                        case TypeShort:
                        case TypeInt:
                        case TypeLong:
                        case TypeZero:
                            return ReadNumber(type);
                        case TypeFloat:
                            return ReadFloat();
                        case TypeDouble:
                            return ReadDouble();
                        case TypeString1:
                        case TypeString4:
                            return ReadString(type);
                        case TypeSimpleList:
                            return ReadSimpleList();
                        case TypeStructBegin:
                            return ParseStruct();
                        case TypeList:
                            return ReadList();
                        case TypeMap:
                            return ReadMap();
                        default:
                            throw new InvalidDataException("Unsupported Tars type: " + type.ToString(CultureInfo.InvariantCulture));
                    }
                }

                private long ReadNumber(byte type)
                {
                    switch (type)
                    {
                        case TypeZero:
                            return 0L;
                        case TypeByte:
                            EnsureRemaining(1);
                            return unchecked((sbyte)_buffer[_position++]);
                        case TypeShort:
                            EnsureRemaining(2);
                            short shortValue = BinaryPrimitives.ReadInt16BigEndian(_buffer.AsSpan(_position, 2));
                            _position += 2;
                            return shortValue;
                        case TypeInt:
                            EnsureRemaining(4);
                            int intValue = BinaryPrimitives.ReadInt32BigEndian(_buffer.AsSpan(_position, 4));
                            _position += 4;
                            return intValue;
                        case TypeLong:
                            EnsureRemaining(8);
                            long longValue = BinaryPrimitives.ReadInt64BigEndian(_buffer.AsSpan(_position, 8));
                            _position += 8;
                            return longValue;
                        default:
                            throw new InvalidDataException("Cannot read numeric value from type: " + type.ToString(CultureInfo.InvariantCulture));
                    }
                }

                private float ReadFloat()
                {
                    EnsureRemaining(4);
                    int raw = BinaryPrimitives.ReadInt32BigEndian(_buffer.AsSpan(_position, 4));
                    _position += 4;
                    byte[] bytes = BitConverter.GetBytes(raw);
                    if (BitConverter.IsLittleEndian)
                    {
                        Array.Reverse(bytes);
                    }

                    return BitConverter.ToSingle(bytes, 0);
                }

                private double ReadDouble()
                {
                    EnsureRemaining(8);
                    long raw = BinaryPrimitives.ReadInt64BigEndian(_buffer.AsSpan(_position, 8));
                    _position += 8;
                    byte[] bytes = BitConverter.GetBytes(raw);
                    if (BitConverter.IsLittleEndian)
                    {
                        Array.Reverse(bytes);
                    }

                    return BitConverter.ToDouble(bytes, 0);
                }

                private string ReadString(byte type)
                {
                    int length;
                    switch (type)
                    {
                        case TypeString1:
                            EnsureRemaining(1);
                            length = _buffer[_position++];
                            break;
                        case TypeString4:
                            EnsureRemaining(4);
                            length = BinaryPrimitives.ReadInt32BigEndian(_buffer.AsSpan(_position, 4));
                            _position += 4;
                            break;
                        default:
                            throw new InvalidDataException("Cannot read string from type: " + type.ToString(CultureInfo.InvariantCulture));
                    }

                    if (length <= 0)
                    {
                        return string.Empty;
                    }

                    EnsureRemaining(length);
                    string value = Encoding.UTF8.GetString(_buffer, _position, length);
                    _position += length;
                    return value;
                }

                private byte[] ReadSimpleList()
                {
                    int innerTag;
                    byte innerType;
                    if (!TryReadHeader(out innerTag, out innerType) || innerType != TypeByte)
                    {
                        throw new InvalidDataException("Invalid simple-list inner type.");
                    }

                    int length = ReadContainerLength();
                    if (length <= 0)
                    {
                        return Array.Empty<byte>();
                    }

                    EnsureRemaining(length);
                    byte[] value = new byte[length];
                    Buffer.BlockCopy(_buffer, _position, value, 0, length);
                    _position += length;
                    return value;
                }

                private IList<object?> ReadList()
                {
                    int count = ReadContainerLength();
                    List<object?> list = new List<object?>(Math.Max(0, count));
                    for (int i = 0; i < count; i++)
                    {
                        int tag;
                        byte type;
                        if (!TryReadHeader(out tag, out type))
                        {
                            break;
                        }

                        if (type == TypeStructEnd)
                        {
                            break;
                        }

                        list.Add(ReadValue(type));
                    }

                    return list;
                }

                private IList<KeyValuePair<object?, object?>> ReadMap()
                {
                    int count = ReadContainerLength();
                    List<KeyValuePair<object?, object?>> list = new List<KeyValuePair<object?, object?>>(Math.Max(0, count));
                    for (int i = 0; i < count; i++)
                    {
                        list.Add(new KeyValuePair<object?, object?>(ReadTaggedValue(), ReadTaggedValue()));
                    }

                    return list;
                }

                private object? ReadTaggedValue()
                {
                    int tag;
                    byte type;
                    if (!TryReadHeader(out tag, out type))
                    {
                        return null;
                    }

                    if (type == TypeStructEnd)
                    {
                        return null;
                    }

                    return ReadValue(type);
                }

                private int ReadContainerLength()
                {
                    int tag;
                    byte type;
                    if (!TryReadHeader(out tag, out type))
                    {
                        return 0;
                    }

                    long length = ReadNumber(type);
                    return length <= 0 ? 0 : (int)length;
                }

                private bool TryReadHeader(out int tag, out byte type)
                {
                    if (_position >= _end)
                    {
                        tag = 0;
                        type = 0;
                        return false;
                    }

                    byte value = _buffer[_position++];
                    type = (byte)(value & 0x0F);
                    tag = (value & 0xF0) >> 4;
                    if (tag == 15)
                    {
                        EnsureRemaining(1);
                        tag = _buffer[_position++];
                    }

                    return true;
                }

                /// <summary>
                /// 校验剩余字节数，防止越界读取。
                /// </summary>
                private void EnsureRemaining(int count)
                {
                    if (_position + count > _end)
                    {
                        throw new EndOfStreamException("Unexpected end of Tars payload.");
                    }
                }
            }

            /// <summary>
            /// 最小 Tars 写入器：仅支持当前协议涉及的字段类型。
            /// </summary>
            private sealed class TarsWriter
            {
                private readonly MemoryStream _stream = new MemoryStream();

                public void WriteIntField(int tag, long value)
                {
                    WriteNumericField(tag, value);
                }

                public void WriteLongField(int tag, long value)
                {
                    WriteNumericField(tag, value);
                }

                public void WriteBoolField(int tag, bool value)
                {
                    WriteHeader(tag, TypeByte);
                    _stream.WriteByte(value ? (byte)1 : (byte)0);
                }

                public void WriteStringField(int tag, string value)
                {
                    string safeValue = value ?? string.Empty;
                    byte[] bytes = Encoding.UTF8.GetBytes(safeValue);
                    if (bytes.Length <= byte.MaxValue)
                    {
                        WriteHeader(tag, TypeString1);
                        _stream.WriteByte((byte)bytes.Length);
                    }
                    else
                    {
                        WriteHeader(tag, TypeString4);
                        WriteInt32(bytes.Length);
                    }

                    if (bytes.Length > 0)
                    {
                        _stream.Write(bytes, 0, bytes.Length);
                    }
                }

                public void WriteBytesField(int tag, byte[] value)
                {
                    byte[] safeValue = value ?? Array.Empty<byte>();
                    WriteHeader(tag, TypeSimpleList);
                    WriteHeader(0, TypeByte);
                    WriteNumericField(0, safeValue.Length);
                    if (safeValue.Length > 0)
                    {
                        _stream.Write(safeValue, 0, safeValue.Length);
                    }
                }

                public void WriteStringListField(int tag, IReadOnlyList<string> values)
                {
                    WriteHeader(tag, TypeList);
                    WriteNumericField(0, values.Count);
                    for (int i = 0; i < values.Count; i++)
                    {
                        WriteStringField(0, values[i] ?? string.Empty);
                    }
                }

                public byte[] ToArray()
                {
                    return _stream.ToArray();
                }

                private void WriteNumericField(int tag, long value)
                {
                    if (value == 0L)
                    {
                        WriteHeader(tag, TypeZero);
                        return;
                    }

                    if (value >= sbyte.MinValue && value <= sbyte.MaxValue)
                    {
                        WriteHeader(tag, TypeByte);
                        _stream.WriteByte(unchecked((byte)(sbyte)value));
                        return;
                    }

                    if (value >= short.MinValue && value <= short.MaxValue)
                    {
                        WriteHeader(tag, TypeShort);
                        WriteInt16((short)value);
                        return;
                    }

                    if (value >= int.MinValue && value <= int.MaxValue)
                    {
                        WriteHeader(tag, TypeInt);
                        WriteInt32((int)value);
                        return;
                    }

                    WriteHeader(tag, TypeLong);
                    WriteInt64(value);
                }

                private void WriteHeader(int tag, byte type)
                {
                    if (tag < 15)
                    {
                        _stream.WriteByte((byte)((tag << 4) | type));
                        return;
                    }

                    _stream.WriteByte((byte)(0xF0 | type));
                    _stream.WriteByte((byte)tag);
                }

                private void WriteInt16(short value)
                {
                    _stream.WriteByte((byte)((value >> 8) & 0xFF));
                    _stream.WriteByte((byte)(value & 0xFF));
                }

                private void WriteInt32(int value)
                {
                    _stream.WriteByte((byte)((value >> 24) & 0xFF));
                    _stream.WriteByte((byte)((value >> 16) & 0xFF));
                    _stream.WriteByte((byte)((value >> 8) & 0xFF));
                    _stream.WriteByte((byte)(value & 0xFF));
                }

                private void WriteInt64(long value)
                {
                    _stream.WriteByte((byte)((value >> 56) & 0xFF));
                    _stream.WriteByte((byte)((value >> 48) & 0xFF));
                    _stream.WriteByte((byte)((value >> 40) & 0xFF));
                    _stream.WriteByte((byte)((value >> 32) & 0xFF));
                    _stream.WriteByte((byte)((value >> 24) & 0xFF));
                    _stream.WriteByte((byte)((value >> 16) & 0xFF));
                    _stream.WriteByte((byte)((value >> 8) & 0xFF));
                    _stream.WriteByte((byte)(value & 0xFF));
                }
            }
        }
    }
}
