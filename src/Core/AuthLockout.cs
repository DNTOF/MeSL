// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/SLDataAPI)
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/MeSL)
// 改编自 SLDataAPI Control/ControlAuth.cs（IsLocked / RegisterFailure / ResetFailures / SecureEquals）。

using System;
using System.Collections.Generic;

namespace MeSL.Core;

/// <summary>
/// 按来源 IP 的鉴权失败锁定：窗口内失败达到阈值后锁定一段时间，锁定期间不再比较凭据。
/// 与 SLDataAPI 不同，MeSL 的握手全部在主线程协程里处理，因此这里不需要并发字典，时钟可注入便于测试。
/// </summary>
public sealed class AuthLockout
{
    private readonly int _maxFailures;
    private readonly TimeSpan _window;
    private readonly TimeSpan _lockDuration;
    private readonly Dictionary<string, (int Count, DateTime WindowStart)> _table = new(StringComparer.Ordinal);

    public AuthLockout(int maxFailures = 10, TimeSpan? window = null, TimeSpan? lockDuration = null)
    {
        _maxFailures = maxFailures;
        _window = window ?? TimeSpan.FromMinutes(5);
        _lockDuration = lockDuration ?? TimeSpan.FromMinutes(5);
    }

    public bool IsLocked(string ip, DateTime nowUtc, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (!_table.TryGetValue(ip, out var entry))
            return false;
        if (entry.Count < _maxFailures)
            return false;

        DateTime lockedUntil = entry.WindowStart + _window + _lockDuration;
        if (nowUtc >= lockedUntil)
        {
            _table.Remove(ip);
            return false;
        }
        remaining = lockedUntil - nowUtc;
        return true;
    }

    public void RegisterFailure(string ip, DateTime nowUtc)
    {
        if (_table.TryGetValue(ip, out var old) && nowUtc - old.WindowStart <= _window)
            _table[ip] = (old.Count + 1, old.WindowStart);
        else
            _table[ip] = (1, nowUtc);
    }

    public void Reset(string ip) => _table.Remove(ip);

    /// <summary>清理过期条目（Tick 低频调用，防止大量伪造来源撑大表）。</summary>
    public void Sweep(DateTime nowUtc)
    {
        List<string>? stale = null;
        foreach (var kv in _table)
        {
            if (nowUtc - kv.Value.WindowStart > _window + _lockDuration)
                (stale ??= new List<string>()).Add(kv.Key);
        }
        if (stale == null) return;
        foreach (var k in stale) _table.Remove(k);
    }

    public int Count => _table.Count;

    /// <summary>常量时间字符串比较，避免按响应耗时逐字节猜出凭据。</summary>
    public static bool SecureEquals(string a, string b)
    {
        a ??= string.Empty;
        b ??= string.Empty;
        int diff = a.Length ^ b.Length;
        int max = Math.Max(a.Length, b.Length);
        for (int i = 0; i < max; i++)
        {
            char ca = i < a.Length ? a[i] : '\0';
            char cb = i < b.Length ? b[i] : '\0';
            diff |= ca ^ cb;
        }
        return diff == 0;
    }
}
