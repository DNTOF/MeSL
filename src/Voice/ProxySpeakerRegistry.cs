// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/MeSL)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LabApi.Features.Wrappers;
using MeSL.Core;
using MeSL.Game;
using Mirror;
using NetworkManagerUtils.Dummies;
using PlayerRoles;
using UnityEngine;
using VoiceChat;
using VoiceChat.Networking;
using Player = LabApi.Features.Wrappers.Player;

namespace MeSL.Voice;

/// <summary>
/// 接收端重注入（MeSL-Protocol-v0 §6）。
/// 每个远端说话者 (peer, originNetId) 在本服对应一个 Dummy 代理：身份与发送方相同、站在还原后的采集站点，
/// 然后以代理自己的连接调用原版 VoiceTransceiver.ServerReceiveMessage——
/// 本服的 ValidateSend / 逐接收者 ValidateReceive（距离、对讲机）/ LabAPI ReceivingVoiceMessage（MeSL 阵营过滤）
/// 全部按原版路径自然生效，Opus 字节原样下发。
/// 必须一个说话者一个代理：客户端按说话者 Hub 各自维护 Opus 解码器，两人共用代理会互相破坏解码状态。
/// </summary>
public static class ProxySpeakerRegistry
{
    public sealed class Proxy
    {
        public string PeerId = "";
        public uint OriginNetId;
        public ReferenceHub Hub = null!;
        public Player Player = null!;
        public uint LocalNetId;
        public MeshGroup Group;
        public int LocalChannel;
        public RoleTypeId Role;
        public float LastSeen;
    }

    private static readonly Dictionary<(string Peer, uint NetId), Proxy> ByKey = new();
    private static readonly Dictionary<uint, Proxy> ByLocalNetId = new();
    private static readonly Dictionary<(string Peer, uint NetId), string> PendingNicknames = new();

    private static Action<NetworkConnection, VoiceMessage>? _serverReceive;
    private static int _maxTotal = 16;
    private static int _maxPerPeer = 8;
    private static float _idleSeconds = 6f;

    public static long InjectedPackets { get; private set; }
    public static int Count => ByKey.Count;

