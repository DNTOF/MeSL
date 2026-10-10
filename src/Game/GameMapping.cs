// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/MeSL)

using MeSL.Core;
using PlayerRoles;
using VoiceChat;
using Player = LabApi.Features.Wrappers.Player;

namespace MeSL.Game;

/// <summary>游戏枚举 → MeSL 线路值的唯一映射处。</summary>
public static class GameMapping
{
    public static MeshGroup GroupOf(Player player)
    {
        try { return GroupOf(player.Role); }
        catch { return MeshGroup.None; }
    }

    /// <summary>
    /// A 组 = D 级 + 混沌；B 组 = 科学家 + 九尾狐（含设施警卫）；旁观者单独一组。
    /// SCP、教程角色、Overwatch、Filmmaker 等不参与 mesh。
    /// </summary>
    public static MeshGroup GroupOf(RoleTypeId role)
    {
        if (role == RoleTypeId.Spectator) return MeshGroup.Spectator;
        switch (role.GetTeam())
        {
            case Team.ClassD:
            case Team.ChaosInsurgency:
                return MeshGroup.GroupA;
            case Team.Scientists:
            case Team.FoundationForces:
                return MeshGroup.GroupB;
            default:
                return MeshGroup.None;
        }
    }

    public static CaptureKind KindOf(VoiceChatChannel channel) => channel switch
    {
        VoiceChatChannel.Proximity => CaptureKind.Proximity,
        VoiceChatChannel.Radio => CaptureKind.Radio,
        VoiceChatChannel.Spectator => CaptureKind.Spectator,
        _ => CaptureKind.Other,
    };

    public static VoiceChatChannel ChannelOf(CaptureKind kind) => kind switch
    {
        CaptureKind.Proximity => VoiceChatChannel.Proximity,
        CaptureKind.Radio => VoiceChatChannel.Radio,
        CaptureKind.Spectator => VoiceChatChannel.Spectator,
        _ => VoiceChatChannel.None,
    };

    public static string GroupLabel(MeshGroup g) => g switch
    {
        MeshGroup.GroupA => "A（D级/混沌）",
        MeshGroup.GroupB => "B（科学家/九尾狐）",
        MeshGroup.Spectator => "旁观者",
        _ => "不可用",
    };
}
