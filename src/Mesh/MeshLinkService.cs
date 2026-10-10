// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/SLDataAPI)
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/MeSL)
// 准入模式改编自 SLDataAPI Voice/VoiceService.cs（TickOnce：pending 与已鉴权分池、Accept 上限、心跳日志）。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using MeSL.Core;
using MeSL.Voice;

namespace MeSL.Mesh;

/// <summary>
/// mesh 链路管理：一个 TCP 监听口 + 对字典序更大的节点主动拨号（每对节点只一条链路）。
/// 只做一跳：本服只发送本服玩家的语音，收到的远端语音绝不转发给第三方节点（拓扑为全互联 mesh，无环路）。
/// 全部在主线程 MEC 协程里轮询（与 SLDataAPI 语音服务同一方案），不引入后台线程与锁。
/// </summary>
public static class MeshLinkService
{
    private const int MaxPendingLinks = 8;
    private const double VoicePacketsPerSecondPerSpeaker = 110; // 10ms Opus 帧约 100 包/秒，留余量

    public sealed class Context
    {
        public string LocalId = "";
        public ChannelTable Channels = new(Array.Empty<string>());
        public readonly AuthLockout Lockout = new();
        public readonly NonceCache Nonces = new();
        public readonly Dictionary<string, PeerConfig> Peers = new(StringComparer.Ordinal);
        public int MaxSpeakersPerPeer = 8;

        public PeerConfig? FindPeer(string id) => Peers.TryGetValue(id ?? "", out PeerConfig p) ? p : null;
        public void OnAuthenticated(PeerLink link) => MeshLinkService.OnAuthenticated(link);
        public void OnReady(PeerLink link) => MeshLinkService.OnReady(link);
        public void OnMessage(PeerLink link, byte[] payload) => MeshLinkService.OnMessage(link, payload);
    }

    private sealed class DialState
    {
        public PeerConfig Peer = null!;
        public readonly ReconnectBackoff Backoff = new();
        public float NextAttemptAt;
        public Task<IPAddress[]>? Resolving;
        public PeerLink? Link;
    }

    private static Context _ctx = new();
    private static TcpListener? _listener;
    private static readonly List<PeerLink> Links = new();
    private static readonly Dictionary<string, DialState> Dialers = new(StringComparer.Ordinal);
    private static bool _running;
    private static float _lastSweep;
    private static float _lastHeartbeat;

    public static bool IsRunning => _running;
    public static bool HasOpenLinks { get; private set; }
    public static string LocalId => _ctx.LocalId;

    // ────────────── 生命周期 ──────────────

    /// <summary>校验配置并启动。fail-closed：server_id 非法则整体不启用；单个节点配置非法只跳过该节点。</summary>
    public static bool Start(Config cfg, ChannelTable channels)
    {
        if (_running) return true;
        if (!PeerAuth.IsValidServerId(cfg.ServerId))
        {
            Log.Error($"[MeSL] server_id \"{cfg.ServerId}\" 非法（1..32 位小写字母/数字/'-'），mesh 未启用");
            return false;
        }

        _ctx = new Context { LocalId = cfg.ServerId, Channels = channels, MaxSpeakersPerPeer = Math.Max(1, cfg.MaxRemoteSpeakersPerPeer) };
        foreach (PeerConfig p in cfg.Peers ?? new List<PeerConfig>())
        {
            string id = p?.ServerId ?? "";
            if (p == null || !PeerAuth.IsValidServerId(id)) { Log.Error($"[MeSL] 节点 server_id \"{id}\" 非法，已跳过"); continue; }
            if (id == cfg.ServerId) { Log.Error("[MeSL] peers 中包含本服自身，已跳过"); continue; }
            if (_ctx.Peers.ContainsKey(id)) { Log.Error($"[MeSL] 节点 {id} 重复配置，已跳过后者"); continue; }
            if (!PeerAuth.IsAcceptableSecret(p.Secret, out string why)) { Log.Error($"[MeSL] 节点 {id} 的 secret 不合格（{why}），已跳过"); continue; }
            if (string.IsNullOrWhiteSpace(p.Host) || p.Port <= 0 || p.Port > 65535) { Log.Error($"[MeSL] 节点 {id} 的 host/port 非法，已跳过"); continue; }
            _ctx.Peers[id] = p;
            if (MeshPolicy.ShouldDial(cfg.ServerId, id))
                Dialers[id] = new DialState { Peer = p };
        }

        if (_ctx.Peers.Count == 0)
        {
            Log.Warn("[MeSL] 没有可用的受信节点（peers 为空或全部非法），mesh 未启用");
            return false;
        }

        try
        {
            IPAddress bind = IPAddress.TryParse(cfg.ListenAddress, out IPAddress a) ? a : IPAddress.Any;
            _listener = new TcpListener(bind, cfg.ListenPort);
            _listener.Start();
        }
        catch (Exception ex)
        {
            _listener = null;
            Log.Error($"[MeSL] 无法监听 {cfg.ListenAddress}:{cfg.ListenPort}（{ex.Message}），本服只主动拨号，不接受入站链路");
        }

        _running = true;
        Log.Info($"[MeSL] mesh 已启动：server_id={cfg.ServerId}，频道=[{string.Join(", ", channels.MeshChannels)}]，" +
                 $"受信节点 {_ctx.Peers.Count} 个（本服拨号 {Dialers.Count} 个），监听 {(_listener != null ? cfg.ListenPort.ToString() : "关闭")}");
        return true;
    }

