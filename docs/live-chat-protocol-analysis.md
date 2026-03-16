# Live Chat Protocol Analysis

## Summary

This repository reverse engineers browser-side live chat traffic rather than using official SDKs. The Java codebase already contains five platform implementations:

| Platform | Init | Auth | Heartbeat | Message Body | C# viewer status |
| --- | --- | --- | --- | --- | --- |
| Bilibili | `room_init` + `getDanmuInfo` | WS auth packet with token, room id, `buvid`, uid | WS op `2` every 25s | Big-endian frame header + JSON payload, may be `zlib` or `brotli` | Implemented |
| Douyu | Resolve real room id + gateway `wss` + proxy list | Two-step WS login: control channel and danmu channel | `keeplive` on control channel, `mrkl` on danmu channel | Custom STT text protocol with little-endian headers | Implemented |
| Douyin | Room init + websocket query params | No explicit auth frame; query params + cookie carry state | `needAck` frame ack instead of classic heartbeat | Protobuf outer frame + gzip inner payload | Analysis only |
| Kuaishou | Room init returns `token` + `liveStreamId` | `CS_ENTER_ROOM` protobuf payload | `CS_HEARTBEAT` protobuf payload | Protobuf `SocketMessage` with `payloadType` dispatch | Analysis only |
| Huya | Parse room page to resolve channel ids | Register req + register group req via Tars frame | WS op `5` heartbeat every 25s | Tars encoded `WebSocketCommand` + command payload | Implemented |

## Bilibili

### Source Files

- `live-chat-clients/live-chat-client-bilibili/.../BilibiliLiveChatClient.java`
- `live-chat-clients/live-chat-client-bilibili/.../BilibiliConnectionHandler.java`
- `live-chat-clients/live-chat-client-bilibili/.../BilibiliCodecUtil.java`
- `live-chat-clients/live-chat-client-bilibili/.../BilibiliWebSocketFrameFactory.java`

### Flow

1. Call `room_init?id={roomId}` to resolve the real room id.
2. Call `getDanmuInfo?id={roomId}&type=0` to fetch the danmu token and host list.
3. Connect to `wss://{host}:{wss_port}/sub` or the default `wss://broadcastlv.chat.bilibili.com:443/sub`.
4. Send auth packet with:
   - `uid`
   - `roomid`
   - `protover`
   - `platform=web`
   - `type=2`
   - `buvid`
   - `key` (the danmu token)
5. Send heartbeat packet on a 25 second interval after an initial 15 second delay.

### Frame Structure

- 16-byte big-endian header
- `int32 length`
- `int16 headerLength`
- `int16 protover`
- `int32 operation`
- `int32 sequence`
- body bytes

### Important Operations

- `7`: auth
- `8`: auth reply
- `2`: heartbeat
- `3`: heartbeat reply
- `5`: business message

### Compression

- `protover=2`: `zlib`
- `protover=3`: `brotli`

The decompressed payload may itself contain multiple framed packets, so decoding must recurse until only plain packets remain.

### Danmaku Mapping

The Java implementation turns `cmd=DANMU_MSG` into a `DanmuMsgMsg` and reads:

- `info[1]` -> text content
- `info[2][0]` -> user id
- `info[2][1]` -> username
- `info[3][0]` -> badge level
- `info[3][1]` -> badge name

## Douyu

### Source Files

- `live-chat-clients/live-chat-client-douyu/.../DouyuLiveChatClient.java`
- `live-chat-clients/live-chat-client-douyu/.../DouyuWsLiveChatClient.java`
- `live-chat-clients/live-chat-client-douyu/.../DouyuDanmuLiveChatClient.java`
- `live-chat-clients/live-chat-client-douyu/.../DouyuConnectionHandler.java`
- `live-chat-clients/live-chat-client-douyu/.../DouyuCodecUtil.java`
- `live-chat-clients/live-chat-client-douyu/.../DouyuWebSocketFrameFactory.java`

### Flow

