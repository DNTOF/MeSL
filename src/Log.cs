// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/SLDataAPI)
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/MeSL)
// 改编自 SLDataAPI Log.cs；限流日志改编自 SLDataAPI Voice/VoiceService.cs（RateLimitedWarn）。

using System.Collections.Generic;
using LabApi.Features.Console;

namespace MeSL;

/// <summary>日志门面：统一走 LabAPI Logger，Debug 由 Config.Debug 门控；高频路径用 RateLimited 防刷屏。</summary>
public static class Log
{
    private static readonly Dictionary<string, float> LastByKey = new();

    public static void Debug(string message) =>
        Logger.Debug(message, Plugin.Instance?.Config?.Debug ?? false);

    public static void Info(string message) => Logger.Info(message);

    public static void Warn(string message) => Logger.Warn(message);

    public static void Error(string message) => Logger.Error(message);

    /// <summary>同一 key 每 interval 秒最多打一条 Warn（主线程调用）。</summary>
    public static void RateLimitedWarn(string key, string message, float interval = 10f)
    {
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (LastByKey.TryGetValue(key, out float last) && now - last < interval)
            return;
        LastByKey[key] = now;
        Logger.Warn(message);
    }
}
