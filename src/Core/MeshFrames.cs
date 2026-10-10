// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/SLDataAPI)
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/MeSL)
// 语音帧布局源自 SLDataAPI 语音 WebSocket 二进制帧（0x01 | channel | playerId | seq | payload），
// MeSL 改为原样携带 Opus 字节并附加采集站点。

using System;
using System.Collections.Generic;
using System.Text;

namespace MeSL.Core;

public static class MeshMessageType
{
    public const byte Voice = 0x01;
    public const byte Speaker = 0x02;
    public const byte SpeakerGone = 0x03;
    public const byte Hello = 0x10;
    public const byte Bye = 0x11;
}

/// <summary>阵营分组。线路值固定，不随游戏枚举变化。</summary>
public enum MeshGroup : byte
{
    None = 0,
    /// <summary>D 级人员 + 混沌分裂者。</summary>
    GroupA = 1,
    /// <summary>科学家 + 九尾狐（含设施警卫）。</summary>
    GroupB = 2,
    Spectator = 3,
}

/// <summary>
/// 采集站点：说话者所在房间（名称/形状/区域）+ 房间局部坐标 + 世界坐标。
/// 接收服优先按房间局部坐标还原（两服地图种子不同也能落到同类房间的同一位置），找不到同类房间才退回世界坐标。
/// </summary>
public struct MeshSite
{
    public short RoomName;
    public byte RoomShape;
    public byte Zone;
    public float LocalX, LocalY, LocalZ;
    public float WorldX, WorldY, WorldZ;

    public const int WireSize = 2 + 1 + 1 + 12 + 12;
}

public sealed class MeshVoiceFrame
{
    public const int FixedHeaderSize = 14;
    public const byte FlagNewBurst = 0x01;
    public const byte FlagHasSite = 0x02;

    /// <summary>频道下标：指向「发送方」HELLO 里宣告的频道表，接收方按名称映射到本地频道。</summary>
    public byte ChannelIndex;
    /// <summary>采集时的原版 VoiceChatChannel（Proximity / Radio / Spectator）。只做一致性校验，接收端重注入频道由 Group 决定（人类恒为 Radio）。</summary>
    public byte VoiceChannel;
    public MeshGroup Group;
    /// <summary>发送服上说话者的 netId；接收服以 (peer, netId) 作为代理说话者键。</summary>
    public uint OriginNetId;
    public uint Seq;
    public bool NewBurst;
    /// <summary>发送服上的 RoleTypeId（sbyte 原值）。</summary>
    public sbyte Role;
    public MeshSite? Site;
    public byte[] Opus = Array.Empty<byte>();
    public int OpusLength;
}

public sealed class MeshSpeakerInfo
{
    public uint OriginNetId;
    public sbyte Role;
    public MeshGroup Group;
    public string Nickname = "";
}

public sealed class MeshHello
{
    public byte ProtocolVersion;
    public string ServerId = "";
    public List<string> Channels = new();
    public ushort MaxOpusBytes;
}

/// <summary>MeSL-Protocol-v0 二进制消息编解码。所有多字节整数小端；解码失败一律返回 false（调用方计违规）。</summary>
public static class MeshCodec
{
    public const byte ProtocolVersion = 0;
    public const int MaxChannels = 16;
    public const int MaxNicknameBytes = 64;
    public const int MaxOpusBytes = 1500;
    /// <summary>站点坐标绝对值上限（米）。超出视为伪造，防止把代理说话者传送到地图外。</summary>
    public const float MaxCoordinate = 10000f;

    // ────────────── Voice ──────────────

