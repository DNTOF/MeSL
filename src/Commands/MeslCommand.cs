// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/MeSL)

using System;
using System.Text;
using CommandSystem;
using MeSL.Mesh;
using MeSL.Voice;

namespace MeSL.Commands;

/// <summary>服务器控制台 `mesl`：链路与代理状态。只注册到控制台（输出含节点 IP，不对 RA 开放）。</summary>
[CommandHandler(typeof(GameConsoleCommandHandler))]
public class MeslCommand : ICommand
{
    public string Command => "mesl";
    public string[] Aliases => Array.Empty<string>();
    public string Description => "MeSL mesh 语音链路状态";

    public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
    {
        if (!MeshLinkService.IsRunning)
        {
            response = "MeSL mesh 未启用（检查 server_id / channels / peers 配置）";
            return true;
        }
        var sb = new StringBuilder();
        sb.AppendLine($"server_id={MeshLinkService.LocalId}");
        foreach (string line in MeshLinkService.DescribeLinks())
            sb.AppendLine("  " + line);
        sb.Append($"转发 {VoiceCapture.ForwardedPackets} 包 / 注入 {ProxySpeakerRegistry.InjectedPackets} 包 / 代理说话者 {ProxySpeakerRegistry.Count}");
        response = sb.ToString();
        return true;
    }
}
