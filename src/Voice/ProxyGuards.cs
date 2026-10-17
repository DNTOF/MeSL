// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/MeSL)

using System;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Arguments.ServerEvents;
using PlayerRoles;

namespace MeSL.Voice;

/// <summary>
/// 代理说话者（Dummy）对原版玩法的副作用隔离：
///   - 复活波次会挑选旁观者 Dummy、管理员命令也可能改动它——只允许 MeSL 自己（RemoteAdmin 原因）改身份；
///   - RoundSummary 统计存活阵营时会把 Dummy 算进去——排除代理后重算，避免远端说话者让本服回合结束不了；
///   - 代理对客户端隐身，既不出现在地图上，也让其近距离声源听不到（远端语音只走对讲机）。
/// </summary>
public static class ProxyGuards
{
    public static void OnChangingRole(PlayerChangingRoleEventArgs ev)
    {
        try
        {
            if (ev.Player == null || !ProxySpeakerRegistry.IsProxy(ev.Player.NetworkId)) return;
            if (ev.ChangeReason != RoleChangeReason.RemoteAdmin)
                ev.IsAllowed = false;
        }
        catch (Exception ex)
        {
            Log.RateLimitedWarn("guard-role", $"[MeSL] 代理身份守卫异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 代理对所有观察者隐身：客户端会把隐身玩家放到 FpcMotor.InvisiblePosition（y=6000），
    /// 于是原版收到 Radio 消息时顺带写入的近距离 3D 播放器远在听距之外，远端语音只剩对讲机声道。
    /// 只影响位置同步；服务端 transform 不变，对讲机 TransmitterPositionMessage 仍按站点上报。
    /// 每 tick × 每对玩家调用，必须保持廉价。
    /// </summary>
    public static void OnValidatedVisibility(PlayerValidatedVisibilityEventArgs ev)
    {
        if (ev.IsVisible && ev.Target != null && ProxySpeakerRegistry.IsProxy(ev.Target.NetworkId))
            ev.IsVisible = false;
    }

    public static void OnRoundEndingConditionsCheck(RoundEndingConditionsCheckEventArgs ev)
    {
        try
        {
            if (ev.CanEnd || ProxySpeakerRegistry.Count == 0) return;

            int foundation = 0, insurgency = 0, scps = 0, flamingos = 0;
            foreach (ReferenceHub hub in ReferenceHub.AllHubs)
            {
                if (ProxySpeakerRegistry.IsProxy(hub.netId)) continue;
                switch (hub.GetTeam())
                {
                    case Team.FoundationForces:
                    case Team.Scientists:
                        foundation++;
                        break;
                    case Team.ChaosInsurgency:
                    case Team.ClassD:
                        insurgency++;
                        break;
                    case Team.SCPs:
                        scps++;
                        break;
                    case Team.Flamingos:
                        flamingos++;
                        break;
                }
            }
            int alive = (foundation > 0 ? 1 : 0) + (insurgency > 0 ? 1 : 0) + (scps > 0 ? 1 : 0) + (flamingos > 0 ? 1 : 0);
            if (RoundSummary.singleton != null && RoundSummary.singleton.ExtraTargets > 0) alive++;
            if (alive <= 1) ev.CanEnd = true;
        }
        catch (Exception ex)
        {
            Log.RateLimitedWarn("guard-round", $"[MeSL] 回合结束判定守卫异常: {ex.Message}");
        }
    }
}