    public static byte[] EncodeVoice(MeshVoiceFrame f)
    {
        if (f.OpusLength < 0 || f.OpusLength > MaxOpusBytes || f.OpusLength > f.Opus.Length)
            throw new ArgumentOutOfRangeException(nameof(f), "opus length");

        int size = MeshVoiceFrame.FixedHeaderSize + (f.Site.HasValue ? MeshSite.WireSize : 0) + 2 + f.OpusLength;
        var w = new Writer(size);
        w.U8(MeshMessageType.Voice);
        w.U8(f.ChannelIndex);
        w.U8(f.VoiceChannel);
        w.U8((byte)f.Group);
        w.U32(f.OriginNetId);
        w.U32(f.Seq);
        byte flags = 0;
        if (f.NewBurst) flags |= MeshVoiceFrame.FlagNewBurst;
        if (f.Site.HasValue) flags |= MeshVoiceFrame.FlagHasSite;
        w.U8(flags);
        w.U8(unchecked((byte)f.Role));
        if (f.Site.HasValue)
        {
            MeshSite s = f.Site.Value;
            w.I16(s.RoomName);
            w.U8(s.RoomShape);
            w.U8(s.Zone);
            w.F32(s.LocalX); w.F32(s.LocalY); w.F32(s.LocalZ);
            w.F32(s.WorldX); w.F32(s.WorldY); w.F32(s.WorldZ);
        }
        w.U16((ushort)f.OpusLength);
        w.Bytes(f.Opus, f.OpusLength);
        return w.Buffer;
    }

    public static bool TryDecodeVoice(byte[] p, out MeshVoiceFrame frame)
    {
        frame = new MeshVoiceFrame();
        var r = new Reader(p);
        if (!r.U8(out byte type) || type != MeshMessageType.Voice) return false;
        if (!r.U8(out frame.ChannelIndex)) return false;
        if (!r.U8(out frame.VoiceChannel)) return false;
        if (!r.U8(out byte g) || !IsKnownGroup(g)) return false;
        frame.Group = (MeshGroup)g;
        if (!r.U32(out frame.OriginNetId)) return false;
        if (!r.U32(out frame.Seq)) return false;
        if (!r.U8(out byte flags)) return false;
        if ((flags & ~(MeshVoiceFrame.FlagNewBurst | MeshVoiceFrame.FlagHasSite)) != 0) return false;
        frame.NewBurst = (flags & MeshVoiceFrame.FlagNewBurst) != 0;
        if (!r.U8(out byte role)) return false;
        frame.Role = unchecked((sbyte)role);

        if ((flags & MeshVoiceFrame.FlagHasSite) != 0)
        {
            var s = new MeshSite();
            if (!r.I16(out s.RoomName) || !r.U8(out s.RoomShape) || !r.U8(out s.Zone)) return false;
            if (!r.F32(out s.LocalX) || !r.F32(out s.LocalY) || !r.F32(out s.LocalZ)) return false;
            if (!r.F32(out s.WorldX) || !r.F32(out s.WorldY) || !r.F32(out s.WorldZ)) return false;
            if (!SaneCoord(s.LocalX) || !SaneCoord(s.LocalY) || !SaneCoord(s.LocalZ) ||
                !SaneCoord(s.WorldX) || !SaneCoord(s.WorldY) || !SaneCoord(s.WorldZ)) return false;
            frame.Site = s;
        }

        if (!r.U16(out ushort len) || len == 0 || len > MaxOpusBytes) return false;
        if (r.Remaining != len) return false;
        frame.Opus = new byte[len];
        r.Bytes(frame.Opus, len);
        frame.OpusLength = len;
        return true;
    }

    // ────────────── Speaker / SpeakerGone ──────────────

    public static byte[] EncodeSpeaker(MeshSpeakerInfo s)
    {
        byte[] nick = Utf8Truncate(CleanField(s.Nickname), MaxNicknameBytes);
        var w = new Writer(1 + 4 + 1 + 1 + 1 + nick.Length);
        w.U8(MeshMessageType.Speaker);
        w.U32(s.OriginNetId);
        w.U8(unchecked((byte)s.Role));
        w.U8((byte)s.Group);
        w.U8((byte)nick.Length);
        w.Bytes(nick, nick.Length);
        return w.Buffer;
    }