    public static void Stop()
    {
        if (!_running) return;
        _running = false;
        foreach (PeerLink l in Links)
        {
            try
            {
                l.SendBinary(MeshCodec.EncodeBye("shutdown"));
                l.Close("shutdown");
            }
            catch { /* 忽略 */ }
        }
        Links.Clear();
        Dialers.Clear();
        HasOpenLinks = false;
        try { _listener?.Stop(); } catch { /* 忽略 */ }
        _listener = null;
    }

    // ────────────── 主循环（Plugin 的 MEC 协程每帧调用） ──────────────

    public static void Tick()
    {
        if (!_running) return;
        float now = UnityEngine.Time.time;

        AcceptPending(now);
        ScheduleDials(now);

        for (int i = Links.Count - 1; i >= 0; i--)
        {
            PeerLink l = Links[i];
            try { l.Pump(now); }
            catch (Exception ex) { l.Close("pump: " + ex.Message); }
            if (l.IsClosed)
            {
                Links.RemoveAt(i);
                OnClosed(l, now);
            }
        }

        bool open = false;
        foreach (PeerLink l in Links)
            if (l.IsReady) { open = true; break; }
        HasOpenLinks = open;

        if (now - _lastSweep > 60f)
        {
            _lastSweep = now;
            _ctx.Lockout.Sweep(DateTime.UtcNow);
        }
        if (now - _lastHeartbeat > 300f)
        {
            _lastHeartbeat = now;
            Log.Info($"[MeSL] 心跳: 已就绪链路 {Links.Count(l => l.IsReady)}/{_ctx.Peers.Count}，握手中 {Links.Count(l => l.IsPending)}，" +
                     $"累计转发 {VoiceCapture.ForwardedPackets} 包，累计注入 {ProxySpeakerRegistry.InjectedPackets} 包，代理说话者 {ProxySpeakerRegistry.Count}");
        }
    }

    private static void AcceptPending(float now)
    {
        try
        {
            while (_listener != null && _listener.Pending())
            {
                Socket sock = _listener.AcceptSocket();
                int pending = Links.Count(l => !l.IsDialer && l.IsPending);
                if (pending >= MaxPendingLinks)
                {
                    Log.RateLimitedWarn("pending-full", $"[MeSL] 未握手入站链路已满（上限 {MaxPendingLinks}），拒绝新连接");
                    try { sock.Close(); } catch { /* 忽略 */ }
                    continue;
                }
                Links.Add(PeerLink.Accept(_ctx, sock, now));
            }
        }
        catch (Exception ex)
        {
            Log.RateLimitedWarn("accept", $"[MeSL] Accept 异常: {ex.Message}");
        }
    }

    private static void ScheduleDials(float now)
    {
        foreach (DialState d in Dialers.Values)
        {
            if (d.Link != null && !d.Link.IsClosed) continue;
            if (now < d.NextAttemptAt) continue;

            if (IPAddress.TryParse(d.Peer.Host, out IPAddress literal))
            {
                StartDial(d, literal, now);
                continue;
            }
            // 主机名解析在后台完成，主线程只轮询结果
            if (d.Resolving == null)
            {
                d.Resolving = Dns.GetHostAddressesAsync(d.Peer.Host);
                continue;
            }
            if (!d.Resolving.IsCompleted) continue;
            Task<IPAddress[]> t = d.Resolving;
            d.Resolving = null;
            IPAddress? addr = t.Status == TaskStatus.RanToCompletion ? t.Result.FirstOrDefault() : null;
            if (addr == null)
            {
                d.NextAttemptAt = now + (float)d.Backoff.NextDelaySeconds();
                Log.RateLimitedWarn("dns-" + d.Peer.ServerId, $"[MeSL] 无法解析节点 {d.Peer.ServerId} 的主机名 {d.Peer.Host}");
                continue;
            }
            StartDial(d, addr, now);
        }
    }

    private static void StartDial(DialState d, IPAddress addr, float now)
    {
        d.Link = PeerLink.Dial(_ctx, d.Peer, addr, now);
        Links.Add(d.Link);
    }

    // ────────────── 链路事件 ──────────────

