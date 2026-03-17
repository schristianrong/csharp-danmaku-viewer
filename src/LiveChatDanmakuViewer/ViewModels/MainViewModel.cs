using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LiveChatDanmakuViewer.Models;
using LiveChatDanmakuViewer.Services;

namespace LiveChatDanmakuViewer.ViewModels
{
    /// <summary>
    /// 主界面 ViewModel。
    /// 负责平台选择、连接生命周期、消息/日志集合维护以及 UI 状态联动。
    /// </summary>
    public sealed class MainViewModel : ObservableObject
    {
        private readonly Dispatcher _dispatcher;
        private IPlatformClient? _currentClient;
        private PlatformChoice _selectedPlatform;
        private string _roomId = "189201";
        private string _cookie = string.Empty;
        private ConnectionState _connectionState = ConnectionState.Disconnected;
        private string _statusDetail = "等待连接";
        private bool _isBusy;
        private bool _enableAsciiArt;

        /// <summary>
        /// 初始化主界面状态和平台列表。
        /// </summary>
        public MainViewModel()
        {
            _dispatcher = Application.Current.Dispatcher;
            List<PlatformChoice> platformChoices = new List<PlatformChoice>
            {
                new PlatformChoice(ViewerPlatform.Bilibili, "Bilibili B 站", "HTTP 取 token + WebSocket 二进制头 + zlib 解包"),
                new PlatformChoice(ViewerPlatform.Douyu, "Douyu 斗鱼", "控制通道拿转发节点 + Danmu 通道收消息 + STT 文本协议"),
                new PlatformChoice(ViewerPlatform.Huya, "Huya 虎牙", "WebSocket + Tars 帧，先走注册分组和消息推送链路"),
            };

            Platforms = new ReadOnlyCollection<PlatformChoice>(platformChoices);
            PlatformChoice? defaultPlatform = platformChoices.Find(choice => choice.Platform == ViewerPlatform.Huya);
            _selectedPlatform = defaultPlatform ?? platformChoices[0];
            Messages = new ObservableCollection<LiveMessage>();
            Logs = new ObservableCollection<ViewerLogEntry>();
        }

        /// <summary>
        /// 支持的平台集合（供下拉框绑定）。
        /// </summary>
        public ReadOnlyCollection<PlatformChoice> Platforms { get; private set; }

        /// <summary>
        /// 当前消息流（最新消息插入在前）。
        /// </summary>
        public ObservableCollection<LiveMessage> Messages { get; private set; }

        /// <summary>
        /// 当前日志流（最新日志插入在前）。
        /// </summary>
        public ObservableCollection<ViewerLogEntry> Logs { get; private set; }

        /// <summary>
        /// 当前选中的平台。
        /// </summary>
        public PlatformChoice SelectedPlatform
        {
            get { return _selectedPlatform; }
            set { SetProperty(ref _selectedPlatform, value); }
        }

        /// <summary>
        /// 目标房间号。
        /// </summary>
        public string RoomId
        {
            get { return _roomId; }
            set { SetProperty(ref _roomId, value); }
        }

        /// <summary>
        /// 请求 Cookie（可选）。
        /// </summary>
        public string Cookie
        {
            get { return _cookie; }
            set { SetProperty(ref _cookie, value); }
        }

        /// <summary>
        /// 连接状态短文本（用于 UI 标记）。
        /// </summary>
        public string StatusText
        {
            get
            {
                switch (_connectionState)
                {
                    case ConnectionState.Connected:
                        return "Connected";
                    case ConnectionState.Connecting:
                        return "Connecting";
                    case ConnectionState.Faulted:
                        return "Faulted";
                    default:
                        return "Disconnected";
                }
            }
        }

        /// <summary>
        /// 连接状态详情文本。
        /// </summary>
        public string StatusDetail
        {
            get { return _statusDetail; }
            private set { SetProperty(ref _statusDetail, value); }
        }

        /// <summary>
        /// 当前消息总数。
        /// </summary>
        public int MessageCount
        {
            get { return Messages.Count; }
        }

