// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/MeSL)

using System.Collections.Generic;
using System.ComponentModel;

namespace MeSL;

/// <summary>
/// 插件配置。LabAPI 在 Enable() 前自动加载：
///   &lt;LabAPI&gt;/configs/&lt;端口或 global&gt;/MeSL/config.yml（键名 snake_case）
/// 注意：YAML 任一值格式错误时 LabAPI 会把整个文件静默回退为默认值，Enable 会做完整性自检并报出行号。
/// </summary>
public class Config
{
    public bool Debug { get; set; } = false;

    [Description("本服在 mesh 中的唯一 ID：1..32 位小写字母/数字/'-'。留空 = 不启用 mesh。")]
    public string ServerId { get; set; } = "";

    [Description("节点间链路监听地址与端口（TCP）。只应对受信节点开放，建议走 WireGuard 等加密隧道。")]
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int ListenPort { get; set; } = 7790;

    [Description("mesh 频道名（全网按名称匹配，最多 16 个）。按键循环：本地 → 频道1 → … → 本地。")]
    public List<string> Channels { get; set; } = new() { "mesh" };

    [Description("受信节点列表。每对节点使用同一把预共享密钥（≥32 字符），两端都要配置对方。")]
    public List<PeerConfig> Peers { get; set; } = new();

    [Description("false（默认）= 雷达门：人类须正在用对讲机发言才进入远程频道；true = 关闭雷达门，近距离发言也转发。")]
    public bool ForceRadio { get; set; } = false;

    [Description("同时存在的远程代理说话者上限（全服 / 每个节点）。")]
    public int MaxRemoteSpeakers { get; set; } = 16;
    public int MaxRemoteSpeakersPerPeer { get; set; } = 8;

    [Description("远程说话者静默多少秒后回收其代理。")]
    public float ProxyIdleSeconds { get; set; } = 6f;

    [Description("切换频道按键的 SSS 设置 ID（与其他插件冲突时修改）。")]
    public int KeybindSettingId { get; set; } = 7790001;

    [Description("切换频道后是否显示提示。")]
    public bool ShowChannelHint { get; set; } = true;
}

public class PeerConfig
{
    public string ServerId { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 7790;

    [Description("与该节点共享的密钥（≥32 字符，不含空白/引号）。只写在本地配置文件，切勿提交到仓库。")]
    public string Secret { get; set; } = "";
}
