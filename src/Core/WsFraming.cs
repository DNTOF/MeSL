// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/SLDataAPI)
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/MeSL)
// 改编自 SLDataAPI Voice/VoiceService.cs（VoiceClient.ProcessWsFrames / SendWsFrame / TryHandshake）。

using System;
using System.Security.Cryptography;
using System.Text;

namespace MeSL.Core;

public enum WsParseResult
{
    /// <summary>缓冲内数据不足一帧，等下一轮读取。</summary>
    NeedMore,
    /// <summary>解析出一帧，已从缓冲中移除。</summary>
    Frame,
    /// <summary>协议违规（超长 / 装不下 / 分片 / 掩码方向错误），调用方必须断连。</summary>
    Violation,
}

/// <summary>
/// RFC 6455 帧编解码（只支持单帧消息，不支持分片——MeSL 消息都远小于上限）。
/// 两条来自 SLDataAPI 线上事故的硬约束：
///   L-02：声明长度超过 maxPayload 直接判违规，防 (int)len 回绕绕过缓冲检查；
///   N-01：声明长度超过接收缓冲容量的帧永远收不全，必须断连而不是无限等待（否则永久占用连接槽）。
/// </summary>
public static class WsFraming
{
    public const string AcceptMagic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    public const int OpText = 0x1;
    public const int OpBinary = 0x2;
    public const int OpClose = 0x8;
    public const int OpPing = 0x9;
    public const int OpPong = 0xA;

    public static string ComputeAccept(string secWebSocketKey)
    {
        using var sha = SHA1.Create();
        return Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(secWebSocketKey + AcceptMagic)));
    }

    /// <summary>
    /// 尝试从 buf[0..len) 解析一帧。成功时 payload 已去掩码，len 已扣除消费字节。
    /// requireMasked：服务端（acceptor）读取时为 true（RFC 要求客户端→服务端必须掩码），
    /// 客户端（dialer）读取时为 false（服务端→客户端禁止掩码）。
    /// </summary>
    public static WsParseResult TryParse(byte[] buf, ref int len, int maxPayload, bool requireMasked,
        out int opcode, out byte[] payload)
    {
        opcode = 0;
        payload = Array.Empty<byte>();
        if (len < 2) return WsParseResult.NeedMore;

        int b0 = buf[0];
        int b1 = buf[1];
        bool fin = (b0 & 0x80) != 0;
        if ((b0 & 0x70) != 0) return WsParseResult.Violation; // RSV 位：未协商扩展
        opcode = b0 & 0x0F;
        bool masked = (b1 & 0x80) != 0;
        if (masked != requireMasked) return WsParseResult.Violation;
        if (!fin || opcode == 0x0) return WsParseResult.Violation; // 分片不支持

        long plen = b1 & 0x7F;
        int off = 2;
        if (plen == 126)
        {
            if (len < 4) return WsParseResult.NeedMore;
            plen = (buf[2] << 8) | buf[3];
            off = 4;
        }
        else if (plen == 127)
        {
            if (len < 10) return WsParseResult.NeedMore;
            plen = 0;
            for (int i = 0; i < 8; i++) plen = (plen << 8) | buf[2 + i];
            off = 10;
            if (plen < 0) return WsParseResult.Violation;
        }

        if (plen > maxPayload) return WsParseResult.Violation;

        int maskOff = off;
        if (masked) off += 4;

        long total = off + plen;
        if (total > buf.Length) return WsParseResult.Violation;
        if (len < total) return WsParseResult.NeedMore;

        payload = new byte[plen];
        Buffer.BlockCopy(buf, off, payload, 0, (int)plen);
        if (masked)
        {
            for (int i = 0; i < payload.Length; i++)
                payload[i] ^= buf[maskOff + (i & 3)];
        }

        int consumed = (int)total;
        Buffer.BlockCopy(buf, consumed, buf, 0, len - consumed);
        len -= consumed;
        return WsParseResult.Frame;
    }

    /// <summary>编码单帧。mask 非空时按 RFC 6455 加掩码（dialer 发送方向）。</summary>
    public static byte[] Encode(int opcode, byte[] payload, int payloadLength, byte[]? mask)
    {
        int headerLen = payloadLength < 126 ? 2 : payloadLength < 65536 ? 4 : 10;
        int maskLen = mask != null ? 4 : 0;
        byte[] full = new byte[headerLen + maskLen + payloadLength];
        full[0] = (byte)(0x80 | (opcode & 0x0F));
        byte maskBit = (byte)(mask != null ? 0x80 : 0x00);
        if (headerLen == 2)
        {
            full[1] = (byte)(maskBit | payloadLength);
        }
        else if (headerLen == 4)
        {
            full[1] = (byte)(maskBit | 126);
            full[2] = (byte)(payloadLength >> 8);
            full[3] = (byte)(payloadLength & 0xFF);
        }
        else
        {
            full[1] = (byte)(maskBit | 127);
            long l = payloadLength;
            for (int i = 0; i < 8; i++) full[9 - i] = (byte)(l >> (8 * i));
        }

        int off = headerLen;
        if (mask != null)
        {
            Buffer.BlockCopy(mask, 0, full, off, 4);
            off += 4;
            for (int i = 0; i < payloadLength; i++)
                full[off + i] = (byte)(payload[i] ^ mask[i & 3]);
        }
        else
        {
            Buffer.BlockCopy(payload, 0, full, off, payloadLength);
        }
        return full;
    }

    public static byte[] Encode(int opcode, byte[] payload, byte[]? mask) =>
        Encode(opcode, payload, payload.Length, mask);

    /// <summary>在 buf[0..len) 中查找 HTTP 头结束位置（\r\n\r\n 之后的下标），未找到返回 -1。</summary>
    public static int FindHeaderEnd(byte[] buf, int len)
    {
        for (int i = 0; i + 3 < len; i++)
        {
            if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                return i + 4;
        }
        return -1;
    }

    /// <summary>从原始 HTTP 头文本取指定头（大小写不敏感，取第一个）。</summary>
    public static string? GetHeader(string headerBlock, string name)
    {
        foreach (var raw in headerBlock.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            int idx = line.IndexOf(':');
            if (idx <= 0) continue;
            if (line.Substring(0, idx).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return line.Substring(idx + 1).Trim();
        }
        return null;
    }
}
