# LiveChatDanmakuViewer

![License](https://img.shields.io/badge/license-MIT-green)
![.NET](https://img.shields.io/badge/.NET-5.0-blue)
![WPF](https://img.shields.io/badge/UI-WPF-5C2D91)

> This project is in progress.
>
> `LiveChatDanmakuViewer` 是 `ordinaryroad-live-chat-client` 的 C# 桌面端 Viewer 子项目，用统一 UI 展示多平台直播间弹幕消息。
>
> 原作者项目地址：<https://github.com/OrdinaryRoad-Project/ordinaryroad-live-chat-client>
>
> 更新协议解析或平台接入时，建议同时更新本文档与 `docs/live-chat-protocol-analysis.md`。

* * *

## 项目特色

- Feature 0: 基于 `.NET 5.0 (net5.0-windows)` + `WPF` 的轻量桌面端实现。
- Feature 1: 统一抽象 `IPlatformClient`，协议层与 UI 层解耦。
- Feature 2: 已接入 Bilibili、Douyu、Huya 三条高频链路。
- Feature 3: 统一消息模型 `LiveMessage`，平台差异在 Service 层收敛。
- Feature 4: 内置连接状态、实时日志、消息流容量控制，便于排查问题。
- Feature 5: 平台扩展成本低，新增平台主要改枚举、工厂和平台客户端实现。

> 说明
>
> - ✅: 已在 Viewer 中可用
> - ☑️: 协议分析已有结论，但 Viewer 暂未接入

## 平台适配情况表

| 平台 | Viewer 接入 | 需 Cookie | 短房间号 | 说明 |
| --- | --- | --- | --- | --- |
| Bilibili B 站 | ✅ | ✅（建议） | ✅ | HTTP 初始化 + WebSocket 鉴权 + zlib |
| Douyu 斗鱼 | ✅ | ✅（建议） | ✅ | 控制通道 + 弹幕通道 + STT 协议 |
| Huya 虎牙 | ✅ | ✅（建议） | ✅ | WebSocket + Tars 编解码 |
| Douyin 抖音 | ☑️ | ☑️ | ✅ | 协议分析完成，Viewer 暂未接入 |
| Kuaishou 快手 | ☑️ | ☑️ | ✅ | 协议分析完成，Viewer 暂未接入 |

## 0 原理

核心思路是复用浏览器侧真实通信流程：  
先完成平台初始化和鉴权，再保持心跳并解析业务推送，最后映射为统一的 `LiveMessage` 给 UI 渲染。  
这样做的优势是无需平台开放接口权限，劣势是需要持续跟进平台协议变动。

## 1 快速开始

> 运行环境：`.NET SDK 5.0`（项目目标框架为 `net5.0-windows`）

```powershell
dotnet run --project .\src\LiveChatDanmakuViewer\LiveChatDanmakuViewer.csproj
```

## 2 使用

1. 选择平台（Bilibili / Douyu / Huya）。
2. 输入房间号。
3. 需要登录态时填入 Cookie（虎牙建议提供 Cookie 以提升稳定性）。
4. 点击“连接直播间”。
5. 在消息面板查看弹幕、礼物、入场事件与日志。

## 3 项目结构

```text
csharp-danmaku-viewer/
├─ live-chat-client-csharp-viewer.sln
├─ README.md
├─ docs/
│  ├─ project-introduction.md
│  └─ live-chat-protocol-analysis.md
└─ src/LiveChatDanmakuViewer/
   ├─ Models/                      # 统一模型层
   ├─ Services/                    # 平台协议实现层
   ├─ ViewModels/                  # 状态管理层
   ├─ MainWindow.xaml              # UI
   └─ LiveChatDanmakuViewer.csproj
```

## 4 扩展新平台

1. 在 `ViewerPlatform` 新增平台枚举。
2. 在 `MainViewModel.Platforms` 新增下拉选项。
3. 新建 `XXXPlatformClient`（建议继承 `PlatformClientBase`）。
4. 在 `MainViewModel.CreateClient` 增加工厂分发。
5. 将平台消息映射到 `LiveMessage`。
6. 更新 `README.md` 和本文档中的适配表。

## 5 常见问题

### 5.1 `NETSDK1045`

当前 SDK 不支持目标框架时，安装 `.NET 5 SDK` 并保持 `TargetFramework=net5.0-windows`。

### 5.2 构建时报文件占用（`MSB3021` / `MSB3027`）

`LiveChatDanmakuViewer.exe` 正在运行会锁定输出文件，关闭运行中的 Viewer 后重新构建即可。

### 5.3 连接成功但无消息

- 检查房间号是否有效。
- 检查是否需要有效 Cookie。
- 检查日志面板中的鉴权、节点解析、心跳异常信息。

## 6 项目来源与开源协议

- 原作者项目地址：<https://github.com/OrdinaryRoad-Project/ordinaryroad-live-chat-client>
- 开源协议：`MIT License`
- 协议文件：<https://github.com/OrdinaryRoad-Project/ordinaryroad-live-chat-client/blob/main/LICENSE>

## 7 相关文档

- 快速说明：`README.md`
- 协议分析：`docs/live-chat-protocol-analysis.md`
