# MeSL-Protocol-v0

> 状态：**草案 / WIP**（随 MeSL 0.1.0 骨架发布）。线路标识 `mesl/0`，HELLO `protocol_version = 0`。
> SPDX-License-Identifier: GPL-3.0-only · Copyright (C) 2026 DNT_OF

本文定义受信 SCP:SL 服务器之间通过 P2P mesh 互通语音频道的线路协议与两端语义。
关键词 MUST / SHOULD / MAY 按 RFC 2119 理解。

---

## 1. 术语

| 术语 | 含义 |
|---|---|
| 节点（peer） | 运行 MeSL 的一台 SCP:SL 专用服，用 `server_id` 标识 |
| 链路（link） | 两节点间的一条 TCP 连接（RFC 6455 帧） |
| mesh 频道 | 按**名称**全网匹配的逻辑语音频道；下标 0 恒为「本地」（原版语音，不上线路） |
| 发送服 / 接收服 | 说话者所在的服 / 把语音重注入的服 |
| 站点（site） | 采集时说话者所在位置：房间（名称/形状/区域）+ 房间局部坐标 + 世界坐标 |
| 代理说话者（proxy） | 接收服为每个远端说话者生成的 Dummy 玩家，语音以它的身份重注入 |
| 阵营分组 | A 组 = D 级 + 混沌；B 组 = 科学家 + 九尾狐（含设施警卫）；旁观者单独一组 |

`server_id` 与频道名规则相同：1..32 个字符，仅 `a-z 0-9 -`，不得以 `-` 开头；按字节比较。

## 2. 拓扑

- 全互联 mesh：每对受信节点之间最多一条链路。
- **拨号规则**：`server_id` 字典序（ordinal）较小的一方 MUST 主动拨号；较大的一方只接受。接受方收到方向相反的请求 MUST 回 `409`。
- **只做一跳**：节点 MUST 只发送本服真实玩家的语音；收到的远端语音（即代理说话者产生的包）MUST NOT 再转发给任何节点。由此全网无环路，也不需要 TTL。
- 同一节点的新链路通过鉴权后 MUST 顶替旧链路（旧链路多为半死连接）。

## 3. 传输与握手

### 3.1 传输

TCP 之上的 RFC 6455 子集：

- 只用单帧消息（FIN=1），不支持分片、扩展（RSV 位必须为 0）。
- 拨号方 → 接受方的帧 MUST 带掩码，反方向 MUST NOT 带掩码；违反即断连。
- 单帧负载上限 16 KiB；声明长度超过上限或超过接收缓冲容量 MUST 立即断连（不得等待）。
- 只允许 opcode `0x2`（binary）、`0x8`（close）、`0x9`（ping）、`0xA`（pong）；text 帧视为违规。
- 心跳：每 15 s 发一次 ping；45 s 无任何入站数据判死。

> **不加密。** v0 链路是明文 TCP，语音内容可被中间节点窃听。跨公网部署 MUST 放在 WireGuard / IPsec / SSH 隧道等加密通道内；`listen_port` 只应对受信节点开放。

### 3.2 拨号方请求

```http
GET /mesl/v0 HTTP/1.1
Host: <host>:<port>
Upgrade: websocket
Connection: Upgrade
Sec-WebSocket-Key: <base64(16 随机字节)>
Sec-WebSocket-Version: 13
X-MeSL-Server: <dialer_id>
X-MeSL-Target: <acceptor_id>
X-MeSL-Ts: <unix 秒>
X-MeSL-Nonce: <32 位小写 hex（16 随机字节）>
X-MeSL-Auth: <hex(HMAC-SHA256(secret, "mesl/0|req|<dialer_id>|<acceptor_id>|<ts>|<nonce>"))>
```

### 3.3 接受方校验顺序

