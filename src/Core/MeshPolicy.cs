// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/MeSL)

using System;
using System.Collections.Generic;

namespace MeSL.Core;

/// <summary>原版语音频道在 MeSL 里的归类（游戏层把 VoiceChatChannel 映射到这里）。</summary>
public enum CaptureKind
{
    Other = 0,
    Proximity = 1,
    Radio = 2,
    Spectator = 3,
}

public enum SendVerdict
{
    /// <summary>玩家在本地频道：原版语音，不转发。</summary>
    Local,
    /// <summary>转发到 mesh。</summary>
    Forward,
    /// <summary>在远程频道但未使用对讲机（雷达门生效）：不转发，本地近距离仍被屏蔽。</summary>
    NeedRadio,
    /// <summary>在远程频道但当前身份 / 频道不可转发（SCP、Intercom 等）：丢弃。</summary>
    Drop,
}

/// <summary>MeSL 语音规则（产品决策的唯一实现处，游戏层只做类型映射）。</summary>
public static class MeshPolicy
{
    /// <summary>
    /// 发送判定（只影响发送服）。雷达门（默认）：人类必须正在用对讲机发言才转发；
    /// ForceRadio=true 关闭雷达门，近距离发言也转发——但接收服仍一律按对讲机重注入（见 <see cref="ReinjectKind"/>）。
    /// 旁观者无对讲机，旁观者频道发言始终可转发（只会送达远端旁观者）。
    /// </summary>
    public static SendVerdict EvaluateSend(bool inRemoteChannel, MeshGroup group, CaptureKind kind, bool forceRadio)
    {
        if (!inRemoteChannel) return SendVerdict.Local;
        switch (group)
        {
            case MeshGroup.Spectator:
                return kind == CaptureKind.Spectator ? SendVerdict.Forward : SendVerdict.Drop;
            case MeshGroup.GroupA:
            case MeshGroup.GroupB:
                if (kind == CaptureKind.Radio) return SendVerdict.Forward;
                if (kind == CaptureKind.Proximity) return forceRadio ? SendVerdict.Forward : SendVerdict.NeedRadio;
                return SendVerdict.Drop;
            default:
                return SendVerdict.Drop;
        }
    }

    /// <summary>
    /// 本地投递屏蔽：说话者切到远程频道后，其近距离 / 对讲机 / 旁观者语音不再投递给本服任何人。
    /// 对讲机必须一起屏蔽：客户端 HumanVoiceModule 收到 Radio 消息时会同时写入近距离播放器，
    /// 服务端无法只放行「对讲机那一半」。Intercom 等其他频道保持原版。
    /// </summary>
    public static bool BlockLocalDelivery(bool senderInRemoteChannel, CaptureKind deliveredKind)
    {
        if (!senderInRemoteChannel) return false;
        return deliveredKind == CaptureKind.Proximity || deliveredKind == CaptureKind.Radio || deliveredKind == CaptureKind.Spectator;
    }

    /// <summary>
    /// 远端语音在接收服的唯一重注入频道：人类一律 Radio（与采集时是近距离还是对讲机无关），
    /// 旁观者为 Spectator（本身不带空间定位）。远端语音永远不以 Proximity 重注入。
    /// </summary>
    public static CaptureKind ReinjectKind(MeshGroup group) => group switch
    {
        MeshGroup.GroupA or MeshGroup.GroupB => CaptureKind.Radio,
        MeshGroup.Spectator => CaptureKind.Spectator,
        _ => CaptureKind.Other,
    };

    /// <summary>
    /// 接收端（服务器侧）阵营过滤：永不信任客户端静音。代理说话者的语音只投递给
    /// 「同一 mesh 频道 + 同一阵营分组」的本服玩家；旁观者只与旁观者互通；
    /// 实际投递频道必须是 <see cref="ReinjectKind"/>，其余（尤其 Proximity）一律拒绝。
    /// </summary>
    public static bool AllowRemoteReceive(MeshGroup frameGroup, int frameLocalChannel, MeshGroup receiverGroup, int receiverChannel,
        CaptureKind deliveredKind)
    {
        if (frameGroup == MeshGroup.None || receiverGroup == MeshGroup.None) return false;
        if (deliveredKind != ReinjectKind(frameGroup)) return false;
        if (frameLocalChannel <= ChannelTable.LocalIndex || receiverChannel != frameLocalChannel) return false;
        return frameGroup == receiverGroup;
    }

