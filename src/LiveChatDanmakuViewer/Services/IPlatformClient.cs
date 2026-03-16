using System;
using System.Threading;
using System.Threading.Tasks;
using LiveChatDanmakuViewer.Models;

namespace LiveChatDanmakuViewer.Services
{
    /// <summary>
    /// 直播平台客户端统一接口。
    /// 每个平台实现独立的协议细节，但对上层暴露一致的连接与事件模型。
    /// </summary>
    public interface IPlatformClient
    {
        /// <summary>
        /// 当前客户端对应的平台类型。
        /// </summary>
        ViewerPlatform Platform { get; }

        /// <summary>
        /// 收到归一化后的直播消息时触发。
        /// </summary>
        event Action<LiveMessage>? MessageReceived;

        /// <summary>
        /// 客户端产生日志时触发。
        /// </summary>
        event Action<ViewerLogEntry>? LogReceived;

        /// <summary>
        /// 客户端连接状态变化时触发。
        /// </summary>
        event Action<ConnectionState, string?>? ConnectionStateChanged;

        /// <summary>
        /// 发起连接。
        /// </summary>
        /// <param name="options">连接参数。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        Task ConnectAsync(ClientOptions options, CancellationToken cancellationToken);

        /// <summary>
        /// 主动断开连接。
        /// </summary>
        /// <param name="cancellationToken">取消令牌。</param>
        Task DisconnectAsync(CancellationToken cancellationToken);

        /// <summary>
        /// 释放客户端资源。
        /// </summary>
        Task DisposeAsync();
    }
}