1. 路径必须是 `GET /mesl/v0`，否则 `404`。
2. 来源 IP 处于失败锁定期 → `429`（不比较凭据）。
3. `X-MeSL-Server` 必须在本服 `peers` 中、`X-MeSL-Target` 必须等于本服 `server_id`，否则 `401`（计失败）。
4. `|now − ts| ≤ 60 s`、nonce 格式正确、HMAC 常量时间比较一致、`(dialer_id, nonce)` 未出现过，否则 `401`（计失败）。只有签名正确的 nonce 才进入重放缓存。
5. 若该节点配置的 `host` 是 IP 字面量，来源地址 MUST 与之相同，否则 `403`（计失败）。
6. 拨号方向错误 → `409`；缺少 WebSocket 升级头 → `400`。

失败锁定：同一来源 5 分钟内鉴权失败 10 次 → 锁定 5 分钟。未完成握手的入站连接独立计数（上限 8），10 s 内未完成握手 + HELLO 交换即断开，不占用已就绪链路。

### 3.4 接受方应答

```http
HTTP/1.1 101 Switching Protocols
Upgrade: websocket
Connection: Upgrade
Sec-WebSocket-Accept: <RFC 6455 accept>
X-MeSL-Server: <acceptor_id>
X-MeSL-Auth: <hex(HMAC-SHA256(secret, "mesl/0|resp|<acceptor_id>|<dialer_id>|<nonce>|<Sec-WebSocket-Accept>"))>
```

拨号方 MUST 校验 `Sec-WebSocket-Accept`、`X-MeSL-Server` 与应答 HMAC（绑定本次 nonce，防应答重放）；任一不符即断开并告警（密钥不一致或中间人）。密钥本身永不上线路。

### 3.5 密钥

每对节点一把预共享密钥，两端配置相同值。MUST ≥ 32 字符，不含空白与引号，不得是文档示例值；不合格的节点在启动时被跳过（fail-closed）。

## 4. 消息

握手完成后双方立即发送 HELLO。链路上每条 binary 帧恰好承载一条 MeSL 消息，首字节为类型。多字节整数一律**小端**；字符串为 `u8 长度 + UTF-8 字节`。任何解码失败（长度不符、尾部多余字节、未知类型/分组/标志位）MUST 断开链路。

| 类型 | 值 | 方向 | 说明 |
|---|---|---|---|
| VOICE | `0x01` | 双向 | 一个 Opus 包 |
| SPEAKER | `0x02` | 双向 | 说话者元数据 |
| SPEAKER_GONE | `0x03` | 双向 | 说话者已静默 / 离开，接收方可立即回收代理 |
| HELLO | `0x10` | 双向，各一次 | 协议版本与频道表 |
| BYE | `0x11` | 双向 | 主动断开原因 |

### 4.1 HELLO `0x10`

| 偏移 | 类型 | 字段 |
|---|---|---|
| 0 | u8 | `0x10` |
| 1 | u8 | protocol_version（v0 = `0`） |
| 2 | str | server_id（MUST 等于握手中的对端 ID） |
| … | u8 | channel_count（≤ 16） |
| … | str × n | 频道名（不得重复） |
| … | u16 | max_opus_bytes（信息性，v0 = 1500） |

HELLO 必须是链路上的第一条 binary 消息，且只能出现一次。版本不一致时发送 BYE 后断开。
**频道映射**：VOICE 中的 `channel_index` 是**发送方** HELLO 频道表的下标；接收方按名称映射到本地频道，本地没有的频道直接丢弃。发送方只把某频道的语音发给在 HELLO 中宣告了该频道的节点。

### 4.2 VOICE `0x01`

布局沿用 SLDataAPI 语音 WebSocket 二进制帧的思路（`0x01 | channel | id | seq | payload`），改为原样携带 Opus 字节并附带站点：

| 偏移 | 类型 | 字段 |
|---|---|---|
| 0 | u8 | `0x01` |
| 1 | u8 | channel_index（发送方频道表下标） |
| 2 | u8 | voice_channel：采集时**服务端校验后**的原版 `VoiceChatChannel`（`1` Proximity / `2` Radio / `4` Spectator） |
| 3 | u8 | group：`1` A 组 / `2` B 组 / `3` 旁观者 |
| 4 | u32 | origin_net_id：发送服上说话者的 netId |
| 8 | u32 | seq：按说话者递增（回绕） |
| 12 | u8 | flags：bit0 `NEW_BURST`，bit1 `HAS_SITE`，其余位 MUST 为 0 |
| 13 | i8 | role：发送服上的 `RoleTypeId` |
| 14 | site | 仅当 `HAS_SITE`：见 §5，28 字节 |
| … | u16 | opus_len（1..1500） |
| … | bytes | Opus 包，**逐字节原样转发**（48 kHz 单声道，原版 480 样本 / 10 ms 一包） |