    /// <summary>绑定原版服务端语音入口。失败则重注入整体关闭（fail-safe：只丢远端语音，不影响本服）。</summary>
    public static bool Init(int maxTotal, int maxPerPeer, float idleSeconds)
    {
        _maxTotal = Math.Max(0, maxTotal);
        _maxPerPeer = Math.Max(0, maxPerPeer);
        _idleSeconds = Math.Max(1f, idleSeconds);
        try
        {
            MethodInfo? mi = typeof(VoiceTransceiver).GetMethod("ServerReceiveMessage",
                BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(NetworkConnection), typeof(VoiceMessage) }, null);
            if (mi == null)
            {
                Log.Error("[MeSL] 找不到 VoiceTransceiver.ServerReceiveMessage（游戏版本变化？），远端语音重注入已关闭");
                return false;
            }
            _serverReceive = (Action<NetworkConnection, VoiceMessage>)Delegate.CreateDelegate(
                typeof(Action<NetworkConnection, VoiceMessage>), mi);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[MeSL] 绑定原版语音入口失败，远端语音重注入已关闭: {ex.Message}");
            _serverReceive = null;
            return false;
        }
    }

    public static bool IsProxy(uint localNetId) => ByLocalNetId.ContainsKey(localNetId);

    public static bool TryGetProxy(uint localNetId, out Proxy proxy) => ByLocalNetId.TryGetValue(localNetId, out proxy!);

    public static void OnSpeakerInfo(string peerId, MeshSpeakerInfo info)
    {
        var key = (peerId, info.OriginNetId);
        PendingNicknames[key] = info.Nickname;
        if (PendingNicknames.Count > 256) PendingNicknames.Clear();
    }

    /// <summary>主线程：把一帧远端语音注入本服。frameLocalChannel 已由链路层按频道名映射为本地下标。</summary>
    public static void Inject(string peerId, MeshVoiceFrame frame, int frameLocalChannel)
    {
        if (_serverReceive == null || !Round.IsRoundStarted) return;

        var role = (RoleTypeId)frame.Role;
        if (!Enum.IsDefined(typeof(RoleTypeId), role)) return;
        // 对端声称的身份必须与其阵营分组一致，防止伪造 SCP / 非 mesh 身份借道发声
        if (GameMapping.GroupOf(role) != frame.Group || frame.Group == MeshGroup.None) return;

        var channel = (VoiceChatChannel)frame.VoiceChannel;
        bool spectator = frame.Group == MeshGroup.Spectator;
        if (spectator ? channel != VoiceChatChannel.Spectator
                      : channel != VoiceChatChannel.Proximity && channel != VoiceChatChannel.Radio)
            return;

        Proxy? proxy = GetOrSpawn(peerId, frame, role, frameLocalChannel);
        if (proxy == null) return;
        proxy.LastSeen = Time.time;
        proxy.LocalChannel = frameLocalChannel;
        proxy.Group = frame.Group;

        if (proxy.Player.Role != role)
        {
            // 发送方身份变了（或刚生成的 Dummy 还没完成上身份）：本包丢弃，下一包重试
            TrySetRole(proxy, role);
            return;
        }

        if (!spectator && !PlaceAtSite(proxy, frame, channel)) return;
        if (channel == VoiceChatChannel.Radio && !EnsureRadio(proxy)) return;

        var msg = new VoiceMessage(proxy.Hub, channel, frame.Opus, frame.OpusLength, false);
        try
        {
            _serverReceive(proxy.Hub.connectionToClient, msg);
            InjectedPackets++;
        }
        catch (Exception ex)
        {
            Log.RateLimitedWarn("inject", $"[MeSL] 远端语音注入异常: {ex.Message}");
        }
    }

    /// <summary>Tick 内调用：静默超时 / 已销毁的代理回收。</summary>
    public static void Cleanup()
    {
        if (ByKey.Count == 0) return;
        float now = Time.time;
        List<Proxy>? stale = null;
        foreach (Proxy p in ByKey.Values)
        {
            if (now - p.LastSeen > _idleSeconds || p.Hub == null)
                (stale ??= new List<Proxy>()).Add(p);
        }
        if (stale == null) return;
        foreach (Proxy p in stale) Despawn(p);
    }

    public static void Release(string peerId, uint originNetId)
    {
        if (ByKey.TryGetValue((peerId, originNetId), out Proxy p)) Despawn(p);
        PendingNicknames.Remove((peerId, originNetId));
    }

    public static void ReleasePeer(string peerId)
    {
        foreach (Proxy p in ByKey.Values.Where(x => x.PeerId == peerId).ToList()) Despawn(p);
        foreach (var k in PendingNicknames.Keys.Where(k => k.Peer == peerId).ToList()) PendingNicknames.Remove(k);
    }

    public static void ReleaseAll()
    {
        foreach (Proxy p in ByKey.Values.ToList()) Despawn(p);
        ByKey.Clear();
        ByLocalNetId.Clear();
        PendingNicknames.Clear();
    }

    /// <summary>回合重启时游戏会清掉所有 Dummy：只丢弃记录，不再 Destroy。</summary>
    public static void ForgetAll()
    {
        ByKey.Clear();
        ByLocalNetId.Clear();
        PendingNicknames.Clear();
    }

    // ────────────── 内部 ──────────────

    private static Proxy? GetOrSpawn(string peerId, MeshVoiceFrame frame, RoleTypeId role, int localChannel)
    {
        var key = (peerId, frame.OriginNetId);
        if (ByKey.TryGetValue(key, out Proxy existing))
        {
            if (existing.Hub != null) return existing;
            Despawn(existing);
        }

        if (ByKey.Count >= _maxTotal || ByKey.Values.Count(p => p.PeerId == peerId) >= _maxPerPeer)
        {
            Log.RateLimitedWarn("proxy-cap", $"[MeSL] 远端代理说话者已达上限（全服 {_maxTotal} / 每节点 {_maxPerPeer}），丢弃来自 {peerId} 的新说话者");
            return null;
        }

        string nick = PendingNicknames.TryGetValue(key, out string n) && n.Length > 0 ? n : "?";
        ReferenceHub? hub;
        try { hub = DummyUtils.SpawnDummy($"[{peerId}] {nick}"); }
        catch (Exception ex)
        {
            Log.RateLimitedWarn("spawn", $"[MeSL] 生成代理说话者失败: {ex.Message}");
            return null;
        }
        if (hub == null) return null;

        Player? player = Player.Get(hub);
        if (player == null)
        {
            try { NetworkServer.Destroy(hub.gameObject); } catch { /* 忽略 */ }
            return null;
        }

        var proxy = new Proxy
        {
            PeerId = peerId,
            OriginNetId = frame.OriginNetId,
            Hub = hub,
            Player = player,
            LocalNetId = player.NetworkId,
            Group = frame.Group,
            LocalChannel = localChannel,
            Role = role,
            LastSeen = Time.time,
        };
        ByKey[key] = proxy;
        ByLocalNetId[proxy.LocalNetId] = proxy;
        TrySetRole(proxy, role);
        Log.Debug($"[MeSL] 代理说话者已生成: {peerId}/{frame.OriginNetId} → netId {proxy.LocalNetId} ({role})");
        return proxy;
    }

    private static void TrySetRole(Proxy proxy, RoleTypeId role)
    {
        try
        {
            // RoleSpawnFlags.None：不发出生装备、不传送到出生点，位置由站点还原决定
            proxy.Player.SetRole(role, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.None);
            proxy.Role = role;
            if (role != RoleTypeId.Spectator)
                proxy.Player.IsGodModeEnabled = true;
        }
        catch (Exception ex)
        {
            Log.RateLimitedWarn("setrole", $"[MeSL] 代理说话者设置身份失败: {ex.Message}");
        }
    }

    /// <summary>把代理放到采集站点。站点无法还原时：对讲机帧仍可投递（对讲机不依赖位置），近距离帧丢弃。</summary>
    private static bool PlaceAtSite(Proxy proxy, MeshVoiceFrame frame, VoiceChatChannel channel)
    {
        if (frame.Site.HasValue && SiteResolver.TryResolve(frame.Site.Value, out Vector3 pos))
        {
            try
            {
                if ((proxy.Player.Position - pos).sqrMagnitude > 0.01f)
                    proxy.Player.Position = pos;
                return true;
            }
            catch (Exception ex)
            {
                Log.RateLimitedWarn("place", $"[MeSL] 代理说话者定位失败: {ex.Message}");
                return channel == VoiceChatChannel.Radio;
            }
        }
        return channel == VoiceChatChannel.Radio;
    }

    /// <summary>HumanVoiceModule.ValidateSend(Radio) 要求说话者背包里有可用对讲机。</summary>
    private static bool EnsureRadio(Proxy proxy)
    {
        try
        {
            if (proxy.Player.Items.OfType<RadioItem>().Any(r => r.IsUsable)) return true;
            proxy.Player.AddItem(ItemType.Radio);
            return proxy.Player.Items.OfType<RadioItem>().Any(r => r.IsUsable);
        }
        catch (Exception ex)
        {
            Log.RateLimitedWarn("radio", $"[MeSL] 代理说话者发放对讲机失败: {ex.Message}");
            return false;
        }
    }

    private static void Despawn(Proxy p)
    {
        ByKey.Remove((p.PeerId, p.OriginNetId));
        ByLocalNetId.Remove(p.LocalNetId);
        try
        {
            if (p.Hub != null) NetworkServer.Destroy(p.Hub.gameObject);
        }
        catch (Exception ex)
        {
            Log.RateLimitedWarn("despawn", $"[MeSL] 回收代理说话者异常: {ex.Message}");
        }
    }
}
