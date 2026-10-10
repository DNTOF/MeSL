// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/MeSL)

using System;
using MeSL.Core;
using Xunit;

namespace MeSL.Core.Tests;

public class WsFramingTests
{
    private static readonly byte[] Mask = { 0x11, 0x22, 0x33, 0x44 };

    [Fact]
    public void AcceptKey_MatchesRfc6455Example()
    {
        Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", WsFraming.ComputeAccept("dGhlIHNhbXBsZSBub25jZQ=="));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(125)]
    [InlineData(126)]
    [InlineData(4000)]
    [InlineData(70000)]
    public void Roundtrip_MaskedClientToServer(int size)
    {
        byte[] payload = new byte[size];
        new Random(size).NextBytes(payload);
        byte[] wire = WsFraming.Encode(WsFraming.OpBinary, payload, Mask);

        byte[] buf = new byte[wire.Length + 16];
        Buffer.BlockCopy(wire, 0, buf, 0, wire.Length);
        int len = wire.Length;
        Assert.Equal(WsParseResult.Frame, WsFraming.TryParse(buf, ref len, 100_000, requireMasked: true, out int op, out byte[] got));
        Assert.Equal(WsFraming.OpBinary, op);
        Assert.Equal(payload, got);
        Assert.Equal(0, len);
    }

    [Fact]
    public void PartialFrame_NeedsMore()
    {
        byte[] wire = WsFraming.Encode(WsFraming.OpBinary, new byte[300], null);
        byte[] buf = new byte[1024];
        Buffer.BlockCopy(wire, 0, buf, 0, 100);
        int len = 100;
        Assert.Equal(WsParseResult.NeedMore, WsFraming.TryParse(buf, ref len, 1024, false, out _, out _));
        Assert.Equal(100, len);
    }

    [Fact]
    public void TwoFramesInBuffer_ParsedInOrder()
    {
        byte[] a = WsFraming.Encode(WsFraming.OpBinary, new byte[] { 1, 2 }, null);
        byte[] b = WsFraming.Encode(WsFraming.OpPing, new byte[] { 9 }, null);
        byte[] buf = new byte[64];
        Buffer.BlockCopy(a, 0, buf, 0, a.Length);
        Buffer.BlockCopy(b, 0, buf, a.Length, b.Length);
        int len = a.Length + b.Length;
        Assert.Equal(WsParseResult.Frame, WsFraming.TryParse(buf, ref len, 64, false, out int op1, out byte[] p1));
        Assert.Equal(WsParseResult.Frame, WsFraming.TryParse(buf, ref len, 64, false, out int op2, out byte[] p2));
        Assert.Equal(WsFraming.OpBinary, op1);
        Assert.Equal(new byte[] { 1, 2 }, p1);
        Assert.Equal(WsFraming.OpPing, op2);
        Assert.Equal(new byte[] { 9 }, p2);
        Assert.Equal(0, len);
    }

    [Fact]
    public void DeclaredLengthAboveCap_IsViolation()
    {
        // 127 长度 + 2^32：SLDataAPI L-02 的回绕场景
        byte[] buf = new byte[64];
        buf[0] = 0x82; buf[1] = 127;
        buf[5] = 0x01; // 0x00000001_00000000
        int len = 10;
        Assert.Equal(WsParseResult.Violation, WsFraming.TryParse(buf, ref len, 16 * 1024, false, out _, out _));
    }

    [Fact]
    public void FrameLargerThanReceiveBuffer_IsViolation()
    {
        // SLDataAPI N-01：声明长度在上限内但超过接收缓冲容量，永远收不全
        byte[] wire = WsFraming.Encode(WsFraming.OpBinary, new byte[200], null);
        byte[] buf = new byte[128];
        Buffer.BlockCopy(wire, 0, buf, 0, 128);
        int len = 128;
        Assert.Equal(WsParseResult.Violation, WsFraming.TryParse(buf, ref len, 16 * 1024, false, out _, out _));
    }

    [Fact]
    public void MaskDirectionMismatch_IsViolation()
    {
        byte[] masked = WsFraming.Encode(WsFraming.OpBinary, new byte[] { 1 }, Mask);
        byte[] plain = WsFraming.Encode(WsFraming.OpBinary, new byte[] { 1 }, null);
        int l1 = masked.Length, l2 = plain.Length;
        Assert.Equal(WsParseResult.Violation, WsFraming.TryParse(masked, ref l1, 64, requireMasked: false, out _, out _));
        Assert.Equal(WsParseResult.Violation, WsFraming.TryParse(plain, ref l2, 64, requireMasked: true, out _, out _));
    }

    [Fact]
    public void Fragmentation_IsViolation()
    {
        byte[] buf = { 0x02, 0x01, 0xAA }; // FIN=0
        int len = buf.Length;
        Assert.Equal(WsParseResult.Violation, WsFraming.TryParse(buf, ref len, 64, false, out _, out _));
    }

    [Fact]
    public void HeaderHelpers()
    {
        string h = "GET /mesl/v0 HTTP/1.1\r\nHost: x\r\nX-MeSL-Server: alpha\r\n\r\n";
        byte[] b = System.Text.Encoding.ASCII.GetBytes(h + "tail");
        Assert.Equal(h.Length, WsFraming.FindHeaderEnd(b, b.Length));
        Assert.Equal("alpha", WsFraming.GetHeader(h, "x-mesl-server"));
        Assert.Null(WsFraming.GetHeader(h, "X-Missing"));
    }
}
