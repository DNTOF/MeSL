// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/MeSL)

using System;
using System.Collections.Generic;
using MeSL.Core;
using Xunit;

namespace MeSL.Core.Tests;

public class MeshCodecTests
{
    private static MeshVoiceFrame Sample(bool withSite, int opusLen = 57)
    {
        var opus = new byte[opusLen];
        new Random(opusLen).NextBytes(opus);
        return new MeshVoiceFrame
        {
            ChannelIndex = 2,
            VoiceChannel = 2,
            Group = MeshGroup.GroupB,
            OriginNetId = 0xDEADBEEF,
            Seq = 123456,
            NewBurst = true,
            Role = -1,
            Site = withSite
                ? new MeshSite { RoomName = 37, RoomShape = 3, Zone = 2, LocalX = 1.5f, LocalY = 0.9f, LocalZ = -3.25f, WorldX = 120f, WorldY = -999.5f, WorldZ = 42f }
                : null,
            Opus = opus,
            OpusLength = opusLen,
        };
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Voice_Roundtrip_KeepsOpusBytesVerbatim(bool withSite)
    {
        var f = Sample(withSite);
        byte[] wire = MeshCodec.EncodeVoice(f);
        Assert.Equal(MeshVoiceFrame.FixedHeaderSize + (withSite ? MeshSite.WireSize : 0) + 2 + f.OpusLength, wire.Length);
        Assert.True(MeshCodec.TryDecodeVoice(wire, out var g));
        Assert.Equal(f.ChannelIndex, g.ChannelIndex);
        Assert.Equal(f.VoiceChannel, g.VoiceChannel);
        Assert.Equal(f.Group, g.Group);
        Assert.Equal(f.OriginNetId, g.OriginNetId);
        Assert.Equal(f.Seq, g.Seq);
        Assert.Equal(f.NewBurst, g.NewBurst);
        Assert.Equal(f.Role, g.Role);
        Assert.Equal(f.Site.HasValue, g.Site.HasValue);
        if (withSite)
        {
            Assert.Equal(f.Site!.Value.RoomName, g.Site!.Value.RoomName);
            Assert.Equal(f.Site.Value.LocalZ, g.Site.Value.LocalZ);
            Assert.Equal(f.Site.Value.WorldY, g.Site.Value.WorldY);
        }
        Assert.Equal(f.Opus, g.Opus);
    }

    [Fact]
    public void Voice_Rejects_TrailingBytes_UnknownGroup_UnknownFlags_BadCoords_Oversize()
    {
        byte[] ok = MeshCodec.EncodeVoice(Sample(true));

        byte[] trailing = new byte[ok.Length + 1];
        Buffer.BlockCopy(ok, 0, trailing, 0, ok.Length);
        Assert.False(MeshCodec.TryDecodeVoice(trailing, out _));

        byte[] badGroup = (byte[])ok.Clone(); badGroup[3] = 9;
        Assert.False(MeshCodec.TryDecodeVoice(badGroup, out _));

        byte[] badFlags = (byte[])ok.Clone(); badFlags[12] |= 0x80;
        Assert.False(MeshCodec.TryDecodeVoice(badFlags, out _));

        var nan = Sample(true);
        var s = nan.Site!.Value; s.WorldX = float.NaN; nan.Site = s;
        Assert.False(MeshCodec.TryDecodeVoice(MeshCodec.EncodeVoice(nan), out _));

        var far = Sample(true);
        s = far.Site!.Value; s.LocalY = 1e6f; far.Site = s;
        Assert.False(MeshCodec.TryDecodeVoice(MeshCodec.EncodeVoice(far), out _));

        Assert.Throws<ArgumentOutOfRangeException>(() => MeshCodec.EncodeVoice(Sample(false, MeshCodec.MaxOpusBytes + 1)));
        Assert.False(MeshCodec.TryDecodeVoice(MeshCodec.EncodeVoice(Sample(false, 0)), out _));
    }

    [Fact]
    public void Voice_RandomInput_NeverThrows()
    {
        var rnd = new Random(7);
        byte[] seed = MeshCodec.EncodeVoice(Sample(true));
        for (int i = 0; i < 20000; i++)
        {
            byte[] b = new byte[rnd.Next(0, 80)];
            rnd.NextBytes(b);
            if (b.Length > 0 && rnd.Next(2) == 0) b[0] = MeshMessageType.Voice;
            MeshCodec.TryDecodeVoice(b, out _);
            MeshCodec.TryDecodeSpeaker(b, out _);
            MeshCodec.TryDecodeHello(b, out _);
            MeshCodec.TryDecodeBye(b, out _);

            byte[] m = (byte[])seed.Clone();
            m[rnd.Next(m.Length)] = (byte)rnd.Next(256);
            MeshCodec.TryDecodeVoice(m, out _);
        }
    }

    [Fact]
    public void Speaker_SanitizesAndTruncatesNickname()
    {
        var info = new MeshSpeakerInfo { OriginNetId = 7, Role = 1, Group = MeshGroup.GroupA, Nickname = "Evil\r\n\tName" + new string('长', 60) };
        Assert.True(MeshCodec.TryDecodeSpeaker(MeshCodec.EncodeSpeaker(info), out var got));
        Assert.StartsWith("EvilName", got.Nickname);
        Assert.DoesNotContain('\n', got.Nickname);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(got.Nickname) <= MeshCodec.MaxNicknameBytes);
        Assert.Equal(MeshGroup.GroupA, got.Group);
    }

    [Fact]
    public void SpeakerGone_And_Bye_Roundtrip()
    {
        Assert.True(MeshCodec.TryDecodeSpeakerGone(MeshCodec.EncodeSpeakerGone(99), out uint id));
        Assert.Equal(99u, id);
        Assert.True(MeshCodec.TryDecodeBye(MeshCodec.EncodeBye("shutdown\n"), out string why));
        Assert.Equal("shutdown", why);
    }

    [Fact]
    public void Hello_Roundtrip_AndRejectsDuplicatesOrBadNames()
    {
        var h = new MeshHello { ProtocolVersion = 0, ServerId = "alpha", Channels = new List<string> { "ops", "squad-2" }, MaxOpusBytes = 1500 };
        Assert.True(MeshCodec.TryDecodeHello(MeshCodec.EncodeHello(h), out var g));
        Assert.Equal("alpha", g.ServerId);
        Assert.Equal(new[] { "ops", "squad-2" }, g.Channels);
        Assert.Equal(1500, g.MaxOpusBytes);

        byte[] dup = MeshCodec.EncodeHello(new MeshHello { ServerId = "alpha", Channels = new List<string> { "ops", "opx" } });
        dup[dup.Length - 3] = (byte)'s'; // "opx" → "ops"
        Assert.False(MeshCodec.TryDecodeHello(dup, out _));

        Assert.Throws<ArgumentException>(() => MeshCodec.EncodeHello(new MeshHello { ServerId = "alpha", Channels = new List<string> { "Bad Name" } }));
    }
}