    /// <summary>每对节点只建一条链路：server_id 字典序较小的一方主动拨号。</summary>
    public static bool ShouldDial(string localId, string peerId) => string.CompareOrdinal(localId, peerId) < 0;
}

/// <summary>
/// 本服频道表。下标 0 固定为本地（原版）语音，不上线路；1..N 为 mesh 频道，
/// 线路上用 (下标-1) 指向发送方 HELLO 中的频道名，接收方按名称映射回本地下标。
/// </summary>
public sealed class ChannelTable
{
    public const int LocalIndex = 0;
    private readonly List<string> _mesh;

    public ChannelTable(IEnumerable<string> meshChannels)
    {
        _mesh = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string c in meshChannels)
        {
            if (!MeshCodec.IsValidChannelName(c) || !seen.Add(c)) continue;
            if (_mesh.Count >= MeshCodec.MaxChannels) break;
            _mesh.Add(c);
        }
    }

    public IReadOnlyList<string> MeshChannels => _mesh;

    /// <summary>可选位置总数（含本地）。</summary>
    public int Count => _mesh.Count + 1;

    public string NameOf(int index) => index <= LocalIndex || index > _mesh.Count ? "local" : _mesh[index - 1];

    /// <summary>按键循环：本地 → 频道1 → … → 频道N → 本地。</summary>
    public int Next(int current) => current < LocalIndex || current >= _mesh.Count ? LocalIndex : current + 1;

    public byte WireIndexOf(int localIndex) => (byte)(localIndex - 1);

    /// <summary>把对端 HELLO 的频道表映射成「对端线路下标 → 本地下标」，本地没有的频道映射为 -1（丢弃）。</summary>
    public int[] MapPeerChannels(IReadOnlyList<string> peerChannels)
    {
        var map = new int[peerChannels.Count];
        for (int i = 0; i < peerChannels.Count; i++)
        {
            int idx = _mesh.IndexOf(peerChannels[i]);
            map[i] = idx < 0 ? -1 : idx + 1;
        }
        return map;
    }
}

/// <summary>
/// 讲话段检测：距该说话者上一包超过 gap 视为新一轮讲话（与 SLDataAPI 一致取 0.8s），
/// 新一轮强制重发说话者信息帧，避免对端在静默清理后拿不到元数据。
/// </summary>
public sealed class BurstTracker<TKey> where TKey : notnull
{
    private readonly float _gap;
    private readonly Dictionary<TKey, float> _last = new();

    public BurstTracker(float gapSeconds = 0.8f) => _gap = gapSeconds;

    public bool Mark(TKey key, float now)
    {
        bool newBurst = !_last.TryGetValue(key, out float t) || now - t > _gap;
        _last[key] = now;
        return newBurst;
    }

    public bool TryGetLast(TKey key, out float t) => _last.TryGetValue(key, out t);

    public void Forget(TKey key) => _last.Remove(key);

    public void Clear() => _last.Clear();

    /// <summary>返回超过 idleSeconds 未出声的键（调用方据此做清理）。</summary>
    public List<TKey> CollectIdle(float now, float idleSeconds)
    {
        var r = new List<TKey>();
        foreach (var kv in _last)
            if (now - kv.Value > idleSeconds) r.Add(kv.Key);
        return r;
    }
}

/// <summary>令牌桶限速（按对端限制入站语音包速率）。</summary>
public sealed class TokenBucket
{
    private readonly double _capacity;
    private readonly double _refillPerSecond;
    private double _tokens;
    private double _lastAt;

    public TokenBucket(double capacity, double refillPerSecond, double now)
    {
        _capacity = capacity;
        _refillPerSecond = refillPerSecond;
        _tokens = capacity;
        _lastAt = now;
    }

    public bool TryTake(double now)
    {
        if (now > _lastAt)
        {
            _tokens = Math.Min(_capacity, _tokens + (now - _lastAt) * _refillPerSecond);
            _lastAt = now;
        }
        if (_tokens < 1) return false;
        _tokens -= 1;
        return true;
    }
}

/// <summary>拨号重连指数退避：2s、4s、8s … 上限 60s；连上后 Reset。</summary>
public sealed class ReconnectBackoff
{
    private int _failures;
    public const double MaxDelaySeconds = 60;

    public double NextDelaySeconds()
    {
        double d = Math.Min(MaxDelaySeconds, Math.Pow(2, Math.Min(_failures + 1, 6)));
        _failures++;
        return d;
    }

    public void Reset() => _failures = 0;

    public int Failures => _failures;
}
