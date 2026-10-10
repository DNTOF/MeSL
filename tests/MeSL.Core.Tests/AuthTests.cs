// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/MeSL)

using System;
using MeSL.Core;
using Xunit;

namespace MeSL.Core.Tests;

public class AuthTests
{
    private const string Secret = "0123456789abcdef0123456789ABCDEF-xyz";
    private static readonly DateTime T0 = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Lockout_AfterThreshold_ThenExpires()
    {
        var l = new AuthLockout(3, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        for (int i = 0; i < 2; i++) l.RegisterFailure("1.2.3.4", T0);
        Assert.False(l.IsLocked("1.2.3.4", T0, out _));
        l.RegisterFailure("1.2.3.4", T0);
        Assert.True(l.IsLocked("1.2.3.4", T0.AddMinutes(1), out var rem));
        Assert.True(rem > TimeSpan.Zero);
        Assert.False(l.IsLocked("1.2.3.4", T0.AddMinutes(10).AddSeconds(1), out _));
    }

    [Fact]
    public void Lockout_WindowRestartsAfterExpiry_AndResetClears()
    {
        var l = new AuthLockout(2);
        l.RegisterFailure("a", T0);
        l.RegisterFailure("a", T0.AddMinutes(6)); // 新窗口，从 1 开始
        Assert.False(l.IsLocked("a", T0.AddMinutes(6), out _));
        l.RegisterFailure("a", T0.AddMinutes(6));
        Assert.True(l.IsLocked("a", T0.AddMinutes(6), out _));
        l.Reset("a");
        Assert.False(l.IsLocked("a", T0.AddMinutes(6), out _));
        l.RegisterFailure("b", T0);
        l.Sweep(T0.AddHours(1));
        Assert.Equal(0, l.Count);
    }

    [Fact]
    public void SecureEquals_Basics()
    {
        Assert.True(AuthLockout.SecureEquals("abc", "abc"));
        Assert.False(AuthLockout.SecureEquals("abc", "abd"));
        Assert.False(AuthLockout.SecureEquals("abc", "abcd"));
        Assert.True(AuthLockout.SecureEquals("", ""));
    }

    [Theory]
    [InlineData("alpha", true)]
    [InlineData("srv-01", true)]
    [InlineData("-bad", false)]
    [InlineData("Upper", false)]
    [InlineData("has space", false)]
    [InlineData("", false)]
    [InlineData("a234567890123456789012345678901234", false)]
    public void ServerId_Rules(string id, bool ok) => Assert.Equal(ok, PeerAuth.IsValidServerId(id));

    [Theory]
    [InlineData("short", false)]
    [InlineData("change_me_change_me_change_me_change_me", false)]
    [InlineData("0123456789abcdef0123456789abcdef \u201C", false)]
    [InlineData(Secret, true)]
    public void Secret_Strength(string s, bool ok) => Assert.Equal(ok, PeerAuth.IsAcceptableSecret(s, out _));

    [Fact]
    public void Request_SignVerify_RejectsWrongSecretSkewAndReplay()
    {
        var cache = new NonceCache();
        long ts = PeerAuth.ToUnix(T0);
        string nonce = PeerAuth.NewNonce();
        string mac = PeerAuth.SignRequest(Secret, "alpha", "bravo", ts, nonce);

        Assert.False(PeerAuth.VerifyRequest(Secret + "x", "alpha", "bravo", ts, nonce, mac, T0, cache, out string e1));
        Assert.Equal("bad_mac", e1);
        Assert.False(PeerAuth.VerifyRequest(Secret, "alpha", "charlie", ts, nonce, mac, T0, cache, out _));
        Assert.False(PeerAuth.VerifyRequest(Secret, "alpha", "bravo", ts, nonce, mac, T0.AddMinutes(2), cache, out string e2));
        Assert.Equal("clock_skew", e2);

        Assert.True(PeerAuth.VerifyRequest(Secret, "alpha", "bravo", ts, nonce, mac, T0, cache, out _));
        Assert.False(PeerAuth.VerifyRequest(Secret, "alpha", "bravo", ts, nonce, mac, T0.AddSeconds(1), cache, out string e3));
        Assert.Equal("replay", e3);
    }

    [Fact]
    public void Request_BadMacDoesNotPoisonReplayCache()
    {
        var cache = new NonceCache();
        long ts = PeerAuth.ToUnix(T0);
        string nonce = PeerAuth.NewNonce();
        Assert.False(PeerAuth.VerifyRequest(Secret, "alpha", "bravo", ts, nonce, new string('0', 64), T0, cache, out _));
        Assert.Equal(0, cache.Count);
        string mac = PeerAuth.SignRequest(Secret, "alpha", "bravo", ts, nonce);
        Assert.True(PeerAuth.VerifyRequest(Secret, "alpha", "bravo", ts, nonce, mac, T0, cache, out _));
    }

    [Fact]
    public void Response_BoundToNonceAndAccept()
    {
        string nonce = PeerAuth.NewNonce();
        string acc = WsFraming.ComputeAccept("dGhlIHNhbXBsZSBub25jZQ==");
        string mac = PeerAuth.SignResponse(Secret, "bravo", "alpha", nonce, acc);
        Assert.True(PeerAuth.VerifyResponse(Secret, "bravo", "alpha", nonce, acc, mac));
        Assert.True(PeerAuth.VerifyResponse(Secret, "bravo", "alpha", nonce, acc, mac.ToUpperInvariant()));
        Assert.False(PeerAuth.VerifyResponse(Secret, "bravo", "alpha", PeerAuth.NewNonce(), acc, mac));
        Assert.False(PeerAuth.VerifyResponse(Secret, "alpha", "bravo", nonce, acc, mac));
        Assert.False(PeerAuth.VerifyResponse(Secret, "bravo", "alpha", nonce, acc, null));
    }

    [Fact]
    public void NonceCache_IsBoundedAndExpires()
    {
        var c = new NonceCache(capacity: 2);
        Assert.True(c.TryAdd("a", T0));
        Assert.True(c.TryAdd("b", T0));
        Assert.True(c.TryAdd("c", T0));
        Assert.Equal(2, c.Count);
        Assert.True(c.TryAdd("a", T0)); // 被挤出后可再加
        Assert.True(c.TryAdd("x", T0.AddMinutes(5)));
        Assert.Equal(1, c.Count);
    }
}
