// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/SLDataAPI)
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/MeSL)
// 改编自 SLDataAPI Voice/VoiceService.cs（HandleIncoming：netId 键、内容哈希去重、讲话段检测、说话者帧去重）。

using System;
using System.Collections.Generic;
using LabApi.Events.Arguments.PlayerEvents;
using MeSL.Core;
using MeSL.Game;
using MeSL.Mesh;
using VoiceChat.Networking;
using Player = LabApi.Features.Wrappers.Player;

namespace MeSL.Voice;

/// <summary>
/// 发送端：PlayerEvents.SendingVoiceMessage（VoiceTransceiver.ServerReceiveMessage 内，ValidateSend 之后）。
/// 与 SLDataAPI 的区别：
///   - 不解码，Opus 字节原样上线路；
///   - 频道取 VoiceModule.CurrentChannel（服务端已校验），不信任客户端在 msg.Channel 里声称的值；
///   - 代理说话者（远端重注入）的包一律不转发——mesh 只做一跳，杜绝环路。
/// </summary>
public static class VoiceCapture
{
    private static readonly DuplicateFilter<uint> Dup = new();
    private static readonly BurstTracker<uint> Bursts = new();
    private static readonly Dictionary<uint, uint> Seq = new();
    private static readonly Dictionary<uint, string> SpeakerKeys = new();
    private static bool _forceRadio;

    public static long ForwardedPackets { get; private set; }

    public static void Configure(bool forceRadio) => _forceRadio = forceRadio;

    public static void Reset()
    {
        Dup.Clear();
        Bursts.Clear();
        Seq.Clear();
        SpeakerKeys.Clear();
        LastNeedRadioHint.Clear();
    }

    public static void OnSendingVoiceMessage(PlayerSendingVoiceMessageEventArgs ev)
    {
        try
        {
            if (!ev.IsAllowed) return;
            Handle(ev.Player, ev.Message);
        }
        catch (Exception ex)
        {
            // LabAPI 事件分发无 try-catch，异常会打断原版语音链路
            Log.RateLimitedWarn("capture", $"[MeSL] 语音采集异常: {ex.Message}");
        }
    }

    private static void Handle(Player? player, VoiceMessage msg)
    {
        if (player == null || !MeshLinkService.HasOpenLinks) return;

        uint netId = player.NetworkId;
        if (ProxySpeakerRegistry.IsProxy(netId)) return;

        int channel = ChannelSelector.Get(netId);
        MeshGroup group = GameMapping.GroupOf(player);
        CaptureKind kind = GameMapping.KindOf(player.VoiceChannel);

        SendVerdict verdict = MeshPolicy.EvaluateSend(channel != ChannelTable.LocalIndex, group, kind, _forceRadio);
        if (verdict == SendVerdict.NeedRadio)
        {
            NeedRadioHint(player);
            return;
        }
        if (verdict != SendVerdict.Forward) return;

        if (msg.Data == null || msg.DataLength <= 0 || msg.DataLength > MeshCodec.MaxOpusBytes || msg.DataLength > msg.Data.Length)
            return;
        if (Dup.IsDuplicate(netId, msg.Data, msg.DataLength)) return;

        float now = UnityEngine.Time.time;
        bool newBurst = Bursts.Mark(netId, now);
        sbyte role = unchecked((sbyte)player.Role);

        // 说话者信息帧：新一轮讲话或身份/昵称变化时才发（每包都发会和语音帧抢带宽）
        string nick = player.Nickname ?? "?";
        string key = $"{role}|{(byte)group}|{nick}";
        if (newBurst || !SpeakerKeys.TryGetValue(netId, out string? lastKey) || lastKey != key)
        {
            SpeakerKeys[netId] = key;
            MeshLinkService.Broadcast(channel, MeshCodec.EncodeSpeaker(new MeshSpeakerInfo
            {
                OriginNetId = netId,
                Role = role,
                Group = group,
                Nickname = nick,
            }));
        }

        Seq.TryGetValue(netId, out uint seq);
        Seq[netId] = unchecked(seq + 1);

        byte[] opus = new byte[msg.DataLength];
        Buffer.BlockCopy(msg.Data, 0, opus, 0, msg.DataLength);

        var frame = new MeshVoiceFrame
        {
            ChannelIndex = ChannelSelector.Table.WireIndexOf(channel),
            VoiceChannel = (byte)player.VoiceChannel,
            Group = group,
            OriginNetId = netId,
            Seq = seq,
            NewBurst = newBurst,
            Role = role,
            Site = SiteResolver.Capture(player),
            Opus = opus,
            OpusLength = opus.Length,
        };
        MeshLinkService.Broadcast(channel, MeshCodec.EncodeVoice(frame));
        ForwardedPackets++;
    }

    /// <summary>Tick 内调用：静默超过 idleSeconds 的说话者通知对端立即回收代理。</summary>
    public static void Cleanup(float idleSeconds)
    {
        float now = UnityEngine.Time.time;
        foreach (uint netId in Bursts.CollectIdle(now, idleSeconds))
        {
            Bursts.Forget(netId);
            Dup.Forget(netId);
            Seq.Remove(netId);
            SpeakerKeys.Remove(netId);
            MeshLinkService.BroadcastAll(MeshCodec.EncodeSpeakerGone(netId));
        }
    }

    public static void OnPlayerLeft(uint netId)
    {
        LastNeedRadioHint.Remove(netId);
        if (!Bursts.TryGetLast(netId, out _)) return;
        Bursts.Forget(netId);
        Dup.Forget(netId);
        Seq.Remove(netId);
        SpeakerKeys.Remove(netId);
        MeshLinkService.BroadcastAll(MeshCodec.EncodeSpeakerGone(netId));
    }

    private static readonly Dictionary<uint, float> LastNeedRadioHint = new();

    private static void NeedRadioHint(Player player)
    {
        float now = UnityEngine.Time.time;
        if (LastNeedRadioHint.TryGetValue(player.NetworkId, out float t) && now - t < 5f) return;
        LastNeedRadioHint[player.NetworkId] = now;
        try { player.SendHint("[MeSL] 跨服频道需使用对讲机发言（本地近距离语音已屏蔽）", 3f); }
        catch { /* 忽略 */ }
    }
}
