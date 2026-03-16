using System;

namespace LiveChatDanmakuViewer.Models
{
    /// <summary>
    /// Viewer 支持的直播平台枚举。
    /// </summary>
    public enum ViewerPlatform
    {
        /// <summary>
        /// Bilibili 平台。
        /// </summary>
        Bilibili,

        /// <summary>
        /// Douyu 平台。
        /// </summary>
        Douyu,

        /// <summary>
        /// Huya 平台。
        /// </summary>
        Huya,
    }

    /// <summary>
    /// 客户端连接状态。
    /// </summary>
    public enum ConnectionState
    {
        /// <summary>
        /// 未连接。
        /// </summary>
        Disconnected,

        /// <summary>
        /// 正在连接。
        /// </summary>
        Connecting,

        /// <summary>
        /// 已连接。
        /// </summary>
        Connected,

        /// <summary>
        /// 连接故障（连接失败或中途断开）。
        /// </summary>
        Faulted,
    }

    /// <summary>
    /// 日志严重级别。
    /// </summary>
    public enum LogSeverity
    {
        /// <summary>
        /// 普通信息。
        /// </summary>
        Info,

        /// <summary>
        /// 可恢复问题或潜在风险。
        /// </summary>
        Warning,

        /// <summary>
        /// 错误信息。
        /// </summary>
        Error,
    }

    /// <summary>
    /// UI 平台下拉项模型。
    /// </summary>
    public sealed class PlatformChoice
    {
        /// <summary>
        /// 初始化一个平台选项。
        /// </summary>
        /// <param name="platform">平台枚举。</param>
        /// <param name="displayName">界面显示名。</param>
        /// <param name="note">平台协议要点说明。</param>
        public PlatformChoice(ViewerPlatform platform, string displayName, string note)
        {
            Platform = platform;
            DisplayName = displayName;
            Note = note;
        }

        /// <summary>
        /// 平台类型。
        /// </summary>
        public ViewerPlatform Platform { get; private set; }

        /// <summary>
        /// 平台显示名称。
        /// </summary>
        public string DisplayName { get; private set; }

        /// <summary>
        /// 平台简介或协议说明。
        /// </summary>
        public string Note { get; private set; }
    }

    /// <summary>
    /// 平台客户端连接参数。
    /// </summary>
    public sealed class ClientOptions
    {
        /// <summary>
        /// 初始化客户端连接参数。
        /// </summary>
        /// <param name="roomId">房间号。</param>
        /// <param name="cookie">请求 Cookie，可为空。</param>
        public ClientOptions(string roomId, string cookie)
        {
            RoomId = roomId;
            Cookie = cookie;
        }

        /// <summary>
        /// 房间号（字符串形式，便于跨平台兼容）。
        /// </summary>
        public string RoomId { get; private set; }

        /// <summary>
        /// 平台请求 Cookie。
        /// </summary>
        public string Cookie { get; private set; }
    }

    /// <summary>
    /// 统一后的直播消息模型。
    /// </summary>
    public sealed class LiveMessage
    {
        /// <summary>
        /// 初始化统一消息模型。
        /// </summary>
        /// <param name="timestamp">消息时间戳。</param>
        /// <param name="platform">平台显示名。</param>
        /// <param name="roomId">房间号。</param>
        /// <param name="category">消息类别（Danmaku / Gift / Event）。</param>
        /// <param name="userName">用户名。</param>
        /// <param name="userId">用户 ID。</param>
        /// <param name="badge">勋章/头衔文本。</param>
        /// <param name="content">消息内容。</param>
        /// <param name="command">协议原始命令字。</param>
        public LiveMessage(
            DateTimeOffset timestamp,
            string platform,
            string roomId,
            string category,
            string userName,
            string userId,
            string badge,
            string content,
            string command)
        {
            Timestamp = timestamp;
            Platform = platform;
            RoomId = roomId;
            Category = category;
            UserName = userName;
            UserId = userId;
            Badge = badge;
            Content = content;
            Command = command;
        }

        /// <summary>
        /// 消息采集时间。
        /// </summary>
        public DateTimeOffset Timestamp { get; private set; }

        /// <summary>
        /// 平台显示名称。
        /// </summary>
        public string Platform { get; private set; }

        /// <summary>
        /// 房间号。
        /// </summary>
        public string RoomId { get; private set; }

        /// <summary>
        /// 消息分类。
        /// </summary>
        public string Category { get; private set; }

        /// <summary>
        /// 用户名。
        /// </summary>
        public string UserName { get; private set; }

        /// <summary>
        /// 用户 ID。
        /// </summary>
        public string UserId { get; private set; }

        /// <summary>
        /// 勋章或头衔文本。
        /// </summary>
        public string Badge { get; private set; }

        /// <summary>
        /// 归一化后的消息内容。
        /// </summary>
        public string Content { get; private set; }

        /// <summary>
        /// 协议命令字，方便排查和扩展。
        /// </summary>
        public string Command { get; private set; }
    }

    /// <summary>
    /// 日志项模型。
    /// </summary>
    public sealed class ViewerLogEntry
    {
        /// <summary>
        /// 初始化日志项。
        /// </summary>
        /// <param name="timestamp">日志时间。</param>
        /// <param name="severity">严重级别文本。</param>
        /// <param name="message">日志内容。</param>
        public ViewerLogEntry(DateTimeOffset timestamp, string severity, string message)
        {
            Timestamp = timestamp;
            Severity = severity;
            Message = message;
        }

        /// <summary>
        /// 日志时间。
        /// </summary>
        public DateTimeOffset Timestamp { get; private set; }

        /// <summary>
        /// 严重级别。
        /// </summary>
        public string Severity { get; private set; }

        /// <summary>
        /// 日志文本。
        /// </summary>
        public string Message { get; private set; }
    }

    /// <summary>
    /// 平台枚举扩展方法。
    /// </summary>
    public static class ViewerPlatformExtensions
    {
        /// <summary>
        /// 将平台枚举转换成用于 UI 展示的名称。
        /// </summary>
        /// <param name="platform">平台枚举。</param>
        /// <returns>显示名称。</returns>
        public static string ToDisplayName(this ViewerPlatform platform)
        {
            switch (platform)
            {
                case ViewerPlatform.Bilibili:
                    return "Bilibili B 站";
                case ViewerPlatform.Douyu:
                    return "Douyu 斗鱼";
                case ViewerPlatform.Huya:
                    return "Huya 虎牙";
                default:
                    return platform.ToString();
            }
        }
    }
}