- `voice_channel` MUST 取服务端校验结果（`VoiceModule.CurrentChannel`）。原版在 `PlayerSendingVoiceMessage` 触发时 `msg.Channel` 仍是客户端声称的值，不可信。
- `NEW_BURST`：距该说话者上一包超过 0.8 s。发送方在新讲话段开始或身份/昵称变化时先发一条 SPEAKER。
- 发送方按说话者对「与上一包内容完全相同」的包去重（FNV-1a 64），不得按（帧号, 频道, 长度）去重。

### 4.3 SPEAKER `0x02`

| 偏移 | 类型 | 字段 |
|---|---|---|
| 0 | u8 | `0x02` |
| 1 | u32 | origin_net_id |
| 5 | i8 | role |
| 6 | u8 | group |
| 7 | str | nickname（≤ 64 字节，已剥除控制字符） |

接收方 MUST 再次剥除控制字符后才用于日志或提示。

### 4.4 SPEAKER_GONE `0x03` / BYE `0x11`

- SPEAKER_GONE：`0x03 | u32 origin_net_id`。发送方在说话者静默超过 `proxy_idle_seconds` 或离开时发送。
- BYE：`0x11 | str reason（≤ 120 字节）`，随后关闭链路。

## 5. 站点（site）

| 偏移 | 类型 | 字段 |
|---|---|---|
| 0 | i16 | room_name（`MapGeneration.RoomName`；`-1` = 不在任何房间） |
| 2 | u8 | room_shape（`RoomShape`） |
| 3 | u8 | zone（`FacilityZone`） |
| 4 | f32 × 3 | 房间局部坐标（`room.Transform.InverseTransformPoint(pos)`） |
| 16 | f32 × 3 | 世界坐标 |

- 坐标 MUST 为有限值且 `|v| ≤ 10000`，否则整帧无效。
- 采集：旁观者不带站点；其他身份带站点。
- 还原（接收服）：在本服地图中找名称 + 形状 + 区域都相同的房间，用 `TransformPoint(局部坐标)` 还原；同类房间有多个时取还原点离发送方世界坐标最近的一个；找不到同类房间时，若发送方世界坐标落在本服某房间内则直接使用世界坐标，否则视为无法还原。
- 两服地图种子不同时，局部坐标仍能把代理放到「同一种房间的同一位置」；种子相同时与世界坐标结果一致。

## 6. 重注入语义（接收服）

接收服 MUST NOT 用 SpeakerToy 或任何二次编码的播放器回放远端语音，而是在**采集站点**以**等价说话者**的身份重注入，让本服原版的距离判定、对讲机判定与阵营过滤自然生效：

1. **一个远端说话者一个代理**：键为 `(peer_id, origin_net_id)`。客户端按说话者 Hub 各自维护 Opus 解码器，多人共用一个代理会破坏解码状态。代理是 Dummy 玩家（`DummyUtils.SpawnDummy`），身份设为帧中的 `role`（`RoleSpawnFlags.None`），人类代理开启无敌。
2. **身份校验**：`role` MUST 属于帧中的 `group`（例如 group=A 只能是 D 级 / 混沌身份），且 `voice_channel` 与分组相符（旁观者只能 Spectator，人类只能 Proximity / Radio）；否则丢弃。
3. **定位**：人类代理按 §5 放到还原站点。无法还原时 Radio 帧照常投递（对讲机不依赖位置），Proximity 帧丢弃。
4. **对讲机**：Radio 帧要求代理背包里有可用对讲机（原版 `HumanVoiceModule.ValidateSend(Radio)` 的要求），没有则发放一台。
5. **注入**：构造 `VoiceMessage(proxyHub, voice_channel, opus, len)`，以代理自己的连接调用原版 `VoiceTransceiver.ServerReceiveMessage`。原版随后依次执行：说话者校验、限流、静音检查 → `ValidateSend` → `PlayerSendingVoiceMessage` → 对每个接收者 `ValidateReceive`（距离 / 可见性 / 对讲机）→ `PlayerReceivingVoiceMessage` → 下发 Opus 字节。
6. **MeSL 接收过滤**（服务端、`PlayerReceivingVoiceMessage`，从不依赖客户端静音）：代理的语音只投递给本服真人，且接收者 MUST 当前选择同一 mesh 频道、阵营分组与帧相同。原版 `HumanVoiceModule.ValidateReceive` 本身**不区分**人类阵营，阵营隔离完全由这一步保证。
7. **回收**：代理静默超过 `proxy_idle_seconds`（默认 6 s）、收到 SPEAKER_GONE、链路断开或回合重启时销毁。
8. **副作用隔离**：代理只允许由 MeSL 改变身份（拒绝复活波次与其他来源的改动）；回合结束判定排除代理后重算存活阵营。

