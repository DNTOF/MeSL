// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/MeSL)

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace MeSL.Core;

/// <summary>
/// 节点间握手鉴权（MeSL-Protocol-v0 §3）：每对节点一把预共享密钥，
/// 双方各自用 HMAC-SHA256 证明持有密钥，密钥本身永不上线路。
/// </summary>
public static class PeerAuth
{
    public const string ProtocolTag = "mesl/0";
    public const int MinSecretLength = 32;
    public const int MaxServerIdLength = 32;
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromSeconds(60);

    /// <summary>server_id：1..32 位小写字母 / 数字 / '-'，且不以 '-' 开头。</summary>
    public static bool IsValidServerId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id!.Length > MaxServerIdLength || id[0] == '-')
            return false;
        foreach (char c in id)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>密钥强度（fail-closed）：长度 ≥ 32，且不能是文档示例值。</summary>
    public static bool IsAcceptableSecret(string? secret, out string reason)
    {
        if (string.IsNullOrEmpty(secret)) { reason = "为空"; return false; }
        if (secret!.Length < MinSecretLength) { reason = $"长度 {secret.Length} < {MinSecretLength}"; return false; }
        if (secret.IndexOf("change_me", StringComparison.OrdinalIgnoreCase) >= 0) { reason = "仍是示例值"; return false; }
        foreach (char c in secret)
        {
            if (char.IsWhiteSpace(c) || c == '"' || c == '\'' || c == '\u2018' || c == '\u2019' || c == '\u201C' || c == '\u201D')
            {
                reason = "含空白或引号（常见于从聊天软件复制）";
                return false;
            }
        }
        reason = "";
        return true;
    }

    public static string NewNonce()
    {
        byte[] b = new byte[16];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
        return ToHex(b);
    }

    /// <summary>dialer → acceptor 请求签名。</summary>
    public static string SignRequest(string secret, string dialerId, string acceptorId, long unixTs, string nonce) =>
        Hmac(secret, $"{ProtocolTag}|req|{dialerId}|{acceptorId}|{unixTs}|{nonce}");

    /// <summary>acceptor → dialer 应答签名：绑定请求 nonce 与 Sec-WebSocket-Accept，防应答重放。</summary>
    public static string SignResponse(string secret, string acceptorId, string dialerId, string nonce, string wsAccept) =>
        Hmac(secret, $"{ProtocolTag}|resp|{acceptorId}|{dialerId}|{nonce}|{wsAccept}");

    public static bool VerifyRequest(string secret, string dialerId, string acceptorId, long unixTs, string nonce,
        string providedMac, DateTime nowUtc, NonceCache replay, out string error)
    {
        long now = ToUnix(nowUtc);
        if (Math.Abs(now - unixTs) > (long)MaxClockSkew.TotalSeconds)
        {
            error = "clock_skew";
            return false;
        }
        if (!IsValidNonce(nonce))
        {
            error = "bad_nonce";
            return false;
        }
        string expected = SignRequest(secret, dialerId, acceptorId, unixTs, nonce);
        if (!AuthLockout.SecureEquals(expected, (providedMac ?? "").ToLowerInvariant()))
        {
            error = "bad_mac";
            return false;
        }
        // 只有签名正确的 nonce 才进缓存，避免未鉴权方刷满重放表
        if (!replay.TryAdd(dialerId + "|" + nonce, nowUtc))
        {
            error = "replay";
            return false;
        }
        error = "";
        return true;
    }

    public static bool VerifyResponse(string secret, string acceptorId, string dialerId, string nonce, string wsAccept,
        string? providedMac) =>
        AuthLockout.SecureEquals(SignResponse(secret, acceptorId, dialerId, nonce, wsAccept), (providedMac ?? "").ToLowerInvariant());

    public static long ToUnix(DateTime utc) => (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

    private static bool IsValidNonce(string? nonce)
    {
        if (nonce == null || nonce.Length != 32) return false;
        foreach (char c in nonce)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        }
        return true;
    }

    private static string Hmac(string secret, string message)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return ToHex(h.ComputeHash(Encoding.UTF8.GetBytes(message)));
    }

    private static string ToHex(byte[] b)
    {
        var sb = new StringBuilder(b.Length * 2);
        foreach (byte x in b) sb.Append(x.ToString("x2"));
        return sb.ToString();
    }
}

/// <summary>有界的 nonce 重放缓存：条目在 2×MaxClockSkew 后过期（时间戳窗口外的旧请求本就会被拒）。</summary>
public sealed class NonceCache
{
    private readonly int _capacity;
    private readonly Dictionary<string, DateTime> _seen = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, DateTime At)> _order = new();

    public NonceCache(int capacity = 4096) => _capacity = capacity;

    public bool TryAdd(string key, DateTime nowUtc)
    {
        Expire(nowUtc);
        if (_seen.ContainsKey(key)) return false;
        while (_seen.Count >= _capacity && _order.Count > 0)
            _seen.Remove(_order.Dequeue().Key);
        _seen[key] = nowUtc;
        _order.Enqueue((key, nowUtc));
        return true;
    }

    public int Count => _seen.Count;

    private void Expire(DateTime nowUtc)
    {
        TimeSpan ttl = PeerAuth.MaxClockSkew + PeerAuth.MaxClockSkew;
        while (_order.Count > 0 && nowUtc - _order.Peek().At > ttl)
            _seen.Remove(_order.Dequeue().Key);
    }
}
