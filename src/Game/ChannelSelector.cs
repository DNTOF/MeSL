// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/MeSL)

using System;
using System.Collections.Generic;
using System.Linq;
using MeSL.Core;
using UnityEngine;
using UserSettings.ServerSpecific;
using Player = LabApi.Features.Wrappers.Player;

namespace MeSL.Game;

/// <summary>
/// 每名玩家当前所选频道（0 = 本地原版语音）。一个 SSS 按键循环切换；说话仍是原版按住说话。
/// 状态按 netId 存（不用 ReferenceHub 做键：玩家断开后 Hub 被销毁，GetHashCode 会抛 NRE）。
/// </summary>
public static class ChannelSelector
{
    private static readonly Dictionary<uint, int> Selected = new();
    private static ChannelTable _table = new(Array.Empty<string>());
    private static ServerSpecificSettingBase[] _ownSettings = Array.Empty<ServerSpecificSettingBase>();
    private static int _keybindId;
    private static bool _showHint;
    private static bool _active;

    public static ChannelTable Table => _table;

    public static void Init(ChannelTable table, int keybindId, bool showHint)
    {
        _table = table;
        _keybindId = keybindId;
        _showHint = showHint;
        Selected.Clear();

        _ownSettings = new ServerSpecificSettingBase[]
        {
            new SSGroupHeader("MeSL 跨服语音 / Mesh voice"),
            new SSKeybindSetting(keybindId, "切换语音频道 / Switch voice channel", KeyCode.None,
                preventInteractionOnGui: true, allowSpectatorTrigger: true,
                hint: "本地 → mesh 频道 → 本地；说话仍用原版按键 / Local → mesh channels → Local"),
        };

        // 追加而不是覆盖：DefinedSettings 是全局单例，其他插件的设置项必须保留
        var current = ServerSpecificSettingsSync.DefinedSettings ?? Array.Empty<ServerSpecificSettingBase>();
        if (current.Any(s => s is SSKeybindSetting k && k.SettingId == keybindId))
            Log.Warn($"[MeSL] SSS 设置 ID {keybindId} 已被其他插件占用，请修改 keybind_setting_id");
        ServerSpecificSettingsSync.DefinedSettings = current.Concat(_ownSettings).ToArray();
        ServerSpecificSettingsSync.SendToAll();
        ServerSpecificSettingsSync.ServerOnSettingValueReceived += OnSettingValueReceived;
        _active = true;
    }

    public static void Dispose()
    {
        if (!_active) return;
        _active = false;
        ServerSpecificSettingsSync.ServerOnSettingValueReceived -= OnSettingValueReceived;
        try
        {
            var current = ServerSpecificSettingsSync.DefinedSettings;
            if (current != null)
            {
                ServerSpecificSettingsSync.DefinedSettings = current.Where(s => Array.IndexOf(_ownSettings, s) < 0).ToArray();
                ServerSpecificSettingsSync.SendToAll();
            }
        }
        catch { /* 停服时网络层可能已不可用 */ }
        Selected.Clear();
    }

    public static int Get(uint netId) => Selected.TryGetValue(netId, out int c) ? c : ChannelTable.LocalIndex;

    public static bool IsRemote(uint netId) => Get(netId) != ChannelTable.LocalIndex;

    public static void Forget(uint netId) => Selected.Remove(netId);

    /// <summary>身份变化后若新身份不可参与 mesh（如变成 SCP），强制回到本地频道。</summary>
    public static void OnRoleChanged(Player player)
    {
        if (!IsRemote(player.NetworkId)) return;
        if (GameMapping.GroupOf(player) != MeshGroup.None) return;
        Selected.Remove(player.NetworkId);
        Hint(player, "已回到本地语音（当前身份不可使用跨服频道）");
    }

    private static void OnSettingValueReceived(ReferenceHub hub, ServerSpecificSettingBase setting)
    {
        try
        {
            if (setting is not SSKeybindSetting key || key.SettingId != _keybindId || !key.SyncIsPressed)
                return;
            Player? player = Player.Get(hub);
            if (player == null || player.IsDummy || !player.IsPlayer) return;
            Cycle(player);
        }
        catch (Exception ex)
        {
            Log.RateLimitedWarn("sss", $"[MeSL] 处理频道切换按键异常: {ex.Message}");
        }
    }

    private static void Cycle(Player player)
    {
        uint id = player.NetworkId;
        MeshGroup group = GameMapping.GroupOf(player);
        if (group == MeshGroup.None)
        {
            Selected.Remove(id);
            Hint(player, "当前身份不可使用跨服频道");
            return;
        }

        int next = _table.Next(Get(id));
        if (next == ChannelTable.LocalIndex) Selected.Remove(id);
        else Selected[id] = next;

        if (next == ChannelTable.LocalIndex)
            Hint(player, "频道：本地语音");
        else
            Hint(player, $"频道：{_table.NameOf(next)}（跨服 · 阵营 {GameMapping.GroupLabel(group)}）" +
                         (group == MeshGroup.Spectator || (Plugin.Instance?.Config.ForceRadio ?? false) ? "" : "\n需使用对讲机发言"));
    }

    private static void Hint(Player player, string text)
    {
        if (!_showHint) return;
        try { player.SendHint("[MeSL] " + text, 3f); }
        catch { /* 玩家瞬时断开等 */ }
    }
}
