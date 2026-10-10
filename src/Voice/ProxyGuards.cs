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
///   - RoundSummary 统计存活阵营时会把 Dummy 算进去——排除代理后重算，避免远端说话者让本服回合结束不了。
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