    public static bool TryDecodeSpeaker(byte[] p, out MeshSpeakerInfo info)
    {
        info = new MeshSpeakerInfo();
        var r = new Reader(p);
        if (!r.U8(out byte type) || type != MeshMessageType.Speaker) return false;
        if (!r.U32(out info.OriginNetId)) return false;
        if (!r.U8(out byte role)) return false;
        info.Role = unchecked((sbyte)role);
        if (!r.U8(out byte g) || !IsKnownGroup(g)) return false;
        info.Group = (MeshGroup)g;
        if (!r.Str(MaxNicknameBytes, out string nick)) return false;
        if (r.Remaining != 0) return false;
        info.Nickname = CleanField(nick);
        return true;
    }

    public static byte[] EncodeSpeakerGone(uint originNetId)
    {
        var w = new Writer(5);
        w.U8(MeshMessageType.SpeakerGone);
        w.U32(originNetId);
        return w.Buffer;
    }

    public static bool TryDecodeSpeakerGone(byte[] p, out uint originNetId)
    {
        originNetId = 0;
        var r = new Reader(p);
        return r.U8(out byte type) && type == MeshMessageType.SpeakerGone && r.U32(out originNetId) && r.Remaining == 0;
    }

    // ────────────── Hello / Bye ──────────────

    public static byte[] EncodeHello(MeshHello h)
    {
        if (!PeerAuth.IsValidServerId(h.ServerId)) throw new ArgumentException("server id", nameof(h));
        if (h.Channels.Count > MaxChannels) throw new ArgumentException("too many channels", nameof(h));
        var parts = new List<byte[]>();
        int size = 1 + 1 + 1 + h.ServerId.Length + 1 + 2;
        foreach (string c in h.Channels)
        {
            if (!IsValidChannelName(c)) throw new ArgumentException("channel name: " + c, nameof(h));
            byte[] b = Encoding.ASCII.GetBytes(c);
            parts.Add(b);
            size += 1 + b.Length;
        }
        var w = new Writer(size);
        w.U8(MeshMessageType.Hello);
        w.U8(h.ProtocolVersion);
        byte[] id = Encoding.ASCII.GetBytes(h.ServerId);
        w.U8((byte)id.Length);
        w.Bytes(id, id.Length);
        w.U8((byte)parts.Count);
        foreach (var b in parts)
        {
            w.U8((byte)b.Length);
            w.Bytes(b, b.Length);
        }
        w.U16(h.MaxOpusBytes);
        return w.Buffer;
    }

