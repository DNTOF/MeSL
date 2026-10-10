// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/MeSL)

using System;
using LabApi.Events.Arguments.PlayerEvents;
using MeSL.Core;
using MeSL.Game;
using Player = LabApi.Features.Wrappers.Player;

namespace MeSL.Voice;

/// <summary>
/// PlayerEvents.ReceivingVoiceMessage：原版对每个接收者做完 ValidateReceive 之后触发，
/// ev.Message.Channel 已是该接收者实际会收到的频道。全部在服务端裁决，从不依赖客户端静音。
/// </summary>
public static class ReceiveFilter
{
    public static void OnReceivingVoiceMessage(PlayerReceivingVoiceMessageEventArgs ev)
    {
        try
        {
            if (!ev.IsAllowed) return;
            Player? sender = ev.Sender;
            Player? receiver = ev.Player;
            if (sender == null || receiver == null) return;

            // 1) 远端代理说话者：只投递给同一 mesh 频道 + 同一阵营分组的本服真人
            if (ProxySpeakerRegistry.TryGetProxy(sender.NetworkId, out var proxy))
            {
                if (receiver.IsDummy || !receiver.IsPlayer)
                {
                    ev.IsAllowed = false;
                    return;
                }
                MeshGroup rg = GameMapping.GroupOf(receiver);
                int rc = ChannelSelector.Get(receiver.NetworkId);
                if (!MeshPolicy.AllowRemoteReceive(proxy.Group, proxy.LocalChannel, rg, rc))
                    ev.IsAllowed = false;
                return;
            }

            // 2) 本服说话者切到远程频道：其本地近距离（及随之播放的对讲机/旁观者）语音不投递给本服任何人
            if (ChannelSelector.IsRemote(sender.NetworkId) &&
                MeshPolicy.BlockLocalDelivery(true, GameMapping.KindOf(ev.Message.Channel)))
            {
                ev.IsAllowed = false;
            }
        }
        catch (Exception ex)
        {
            Log.RateLimitedWarn("receive", $"[MeSL] 语音接收过滤异常: {ex.Message}");
        }
    }
}
