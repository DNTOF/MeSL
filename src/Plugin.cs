// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/SLDataAPI)
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/MeSL)
// 生命周期与配置自检改编自 SLDataAPI Plugin.cs（事件链保护、ValidateConfigFileIntegrity）。

using System;
using System.Collections.Generic;
using System.IO;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Loader;
using LabApi.Loader.Features.Yaml;
using MEC;
using MeSL.Core;
using MeSL.Game;
using MeSL.Mesh;
using MeSL.Voice;

namespace MeSL;

public class Plugin : LabApi.Loader.Features.Plugins.Plugin<Config>
{
    public static Plugin? Instance;
    private CoroutineHandle _loop;
    private bool _meshStarted;

    public override string Name => "MeSL";
    public override string Description => "P2P mesh voice channel bridge between trusted SCP:SL servers (MeSL-Protocol-v0)";
    public override string Author => "DNT_OF";
    public override Version Version => new(0, 1, 0);
    public override Version RequiredApiVersion => new(1, 1, 7);

    public override void Enable()
    {
        Instance = this;
        ValidateConfigFileIntegrity();

        if (string.IsNullOrWhiteSpace(Config.ServerId))
        {
            Log.Warn("[MeSL] server_id 为空，mesh 未启用（填写 server_id 与 peers 后重启）");
            return;
        }

        var table = new ChannelTable(Config.Channels ?? new List<string>());
        if (table.MeshChannels.Count == 0)
        {
            Log.Error("[MeSL] channels 中没有合法频道名（小写字母/数字/'-'，≤32），mesh 未启用");
            return;
        }

        if (!MeshLinkService.Start(Config, table))
            return;
        _meshStarted = true;

        bool reinject = ProxySpeakerRegistry.Init(Config.MaxRemoteSpeakers, Config.MaxRemoteSpeakersPerPeer, Config.ProxyIdleSeconds,
            Config.RemoteRadioRange);
        VoiceCapture.Configure(Config.ForceRadio);
        ChannelSelector.Init(table, Config.KeybindSettingId, Config.ShowChannelHint);

        LabApi.Events.Handlers.PlayerEvents.SendingVoiceMessage += VoiceCapture.OnSendingVoiceMessage;
        LabApi.Events.Handlers.PlayerEvents.ReceivingVoiceMessage += ReceiveFilter.OnReceivingVoiceMessage;
        LabApi.Events.Handlers.PlayerEvents.ChangingRole += ProxyGuards.OnChangingRole;
        LabApi.Events.Handlers.PlayerEvents.ValidatedVisibility += ProxyGuards.OnValidatedVisibility;
        LabApi.Events.Handlers.PlayerEvents.ChangedRole += OnChangedRole;
        LabApi.Events.Handlers.PlayerEvents.Left += OnLeft;
        LabApi.Events.Handlers.ServerEvents.WaitingForPlayers += OnWaitingForPlayers;
        LabApi.Events.Handlers.ServerEvents.RoundEndingConditionsCheck += ProxyGuards.OnRoundEndingConditionsCheck;

        _loop = Timing.RunCoroutine(Loop());

        Log.Info($"[MeSL] v{Version} 已启用：雷达门={(Config.ForceRadio ? "关闭（force_radio）" : "开启")}，" +
                 $"远端语音重注入={(reinject ? $"仅对讲机（{Config.RemoteRadioRange}）" : "不可用")}，代理上限 {Config.MaxRemoteSpeakers}/{Config.MaxRemoteSpeakersPerPeer}");
    }

    public override void Disable()
    {
        if (_meshStarted)
        {
            LabApi.Events.Handlers.PlayerEvents.SendingVoiceMessage -= VoiceCapture.OnSendingVoiceMessage;
            LabApi.Events.Handlers.PlayerEvents.ReceivingVoiceMessage -= ReceiveFilter.OnReceivingVoiceMessage;
            LabApi.Events.Handlers.PlayerEvents.ChangingRole -= ProxyGuards.OnChangingRole;
            LabApi.Events.Handlers.PlayerEvents.ValidatedVisibility -= ProxyGuards.OnValidatedVisibility;
            LabApi.Events.Handlers.PlayerEvents.ChangedRole -= OnChangedRole;
            LabApi.Events.Handlers.PlayerEvents.Left -= OnLeft;
            LabApi.Events.Handlers.ServerEvents.WaitingForPlayers -= OnWaitingForPlayers;
            LabApi.Events.Handlers.ServerEvents.RoundEndingConditionsCheck -= ProxyGuards.OnRoundEndingConditionsCheck;

            if (_loop.IsRunning) Timing.KillCoroutines(_loop);
            ProxySpeakerRegistry.ReleaseAll();
            ChannelSelector.Dispose();
            VoiceCapture.Reset();
            MeshLinkService.Stop();
            _meshStarted = false;
        }
        Instance = null;
    }

    private IEnumerator<float> Loop()
    {
        while (true)
        {
            // 任何异常都不能逃出协程（MEC 会直接杀掉抛异常的协程，整个 mesh 停摆）
            try
            {
                MeshLinkService.Tick();
                ProxySpeakerRegistry.Cleanup();
                VoiceCapture.Cleanup(Config.ProxyIdleSeconds);
            }
            catch (Exception ex)
            {
                Log.RateLimitedWarn("loop", $"[MeSL] 主循环异常: {ex.Message}");
            }
            yield return Timing.WaitForOneFrame;
        }
    }

    // LabAPI 事件分发不做 try-catch：处理器里的异常会传播回原版流程，一律兜底
    private void OnChangedRole(PlayerChangedRoleEventArgs ev)
    {
        try { if (ev.Player != null && !ev.Player.IsDummy) ChannelSelector.OnRoleChanged(ev.Player); }
        catch (Exception ex) { Log.RateLimitedWarn("changedrole", $"[MeSL] ChangedRole 处理异常: {ex.Message}"); }
    }

    private void OnLeft(PlayerLeftEventArgs ev)
    {
        try
        {
            if (ev.Player == null) return;
            uint netId = ev.Player.NetworkId;
            ChannelSelector.Forget(netId);
            VoiceCapture.OnPlayerLeft(netId);
        }
        catch (Exception ex) { Log.RateLimitedWarn("left", $"[MeSL] Left 处理异常: {ex.Message}"); }
    }

    /// <summary>回合重启会重载场景、销毁所有 Dummy：丢弃代理记录与发送端状态。</summary>
    private void OnWaitingForPlayers()
    {
        try
        {
            ProxySpeakerRegistry.ForgetAll();
            VoiceCapture.Reset();
        }
        catch (Exception ex) { Log.RateLimitedWarn("wfp", $"[MeSL] WaitingForPlayers 处理异常: {ex.Message}"); }
    }

    /// <summary>
    /// LabAPI 在任一值格式错误时会把整个配置文件静默回退为默认值，这里用同款反序列化器重读一遍并报出精确错误。
    /// </summary>
    private void ValidateConfigFileIntegrity()
    {
        try
        {
            string? path = ConfigurationLoader.GetConfigPath(this, ConfigFileName);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try
            {
                YamlConfigParser.Deserializer.Deserialize<Config>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Log.Error("[MeSL] 配置文件解析失败，LabAPI 已静默回退为全部默认值（mesh 不会启用）。" +
                          $"请修正后重启：{ex.Message.Replace("\n", " ")}");
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[MeSL] 配置自检异常（忽略）: {ex.Message}");
        }
    }
}
