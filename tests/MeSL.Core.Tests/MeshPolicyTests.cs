// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/MeSL)

using System.Collections.Generic;
using MeSL.Core;
using Xunit;

namespace MeSL.Core.Tests;

public class MeshPolicyTests
{
    [Theory]
    // 本地频道：永远原版
    [InlineData(false, MeshGroup.GroupA, CaptureKind.Radio, false, SendVerdict.Local)]
    // 雷达门（默认）：人类须用对讲机
    [InlineData(true, MeshGroup.GroupA, CaptureKind.Radio, false, SendVerdict.Forward)]
    [InlineData(true, MeshGroup.GroupB, CaptureKind.Proximity, false, SendVerdict.NeedRadio)]
    // ForceRadio 关闭雷达门
    [InlineData(true, MeshGroup.GroupB, CaptureKind.Proximity, true, SendVerdict.Forward)]
    // 旁观者只走旁观者频道
    [InlineData(true, MeshGroup.Spectator, CaptureKind.Spectator, false, SendVerdict.Forward)]
    [InlineData(true, MeshGroup.Spectator, CaptureKind.Proximity, true, SendVerdict.Drop)]
    // 非人类 / 非 mesh 频道
    [InlineData(true, MeshGroup.None, CaptureKind.Proximity, true, SendVerdict.Drop)]
    [InlineData(true, MeshGroup.GroupA, CaptureKind.Other, true, SendVerdict.Drop)]
    public void EvaluateSend(bool remote, MeshGroup g, CaptureKind k, bool forceRadio, SendVerdict expected) =>
        Assert.Equal(expected, MeshPolicy.EvaluateSend(remote, g, k, forceRadio));

    [Theory]
    [InlineData(true, CaptureKind.Proximity, true)]
    [InlineData(true, CaptureKind.Spectator, true)]
    [InlineData(true, CaptureKind.Radio, true)]
    [InlineData(false, CaptureKind.Proximity, false)]
    [InlineData(false, CaptureKind.Radio, false)]
    [InlineData(true, CaptureKind.Other, false)]
    public void BlockLocalDelivery(bool remote, CaptureKind k, bool expected) =>
        Assert.Equal(expected, MeshPolicy.BlockLocalDelivery(remote, k));

    [Theory]
    [InlineData(MeshGroup.GroupA, CaptureKind.Radio)]
    [InlineData(MeshGroup.GroupB, CaptureKind.Radio)]
    [InlineData(MeshGroup.Spectator, CaptureKind.Spectator)]
    [InlineData(MeshGroup.None, CaptureKind.Other)]
    public void ReinjectKind_NeverProximity(MeshGroup g, CaptureKind expected)
    {
        Assert.Equal(expected, MeshPolicy.ReinjectKind(g));
        Assert.NotEqual(CaptureKind.Proximity, MeshPolicy.ReinjectKind(g));
    }

    [Theory]
    [InlineData(MeshGroup.GroupA, 1, MeshGroup.GroupA, 1, CaptureKind.Radio, true)]
    [InlineData(MeshGroup.GroupA, 1, MeshGroup.GroupA, 1, CaptureKind.Proximity, false)]
    [InlineData(MeshGroup.GroupB, 1, MeshGroup.GroupB, 1, CaptureKind.Other, false)]
    [InlineData(MeshGroup.GroupA, 1, MeshGroup.GroupB, 1, CaptureKind.Radio, false)]
    [InlineData(MeshGroup.GroupB, 2, MeshGroup.GroupB, 1, CaptureKind.Radio, false)]
    [InlineData(MeshGroup.GroupB, 1, MeshGroup.GroupB, 0, CaptureKind.Radio, false)]
    [InlineData(MeshGroup.Spectator, 1, MeshGroup.Spectator, 1, CaptureKind.Spectator, true)]
    [InlineData(MeshGroup.Spectator, 1, MeshGroup.Spectator, 1, CaptureKind.Radio, false)]
    [InlineData(MeshGroup.Spectator, 1, MeshGroup.GroupA, 1, CaptureKind.Spectator, false)]
    [InlineData(MeshGroup.GroupA, 1, MeshGroup.Spectator, 1, CaptureKind.Radio, false)]
    [InlineData(MeshGroup.GroupA, 0, MeshGroup.GroupA, 0, CaptureKind.Radio, false)]
    [InlineData(MeshGroup.None, 1, MeshGroup.None, 1, CaptureKind.Other, false)]
    public void AllowRemoteReceive(MeshGroup fg, int fc, MeshGroup rg, int rc, CaptureKind k, bool expected) =>
        Assert.Equal(expected, MeshPolicy.AllowRemoteReceive(fg, fc, rg, rc, k));

    [Fact]
    public void ShouldDial_ExactlyOneSide()
    {
        Assert.True(MeshPolicy.ShouldDial("alpha", "bravo"));
        Assert.False(MeshPolicy.ShouldDial("bravo", "alpha"));
        Assert.False(MeshPolicy.ShouldDial("alpha", "alpha"));
    }

    [Fact]
    public void ChannelTable_CyclesAndMapsByName()
    {
        var t = new ChannelTable(new[] { "ops", "squad", "ops", "Bad Name" });
        Assert.Equal(3, t.Count);
        Assert.Equal(1, t.Next(0));
        Assert.Equal(2, t.Next(1));
        Assert.Equal(0, t.Next(2));
        Assert.Equal(0, t.Next(99));
        Assert.Equal("local", t.NameOf(0));
        Assert.Equal("squad", t.NameOf(2));
        Assert.Equal(1, t.WireIndexOf(2));
        Assert.Equal(new[] { 2, -1, 1 }, t.MapPeerChannels(new List<string> { "squad", "other", "ops" }));
    }

    [Fact]
    public void ChannelTable_NoMeshChannels_StaysLocal()
    {
        var t = new ChannelTable(new string[0]);
        Assert.Equal(1, t.Count);
        Assert.Equal(0, t.Next(0));
    }

    [Fact]
    public void BurstTracker_GapStartsNewBurst()
    {
        var b = new BurstTracker<uint>(0.8f);
        Assert.True(b.Mark(1, 0f));
        Assert.False(b.Mark(1, 0.5f));
        Assert.False(b.Mark(1, 1.2f));
        Assert.True(b.Mark(1, 2.1f));
        Assert.Equal(new[] { 1u }, b.CollectIdle(10f, 6f));
    }

    [Fact]
    public void DuplicateFilter_ContentBased()
    {
        var d = new DuplicateFilter<uint>();
        byte[] a = { 1, 2, 3 }, b = { 1, 2, 4 };
        Assert.False(d.IsDuplicate(5, a, 3));
        Assert.True(d.IsDuplicate(5, a, 3));
        Assert.False(d.IsDuplicate(5, b, 3)); // 同长度不同内容不能误删
        Assert.False(d.IsDuplicate(6, b, 3));
    }

    [Fact]
    public void TokenBucket_And_Backoff()
    {
        var tb = new TokenBucket(2, 1, 0);
        Assert.True(tb.TryTake(0));
        Assert.True(tb.TryTake(0));
        Assert.False(tb.TryTake(0));
        Assert.True(tb.TryTake(1.01));

        var bo = new ReconnectBackoff();
        Assert.Equal(2, bo.NextDelaySeconds());
        Assert.Equal(4, bo.NextDelaySeconds());
        for (int i = 0; i < 10; i++) bo.NextDelaySeconds();
        Assert.Equal(60, bo.NextDelaySeconds());
        bo.Reset();
        Assert.Equal(2, bo.NextDelaySeconds());
    }
}