## 7. 发送规则（发送服）

| 玩家状态 | 行为 |
|---|---|
| 本地频道 | 原版语音，不转发 |
| 远程频道 · 人类 · 对讲机发言 | 转发 |
| 远程频道 · 人类 · 近距离发言 | 雷达门（默认）：不转发并提示需要对讲机；`force_radio: true`：转发 |
| 远程频道 · 旁观者 · 旁观者频道 | 转发（只会送达远端旁观者） |
| 远程频道 · SCP / 教程 / Overwatch 等 | 不可选择远程频道；身份变为不可用时自动回到本地 |

- **本地屏蔽**：玩家处于远程频道时，其 Proximity / Radio / Spectator 语音 MUST NOT 投递给本服任何人。对讲机必须一并屏蔽：客户端收到 Radio 消息时会同时写入近距离播放器，服务端无法只放行对讲机那一半。Intercom 等其他频道保持原版。
- **切换**：一个 SSS 按键循环 `本地 → 频道1 → … → 频道N → 本地`；说话仍用原版按住说话。
- 静音玩家：原版在 `PlayerSendingVoiceMessage` 之前就拒绝静音玩家，因此不会被采集。

## 8. 限额

| 项 | 值 |
|---|---|
| 单帧负载 | 16 KiB |
| Opus 包 | 1..1500 字节 |
| 频道数 | ≤ 16 |
| 未握手入站连接 | 8 |
| 握手 + HELLO 超时 | 10 s |
| 入站 VOICE 速率 | 每节点 110 包/s × `max_remote_speakers_per_peer`（令牌桶），超出丢弃 |
| 代理说话者 | 全服 `max_remote_speakers`（16）/ 每节点 `max_remote_speakers_per_peer`（8） |
| 发送缓冲持续不可写 | 3 s 判死；不可写期间语音帧直接丢弃 |

## 9. 版本演进

- 不兼容变更 MUST 提升 `protocol_version` 与 `/mesl/v<N>` 路径。
- v0 内新增消息类型属于不兼容变更（v0 接收方遇到未知类型会断开）。

## 10. 来源

链路准入、帧解析与语音包处理沿用 [DNTOF/SLDataAPI](https://github.com/DNTOF/SLDataAPI) 语音转发（`Voice/VoiceService.cs`）中经线上验证的做法：主线程轮询、未握手 / 已鉴权分池、握手超时、`Poll` 写守卫 + 不可写判死、帧长回绕与缓冲容量检查、netId 为键、FNV-1a 内容去重、0.8 s 讲话段与说话者帧去重；以及 `Control/ControlAuth.cs` 的失败锁定与常量时间比较。详见仓库 NOTICE。

## 11. 待定事项

- `force_radio: true` 时近距离发言目前按 Proximity 在站点重注入（只有站点附近、同频道同阵营的接收者听得到）。是否改为升级成 Radio 投递，待产品确认。
- 对讲机档位（range）未上线路，代理使用默认档位。
- 代理 Dummy 在本服可见（实体出现在站点）。不能用隐身效果隐藏：原版近距离判定依赖可见性，隐身会导致近距离语音收不到。
- 链路加密（v1 候选：Noise / TLS-PSK）。