    public static bool TryDecodeHello(byte[] p, out MeshHello hello)
    {
        hello = new MeshHello();
        var r = new Reader(p);
        if (!r.U8(out byte type) || type != MeshMessageType.Hello) return false;
        if (!r.U8(out hello.ProtocolVersion)) return false;
        if (!r.Str(PeerAuth.MaxServerIdLength, out hello.ServerId) || !PeerAuth.IsValidServerId(hello.ServerId)) return false;
        if (!r.U8(out byte n) || n > MaxChannels) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            if (!r.Str(PeerAuth.MaxServerIdLength, out string c) || !IsValidChannelName(c) || !seen.Add(c)) return false;
            hello.Channels.Add(c);
        }
        if (!r.U16(out hello.MaxOpusBytes)) return false;
        return r.Remaining == 0;
    }

    public static byte[] EncodeBye(string reason)
    {
        byte[] b = Utf8Truncate(CleanField(reason), 120);
        var w = new Writer(2 + b.Length);
        w.U8(MeshMessageType.Bye);
        w.U8((byte)b.Length);
        w.Bytes(b, b.Length);
        return w.Buffer;
    }

    public static bool TryDecodeBye(byte[] p, out string reason)
    {
        var r = new Reader(p);
        reason = "";
        return r.U8(out byte type) && type == MeshMessageType.Bye && r.Str(120, out reason) && r.Remaining == 0;
    }

    // ────────────── 校验与清洗 ──────────────

    /// <summary>频道名与 server_id 同规则（小写字母/数字/'-'，≤32），保证两端按字节比较即可匹配。</summary>
    public static bool IsValidChannelName(string? name) => PeerAuth.IsValidServerId(name);

    public static bool IsKnownGroup(byte g) => g >= (byte)MeshGroup.GroupA && g <= (byte)MeshGroup.Spectator;

    /// <summary>剥除控制字符 / Tab / 换行：远端昵称会进入本服日志与提示，不允许伪造行。</summary>
    public static string CleanField(string? s)
    {
        s ??= "";
        if (s.Length == 0) return s;
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c < 0x20 || c == 0x7F) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool SaneCoord(float v) => !float.IsNaN(v) && !float.IsInfinity(v) && Math.Abs(v) <= MaxCoordinate;

    private static byte[] Utf8Truncate(string s, int maxBytes)
    {
        byte[] b = Encoding.UTF8.GetBytes(s);
        if (b.Length <= maxBytes) return b;
        int cut = maxBytes;
        while (cut > 0 && (b[cut] & 0xC0) == 0x80) cut--; // 不截断在多字节字符中间
        byte[] r = new byte[cut];
        Buffer.BlockCopy(b, 0, r, 0, cut);
        return r;
    }

    // ────────────── 小端读写器 ──────────────

    private sealed class Writer
    {
        public readonly byte[] Buffer;
        private int _o;
        public Writer(int size) => Buffer = new byte[size];
        public void U8(byte v) => Buffer[_o++] = v;
        public void U16(ushort v) { Buffer[_o++] = (byte)v; Buffer[_o++] = (byte)(v >> 8); }
        public void I16(short v) => U16(unchecked((ushort)v));
        public void U32(uint v) { for (int i = 0; i < 4; i++) Buffer[_o++] = (byte)(v >> (8 * i)); }
        public void F32(float v)
        {
            byte[] b = BitConverter.GetBytes(v);
            if (!BitConverter.IsLittleEndian) Array.Reverse(b);
            Bytes(b, 4);
        }
        public void Bytes(byte[] src, int n) { System.Buffer.BlockCopy(src, 0, Buffer, _o, n); _o += n; }
    }

    private sealed class Reader
    {
        private readonly byte[] _b;
        private int _o;
        public Reader(byte[] b) => _b = b;
        public int Remaining => _b.Length - _o;
        public bool U8(out byte v) { v = 0; if (Remaining < 1) return false; v = _b[_o++]; return true; }
        public bool U16(out ushort v) { v = 0; if (Remaining < 2) return false; v = (ushort)(_b[_o] | (_b[_o + 1] << 8)); _o += 2; return true; }
        public bool I16(out short v) { bool ok = U16(out ushort u); v = unchecked((short)u); return ok; }
        public bool U32(out uint v)
        {
            v = 0;
            if (Remaining < 4) return false;
            for (int i = 0; i < 4; i++) v |= (uint)_b[_o + i] << (8 * i);
            _o += 4;
            return true;
        }
        public bool F32(out float v)
        {
            v = 0;
            if (Remaining < 4) return false;
            byte[] t = new byte[4];
            System.Buffer.BlockCopy(_b, _o, t, 0, 4);
            if (!BitConverter.IsLittleEndian) Array.Reverse(t);
            v = BitConverter.ToSingle(t, 0);
            _o += 4;
            return true;
        }
        public void Bytes(byte[] dst, int n) { System.Buffer.BlockCopy(_b, _o, dst, 0, n); _o += n; }
        public bool Str(int maxBytes, out string s)
        {
            s = "";
            if (!U8(out byte n) || n > maxBytes || Remaining < n) return false;
            try { s = new UTF8Encoding(false, true).GetString(_b, _o, n); }
            catch (ArgumentException) { return false; }
            _o += n;
            return true;
        }
    }
}