        /// <summary>
        /// 当前日志总数。
        /// </summary>
        public int LogCount
        {
            get { return Logs.Count; }
        }

        /// <summary>
        /// 是否处于忙碌状态（连接/断开中）。
        /// </summary>
        public bool IsBusy
        {
            get { return _isBusy; }
            private set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    RaiseStateProperties();
                }
            }
        }

        /// <summary>
        /// 是否允许点击“连接”。
        /// </summary>
        public bool CanConnect
        {
            get { return !IsBusy && _connectionState != ConnectionState.Connected; }
        }

        /// <summary>
        /// 是否允许点击“断开”。
        /// </summary>
        public bool CanDisconnect
        {
            get { return !IsBusy && _currentClient != null; }
        }

        /// <summary>
        /// 是否允许编辑平台/房间号/Cookie。
        /// </summary>
        public bool CanEditSettings
        {
            get { return !IsBusy && _connectionState != ConnectionState.Connected; }
        }

        /// <summary>
        /// 是否启用字符画模式展示弹幕内容。
        /// </summary>
        public bool EnableAsciiArt
        {
            get { return _enableAsciiArt; }
            set { SetProperty(ref _enableAsciiArt, value); }
        }

        /// <summary>
        /// 连接入口：参数校验 -> 旧连接清理 -> 创建平台客户端 -> 建立连接。
        /// </summary>
        public async Task ConnectAsync()
        {
            if (string.IsNullOrWhiteSpace(RoomId))
            {
                AppendLog(new ViewerLogEntry(DateTimeOffset.Now, "ERROR", "房间号不能为空。"));
                return;
            }

            if (_currentClient != null)
            {
                await DisconnectAsync();
            }

            IsBusy = true;
            SetConnectionState(ConnectionState.Connecting, "正在发起连接...");

            try
            {
                _currentClient = CreateClient(SelectedPlatform.Platform);
                _currentClient.MessageReceived += HandleMessageReceived;
                _currentClient.LogReceived += HandleLogReceived;
                _currentClient.ConnectionStateChanged += HandleConnectionStateChanged;

                await _currentClient.ConnectAsync(new ClientOptions(RoomId.Trim(), Cookie.Trim()), default(CancellationToken));
            }
            catch (Exception ex)
            {
                AppendLog(new ViewerLogEntry(DateTimeOffset.Now, "ERROR", "连接失败：" + ex.Message));
                SetConnectionState(ConnectionState.Faulted, ex.Message);
                await DisposeCurrentClientAsync();
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// 断开入口：通知平台断开并释放客户端资源。
        /// </summary>
        public async Task DisconnectAsync()
        {
            if (_currentClient == null)
            {
                return;
            }

            IsBusy = true;
            try
            {
                await _currentClient.DisconnectAsync(default(CancellationToken));
                await DisposeCurrentClientAsync();
                SetConnectionState(ConnectionState.Disconnected, "连接已断开");
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// 清空消息和日志面板。
        /// </summary>
        public void ClearStreams()
        {
            Messages.Clear();
            Logs.Clear();
            RaiseCollectionCounters();
        }

        /// <summary>
        /// 窗口关闭时调用，确保释放连接。
        /// </summary>
        public Task DisposeAsync()
        {
            return DisconnectAsync();
        }

        /// <summary>
        /// 平台客户端工厂。
        /// 新平台接入时在此处补充分支即可。
        /// </summary>
        private IPlatformClient CreateClient(ViewerPlatform platform)
        {
            switch (platform)
            {
                case ViewerPlatform.Bilibili:
                    return new BilibiliPlatformClient();
                case ViewerPlatform.Douyu:
                    return new DouyuPlatformClient();
                case ViewerPlatform.Huya:
                    return new HuyaPlatformClient();
                default:
                    throw new InvalidOperationException("暂不支持的平台。");
            }
        }

        /// <summary>
        /// 解除事件绑定并释放当前客户端。
        /// </summary>
        private async Task DisposeCurrentClientAsync()
        {
            if (_currentClient == null)
            {
                return;
            }

            _currentClient.MessageReceived -= HandleMessageReceived;
            _currentClient.LogReceived -= HandleLogReceived;
            _currentClient.ConnectionStateChanged -= HandleConnectionStateChanged;
            await _currentClient.DisposeAsync();
            _currentClient = null;
            RaiseStateProperties();
        }

        /// <summary>
        /// 消息事件处理：切回 UI 线程更新集合并做容量控制。
        /// </summary>
        private void HandleMessageReceived(LiveMessage message)
        {
            _dispatcher.BeginInvoke(new Action(delegate
            {
                Messages.Insert(0, TransformMessageForDisplay(message));
                // 限制 UI 列表长度，避免长时间运行导致内存和渲染压力上升。
                if (Messages.Count > 500)
                {
                    Messages.RemoveAt(Messages.Count - 1);
                }

                RaiseCollectionCounters();
            }));
        }

        /// <summary>
        /// 根据当前展示模式转换消息内容（普通文本 / 字符画）。
        /// </summary>
        private LiveMessage TransformMessageForDisplay(LiveMessage message)
        {
            if (!EnableAsciiArt || string.IsNullOrWhiteSpace(message.Content))
            {
                return message;
            }

            return new LiveMessage(
                message.Timestamp,
                message.Platform,
                message.RoomId,
                message.Category,
                message.UserName,
                message.UserId,
                message.Badge,
                AsciiArtRenderer.Render(message.Content),
                message.Command);
        }

        /// <summary>
        /// 日志事件处理：切回 UI 线程统一走 AppendLog。
        /// </summary>
        private void HandleLogReceived(ViewerLogEntry logEntry)
        {
            _dispatcher.BeginInvoke(new Action(delegate
            {
                AppendLog(logEntry);
            }));
        }

        /// <summary>
        /// 平台状态事件处理：切回 UI 线程更新状态。
        /// </summary>
        private void HandleConnectionStateChanged(ConnectionState state, string? detail)
        {
            _dispatcher.BeginInvoke(new Action(delegate
            {
                SetConnectionState(state, detail);
            }));
        }

        /// <summary>
        /// 插入日志并控制日志总量。
        /// </summary>
        private void AppendLog(ViewerLogEntry logEntry)
        {
            Logs.Insert(0, logEntry);
            // 与消息列表同理，限制日志数量可保持界面响应性稳定。
            if (Logs.Count > 200)
            {
                Logs.RemoveAt(Logs.Count - 1);
            }

            RaiseCollectionCounters();
        }

        /// <summary>
        /// 更新连接状态和状态详情文本，并触发相关按钮状态刷新。
        /// </summary>
        private void SetConnectionState(ConnectionState state, string? detail)
        {
            _connectionState = state;
            OnPropertyChanged(nameof(StatusText));

            if (detail != null)
            {
                StatusDetail = detail;
            }
            else
            {
                switch (state)
                {
                    case ConnectionState.Connected:
                        StatusDetail = "连接已建立";
                        break;
                    case ConnectionState.Connecting:
                        StatusDetail = "正在连接";
                        break;
                    case ConnectionState.Faulted:
                        StatusDetail = "连接失败";
                        break;
                    default:
                        StatusDetail = "等待连接";
                        break;
                }
            }

            RaiseStateProperties();
        }

        /// <summary>
        /// 通知消息数和日志数刷新。
        /// </summary>
        private void RaiseCollectionCounters()
        {
            OnPropertyChanged(nameof(MessageCount));
            OnPropertyChanged(nameof(LogCount));
        }

        /// <summary>
        /// 通知按钮可用性和输入可编辑性刷新。
        /// </summary>
        private void RaiseStateProperties()
        {
            OnPropertyChanged(nameof(CanConnect));
            OnPropertyChanged(nameof(CanDisconnect));
            OnPropertyChanged(nameof(CanEditSettings));
        }
    }
}