1. Resolve the real room id from `https://www.douyu.com/{roomId}`.
2. Call `POST https://www.douyu.com/lapi/live/gateway/web/{roomId}?isH5=1` to fetch the public WSS gateways.
3. Connect a control websocket and send an old-style `loginreq`.
4. After control channel `loginres`, send `keeplive`.
5. Wait for `msgrepeaterproxylist`, which contains the actual danmu relay endpoints.
6. Connect a second websocket to `wss://{ip}:{port}/`.
7. Send the new-style `loginreq` for danmu receiving.
8. After danmu `loginres`, send:
   - `joingroup`
   - `mrkl`
   - `sub`

### Frame Structure

Douyu uses a custom little-endian transport frame:

- `int32 packetLength`
- `int32 packetLength` again
- `int16 messageType`
- `byte 0`
- `byte 0`
- UTF-8 body ending with `\0`

`messageType=689` is client send, `690` is server receive.

### STT Encoding Rules

Body payloads are serialized as `key@=value/`.

Escaping:

- `@` -> `@A`
- `/` -> `@S`

Examples:

- `type@=loginreq/roomid@=74751/.../`
- `type@=chatmsg/nn@=username/txt@=666/`

### Danmaku Mapping

The Java `ChatmsgMsg` reads:

- `nn` -> username
- `uid` -> user id
- `txt` -> content
- `bl` -> badge level
- `bnn` -> badge name

## Douyin

### Source Files

- `live-chat-clients/live-chat-client-douyin/.../DouyinLiveChatClient.java`
- `live-chat-clients/live-chat-client-douyin/.../DouyinBinaryFrameHandler.java`

### Flow Highlights

- Websocket URL is built from room init results and a large query string.
- No explicit auth frame is sent after connect.
- Incoming payloads are `douyin_websocket_frame`, then gzip-decompressed into `douyin_websocket_frame_msg`.
- When `needAck` is true, the client must send an `ack` frame carrying `internalExt`.
- Business payloads are protobuf command messages such as:
  - `WebcastChatMessage`
  - `WebcastGiftMessage`
  - `WebcastMemberMessage`
  - `WebcastLikeMessage`

### Why Not Implemented in the First C# Viewer

The current environment is network-restricted, so pulling protobuf packages or codegen tools would create unnecessary friction for the first working desktop release.

## Kuaishou

### Source Files

- `live-chat-clients/live-chat-client-kuaishou/.../KuaishouLiveChatClient.java`
- `live-chat-clients/live-chat-client-kuaishou/.../KuaishouConnectionHandler.java`
- `live-chat-clients/live-chat-client-kuaishou/.../KuaishouBinaryFrameHandler.java`

### Flow Highlights

- Room init returns `token` and `liveStreamId`.
- Auth frame is a protobuf `SocketMessage` with `payloadType=CS_ENTER_ROOM`.
- Heartbeat frame is protobuf `CS_HEARTBEAT`.
- Incoming messages dispatch through protobuf `payloadType`, especially `SC_FEED_PUSH`.

## Huya

### Source Files

- `live-chat-clients/live-chat-client-huya/.../HuyaLiveChatClient.java`
- `live-chat-clients/live-chat-client-huya/.../HuyaConnectionHandler.java`
- `live-chat-clients/live-chat-client-huya/.../HuyaCodecUtil.java`

### Flow Highlights

- Huya uses custom WUP / Tars encoded payloads.
 - Auth path can use register + register-group frames to subscribe `live:{channelId}` and `chat:{channelId}`.
 - Message decoding relies on Tars-encoded business structures such as `MessageNoticeMsg`.

### C# Implementation Notes

- The C# viewer currently uses a lightweight Tars codec implementation dedicated to Huya command frames.
- Implemented command mappings:
  - `1400` (`MessageNotice`) -> danmaku
  - `6501` (`SendItemSubBroadcastPacket`) -> gift
  - `6110` (`VipEnterBanner`) -> enter-room event
- `WUP` function-level parsing is intentionally avoided in the first Huya version to keep dependencies minimal.

## C# Viewer Design

The desktop viewer intentionally standardizes only two things:

- `IPlatformClient`: one protocol implementation per platform
- `LiveMessage`: one normalized UI message model

That keeps all protocol-specific framing, auth, compression, and parsing logic isolated inside the platform client implementation. The UI remains stable while new platforms are added incrementally.
