// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/SLDataAPI)
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/MeSL)
// 改编自 SLDataAPI Voice/VoiceService.cs（FnvHash 与按说话者的重复包过滤）。

using System.Collections.Generic;

namespace MeSL.Core;

/// <summary>FNV-1a 64 位哈希（判重用，非密码学用途）。</summary>
public static class Fnv
{
    public static ulong Hash(byte[] data, int len)
    {
        ulong h = 14695981039346656037UL;
        int n = len < data.Length ? len : data.Length;
        for (int i = 0; i < n; i++)
        {
            h ^= data[i];
            h *= 1099511628211UL;
        }
        return h;
    }
}

/// <summary>
/// 按说话者键过滤「与上一包内容完全相同」的重复包。
/// 只能按内容哈希判重：10ms Opus 帧约 100 包/秒，同一帧内常有两个长度相同的不同包，
/// 按 (帧号, 频道, 长度) 判重会误删一半音频。
/// </summary>
public sealed class DuplicateFilter<TKey> where TKey : notnull
{
    private readonly Dictionary<TKey, ulong> _last = new();

    /// <summary>是重复包返回 true（调用方丢弃）；否则记录并返回 false。</summary>
    public bool IsDuplicate(TKey key, byte[] data, int len)
    {
        ulong h = Fnv.Hash(data, len);
        if (_last.TryGetValue(key, out ulong prev) && prev == h)
            return true;
        _last[key] = h;
        return false;
    }

    public void Forget(TKey key) => _last.Remove(key);

    public void Clear() => _last.Clear();

    public int Count => _last.Count;
}