    private static void OnAuthenticated(PeerLink link)
    {
        // 同一节点的新链路（已通过 HMAC + 防重放）顶替旧链路：旧链路多半已是半死连接
        foreach (PeerLink other in Links)
        {
            if (other != link && !other.IsClosed && other.PeerId == link.PeerId)
                other.Close("superseded");
        }
        link.VoiceBucket = new TokenBucket(
            VoicePacketsPerSecondPerSpeaker * _ctx.MaxSpeakersPerPeer,
            VoicePacketsPerSecondPerSpeaker * _ctx.MaxSpeakersPerPeer,
            UnityEngine.Time.realtimeSinceStartup);
    }

    private static void OnReady(PeerLink link)
    {
        if (link.PeerId != null && Dialers.TryGetValue(link.PeerId, out DialState d) && d.Link == link)
            d.Backoff.Reset();
        string shared = string.Join(", ", _ctx.Channels.MeshChannels.Where(link.AnnouncedChannel));
        Log.Info($"[MeSL] 链路就绪: {link.PeerId}（{link.RemoteIp}，{(link.IsDialer ? "本服拨号" : "对端拨号")}），共有频道 [{shared}]");
    }

    private static void OnClosed(PeerLink link, float now)
    {
        if (link.PeerId != null && link.RemoteHello != null)
        {
            Log.Info($"[MeSL] 链路断开: {link.PeerId}（{link.CloseReason}）");
            bool stillConnected = Links.Any(l => l.PeerId == link.PeerId && !l.IsClosed);
            if (!stillConnected) ProxySpeakerRegistry.ReleasePeer(link.PeerId);
        }
        else
        {
            Log.Debug($"[MeSL] 未就绪链路关闭: {link.PeerId ?? link.RemoteIp}（{link.CloseReason}）");
        }

        if (link.IsDialer && link.PeerId != null && Dialers.TryGetValue(link.PeerId, out DialState d) && d.Link == link)
        {
            d.Link = null;
            double delay = d.Backoff.NextDelaySeconds();
            d.NextAttemptAt = now + (float)delay;
            if (d.Backoff.Failures <= 1 || d.Backoff.Failures % 10 == 0)
                Log.Warn($"[MeSL] 节点 {link.PeerId} 链路不可用（{link.CloseReason}），{delay:0}s 后重连");
        }
    }

    private static void OnMessage(PeerLink link, byte[] payload)
    {
        string peerId = link.PeerId!;
        switch (payload[0])
        {
            case MeshMessageType.Voice:
                if (link.VoiceBucket != null && !link.VoiceBucket.TryTake(UnityEngine.Time.realtimeSinceStartup))
                {
                    Log.RateLimitedWarn("rate-" + peerId, $"[MeSL] 节点 {peerId} 语音包速率超限，已丢弃");
                    return;
                }
                if (!MeshCodec.TryDecodeVoice(payload, out MeshVoiceFrame frame)) { link.Close("malformed voice"); return; }
                int local = link.MapChannel(frame.ChannelIndex);
                if (local <= ChannelTable.LocalIndex) return;
                ProxySpeakerRegistry.Inject(peerId, frame, local);
                return;
            case MeshMessageType.Speaker:
                if (!MeshCodec.TryDecodeSpeaker(payload, out MeshSpeakerInfo info)) { link.Close("malformed speaker"); return; }
                ProxySpeakerRegistry.OnSpeakerInfo(peerId, info);
                return;
            case MeshMessageType.SpeakerGone:
                if (!MeshCodec.TryDecodeSpeakerGone(payload, out uint gone)) { link.Close("malformed speaker_gone"); return; }
                ProxySpeakerRegistry.Release(peerId, gone);
                return;
            case MeshMessageType.Bye:
                MeshCodec.TryDecodeBye(payload, out string reason);
                link.Close("bye: " + reason);
                return;
            default:
                link.Close("unknown message type 0x" + payload[0].ToString("x2"));
                return;
        }
    }

    // ────────────── 发送 ──────────────

    /// <summary>发给所有已就绪、且在 HELLO 中宣告了该频道的节点。</summary>
    public static void Broadcast(int localChannel, byte[] payload)
    {
        if (!HasOpenLinks || localChannel <= ChannelTable.LocalIndex) return;
        string name = _ctx.Channels.NameOf(localChannel);
        foreach (PeerLink l in Links)
            if (l.IsReady && l.AnnouncedChannel(name)) l.SendBinary(payload);
    }

    public static void BroadcastAll(byte[] payload)
    {
        if (!HasOpenLinks) return;
        foreach (PeerLink l in Links)
            if (l.IsReady) l.SendBinary(payload);
    }

    public static IEnumerable<string> DescribeLinks()
    {
        foreach (string id in _ctx.Peers.Keys)
        {
            PeerLink? l = Links.FirstOrDefault(x => x.PeerId == id && !x.IsClosed);
            string state = l == null ? "断开" : l.IsReady ? "就绪" : "握手中";
            yield return $"{id}: {state}{(l != null ? $"（{l.RemoteIp}）" : "")}";
        }
    }
}
