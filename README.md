# MeSL

SCP:SL 的服务器间 P2P mesh 语音频道桥（LabAPI 插件）· P2P mesh voice-channel bridge for SCP:SL (LabAPI).

> **状态 / Status:** WIP 骨架 v0.1.0，尚未经过实机验证。WIP skeleton, not yet verified in-game.

## 中文

**作用：** 把多台受信服务器连成 mesh。玩家按一个键在「本地 → 频道1 → … → 本地」之间切换，说话仍然用原版的按住说话。

**规则：**
- 进入远程频道后，该玩家的本地近距离语音和对讲机语音都会被屏蔽。
- 阵营分组：A 组为 D 级 + 混沌，B 组为科学家 + 九尾狐。只有同组玩家互相听到，由服务端在接收时过滤，不依赖客户端静音。
- 默认开启对讲机门：人类必须在用对讲机发言时才会被转发。`force_radio: true` 可关闭这个限制（只影响发送端）。
- 远程语音在接收服只以对讲机播放，不会出现「会走路的假人」那种随距离衰减的近距离声音。
- 观察者只和观察者互通。

**设计（没有 SpeakerToy，也不依赖 SecretLabNAudio）：**
1. 发送端在 `SendingVoiceMessage` 时取出原始 Opus 包，原样转发，同时附带说话者所在位置（房间名/形状/区域 + 房间内坐标）。
2. 接收端为每个远程说话者生成一个 dummy 代理玩家。代理对所有客户端隐身，服务端位置放在同一房间的同一位置，作为对讲机发射点。
3. 通过原版 `VoiceTransceiver.ServerReceiveMessage` 把这个包**只以对讲机频道**重新注入（旁观者走旁观者频道），从不作为近距离语音播放。听者需要有开启的对讲机，阵营过滤在服务端执行。

协议细节见 [docs/MeSL-Protocol-v0.md](docs/MeSL-Protocol-v0.md)。

**配置**（`LabAPI/configs/<端口>/MeSL/config.yml`）：

```yaml
server_id: alpha
listen_port: 7790
channels: [mesh]
force_radio: false        # 只影响发送端雷达门；接收端恒为对讲机
remote_radio_range: UltraRange
peers:
  - server_id: beta
    host: 10.0.0.2
    port: 7790
    secret: "<≥32 字符的预共享密钥，两端相同>"
```

⚠️ v0 链路只做 HMAC 双向认证，**不加密**。请只对受信节点开放端口，并通过 WireGuard 等隧道传输。密钥切勿提交到仓库。

控制台命令 `mesl` 可以查看链路和代理状态。

## English

**What:** links trusted servers into a mesh of shared voice channels.
- One keybind cycles local → channel 1 → … → local. Vanilla hold-to-talk is unchanged.
- While in a remote channel, the player's local proximity and radio voice is blocked.
- Faction groups: A = Class-D + Chaos, B = Scientists + NTF. The faction filter runs server-side on receive.
- Radio gate is on by default; `force_radio: true` disables it (send side only).
- Remote voice is played on the receiving server as radio only, never as proximity.
- Spectators only hear spectators.

**How:**
- Opus bytes are forwarded as-is, together with the speaker's site (room + room-local position).
- The receiving server keeps a per-speaker dummy proxy, hidden from all clients, whose server-side position is the same site (the radio transmitter point).
- It re-injects the packet through vanilla `VoiceTransceiver.ServerReceiveMessage` on the **Radio** channel only (Spectator for spectators). Listeners need a working radio, and the faction filter runs server-side.
- No SpeakerToy, no SecretLabNAudio.

**Security:** v0 links use mutual HMAC-SHA256 authentication but are **not encrypted**. Run them over a tunnel such as WireGuard.

## Build / 构建

Game DLLs are not shipped; point `SCPSL_DIR` at a dedicated server's `SCPSL_Data/Managed`.
游戏 DLL 不随仓库分发，请把 `SCPSL_DIR` 指向专用服务器的 `SCPSL_Data/Managed` 目录。

```bash
dotnet build MeSL.csproj -c Release -p:SCPSL_DIR=/path/to/SCPSL_Data/Managed
dotnet test tests/MeSL.Core.Tests
```

## License / 许可

GPL-3.0-only © 2026 DNT_OF.

The voice capture, WebSocket framing, dedup and authentication patterns are adapted from [SLDataAPI](https://github.com/DNTOF/SLDataAPI) by DNT_OF. See [NOTICE](NOTICE) for the file mapping.
语音捕获、WebSocket 帧、去重和认证模式改编自 DNT_OF 的 SLDataAPI，详见 NOTICE。
