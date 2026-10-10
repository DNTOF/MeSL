// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/MeSL)

using System;
using LabApi.Features.Wrappers;
using MapGeneration;
using MeSL.Core;
using UnityEngine;
using Player = LabApi.Features.Wrappers.Player;

namespace MeSL.Game;

/// <summary>
/// 采集站点 ↔ 本服世界坐标（MeSL-Protocol-v0 §5）。
/// 两服地图种子通常不同，世界坐标不可直接复用；优先按「同名同形同区的房间 + 房间局部坐标」还原，
/// 同类房间有多个时取还原点离发送方世界坐标最近的一个；没有同类房间才退回世界坐标。
/// </summary>
public static class SiteResolver
{
    public const short NoRoom = -1;

    public static MeshSite? Capture(Player player)
    {
        try
        {
            if (player.Role == PlayerRoles.RoleTypeId.Spectator)
                return null;
            Vector3 world = player.Position;
            var site = new MeshSite
            {
                RoomName = NoRoom,
                WorldX = world.x, WorldY = world.y, WorldZ = world.z,
            };
            Room? room = Room.GetRoomAtPosition(world);
            if (room != null)
            {
                Vector3 local = room.Transform.InverseTransformPoint(world);
                site.RoomName = (short)room.Name;
                site.RoomShape = (byte)room.Shape;
                site.Zone = (byte)room.Zone;
                site.LocalX = local.x; site.LocalY = local.y; site.LocalZ = local.z;
            }
            return site;
        }
        catch (Exception ex)
        {
            Log.RateLimitedWarn("site-capture", $"[MeSL] 站点采集失败: {ex.Message}");
            return null;
        }
    }

    public static bool TryResolve(MeshSite site, out Vector3 position)
    {
        var world = new Vector3(site.WorldX, site.WorldY, site.WorldZ);
        position = world;
        if (site.RoomName == NoRoom)
            return Room.GetRoomAtPosition(world) != null;

        var local = new Vector3(site.LocalX, site.LocalY, site.LocalZ);
        float best = float.MaxValue;
        bool found = false;
        foreach (Room room in Room.List)
        {
            if (room == null || room.Base == null) continue;
            if ((short)room.Name != site.RoomName || (byte)room.Shape != site.RoomShape || (byte)room.Zone != site.Zone)
                continue;
            Vector3 candidate = room.Transform.TransformPoint(local);
            float d = (candidate - world).sqrMagnitude;
            if (d < best)
            {
                best = d;
                position = candidate;
                found = true;
            }
        }
        if (found) return true;
        return Room.GetRoomAtPosition(world) != null;
    }
}
