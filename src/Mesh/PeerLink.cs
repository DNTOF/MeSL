// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/SLDataAPI)
// Copyright (C) 2026 DNT_OF (https://github.com/DNTOF/MeSL)
// 改编自 SLDataAPI Voice/VoiceService.cs（VoiceClient：主线程轮询、握手超时、Poll 写守卫、不可写判死、帧长上限）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MeSL.Core;

namespace MeSL.Mesh;

/// <summary>
/// 一条节点间 TCP 链路（RFC 6455 帧 + MeSL 握手）。全部方法只在主线程 MEC 协程里调用。
/// 发送绝不允许阻塞主线程：先 Poll 可写再 Send；对端持续不收数据超过 UnwritableDropSec 判死。
/// </summary>
public sealed class PeerLink : IDisposable
{
    public const string Path = "/mesl/v0";
    private const int BufferSize = 64 * 1024;
    private const int MaxPayload = 16 * 1024;
    private const int MaxHeaderBytes = 8 * 1024;
    private const float HandshakeTimeoutSec = 10f;
    private const float UnwritableDropSec = 3f;
    private const float PingIntervalSec = 15f;
    private const float IdleTimeoutSec = 45f;
    private const int MaxFramesPerPump = 256;

    private enum State { Connecting, AwaitRequest, AwaitResponse, Open, Closed }

    private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

    private readonly MeshLinkService.Context _ctx;
    private readonly Socket _socket;
    private readonly byte[] _in = new byte[BufferSize];
    private int _inLen;
    private State _state;
    private readonly float _createdAt;
    private float _lastRecv;
    private float _lastPing;
    private float _unwritableSince = -1f;
    private IAsyncResult? _connectAr;
    private string _nonce = "";
    private string _wsKey = "";

    public bool IsDialer { get; }
    public string RemoteIp { get; private set; } = "?";
    /// <summary>对端 server_id：拨号方创建时已知；接受方鉴权通过后才填。</summary>
    public string? PeerId { get; private set; }
    public MeshHello? RemoteHello { get; private set; }
    public string CloseReason { get; private set; } = "";
    public TokenBucket? VoiceBucket { get; set; }

    private int[] _channelMap = Array.Empty<int>();
    private readonly HashSet<string> _remoteChannels = new(StringComparer.Ordinal);

    /// <summary>握手完成且已收到对端 HELLO。</summary>
    public bool IsReady => _state == State.Open && RemoteHello != null;
    public bool IsClosed => _state == State.Closed;
    /// <summary>尚未完成握手 + HELLO 交换（计入未握手池，受 HandshakeTimeoutSec 约束）。</summary>
    public bool IsPending => !IsReady && _state != State.Closed;

    public bool AnnouncedChannel(string name) => _remoteChannels.Contains(name);

    /// <summary>对端线路频道下标 → 本地频道下标（-1 = 本地没有该频道）。</summary>
    public int MapChannel(byte wireIndex) => wireIndex < _channelMap.Length ? _channelMap[wireIndex] : -1;

    private PeerLink(MeshLinkService.Context ctx, Socket socket, bool dialer, float now)
    {
        _ctx = ctx;
        _socket = socket;
        IsDialer = dialer;
        _createdAt = now;
        _lastRecv = now;
        _lastPing = now;
        _socket.NoDelay = true;
        _socket.ReceiveTimeout = 1;
        _socket.SendTimeout = 250;
    }

    public static PeerLink Accept(MeshLinkService.Context ctx, Socket socket, float now)
    {
        var link = new PeerLink(ctx, socket, false, now) { _state = State.AwaitRequest };
        try { link.RemoteIp = ((IPEndPoint)socket.RemoteEndPoint).Address.ToString(); } catch { /* 忽略 */ }
        return link;
    }

    public static PeerLink Dial(MeshLinkService.Context ctx, PeerConfig peer, IPAddress address, float now)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        var link = new PeerLink(ctx, socket, true, now)
        {
            _state = State.Connecting,
            PeerId = peer.ServerId,
            RemoteIp = address.ToString(),
        };
        try { link._connectAr = socket.BeginConnect(new IPEndPoint(address, peer.Port), null, null); }
        catch (Exception ex) { link.Close("connect: " + ex.Message); }
        return link;
    }

    public void Pump(float now)
    {
        if (_state == State.Closed) return;
        try
        {
            if (_state == State.Connecting)
            {
                if (!_connectAr!.IsCompleted)
                {
                    if (now - _createdAt > HandshakeTimeoutSec) Close("connect timeout");
                    return;
                }
                _socket.EndConnect(_connectAr);
                SendRequest();
                _state = State.AwaitResponse;
                return;
            }

            if (IsPending && now - _createdAt > HandshakeTimeoutSec)
            {
                Close("handshake timeout");
                return;
            }

            if (!ReadAvailable(now)) return;

            if (_state == State.AwaitRequest || _state == State.AwaitResponse)
            {
                int end = WsFraming.FindHeaderEnd(_in, _inLen);
                if (end < 0)
                {
                    if (_inLen > MaxHeaderBytes) Close("header too large");
                    return;
                }
                string header = Encoding.ASCII.GetString(_in, 0, end);
                // 握手头之后可能紧跟对端的第一帧（HELLO），保留剩余字节
                Buffer.BlockCopy(_in, end, _in, 0, _inLen - end);
                _inLen -= end;
                if (_state == State.AwaitRequest) HandleRequest(header, now);
                else HandleResponse(header);
                if (_state != State.Open) return;
            }

            if (_state == State.Open)
            {
                ProcessFrames();
                if (now - _lastRecv > IdleTimeoutSec) { Close("idle timeout"); return; }
                if (now - _lastPing > PingIntervalSec)
                {
                    _lastPing = now;
                    SendFrame(WsFraming.OpPing, Array.Empty<byte>(), 0);
                }
            }
        }
        catch (SocketException ex) { Close("socket: " + ex.SocketErrorCode); }
        catch (IOException ex) { Close("io: " + ex.Message); }
        catch (ObjectDisposedException) { Close("disposed"); }
    }

    // ────────────── 读 ──────────────

    private bool ReadAvailable(float now)
    {
        while (_socket.Available > 0 && _inLen < _in.Length)
        {
            int n = _socket.Receive(_in, _inLen, _in.Length - _inLen, SocketFlags.None);
            if (n <= 0) { Close("remote closed"); return false; }
            _inLen += n;
            _lastRecv = now;
        }
        // 可读但无数据 = 对端已关闭（FIN）
        if (_socket.Available == 0 && _socket.Poll(0, SelectMode.SelectRead))
        {
            Close("remote closed");
            return false;
        }
        return true;
    }

    private void ProcessFrames()
    {
        for (int i = 0; i < MaxFramesPerPump && _state == State.Open; i++)
        {
            // 接受方读取的是拨号方（客户端）发来的帧，按 RFC 必须带掩码
            WsParseResult r = WsFraming.TryParse(_in, ref _inLen, MaxPayload, requireMasked: !IsDialer, out int op, out byte[] payload);
            if (r == WsParseResult.NeedMore) return;
            if (r == WsParseResult.Violation) { Close("frame violation"); return; }

            switch (op)
            {
                case WsFraming.OpClose:
                    Close("remote close frame");
                    return;
                case WsFraming.OpPing:
                    SendFrame(WsFraming.OpPong, payload, payload.Length);
                    break;
                case WsFraming.OpPong:
                    break;
                case WsFraming.OpBinary:
                    OnBinary(payload);
                    break;
                default:
                    Close("unexpected opcode " + op);
                    return;
            }
        }
    }

    private void OnBinary(byte[] payload)
    {
        if (payload.Length == 0) { Close("empty message"); return; }
        if (RemoteHello == null)
        {
            if (!MeshCodec.TryDecodeHello(payload, out MeshHello hello) || hello.ServerId != PeerId)
            {
                Close("bad hello");
                return;
            }
            if (hello.ProtocolVersion != MeshCodec.ProtocolVersion)
            {
                SendBinary(MeshCodec.EncodeBye("unsupported protocol version"));
                Close("protocol version " + hello.ProtocolVersion);
                return;
            }
            RemoteHello = hello;
            _channelMap = _ctx.Channels.MapPeerChannels(hello.Channels);
            _remoteChannels.Clear();
            foreach (string c in hello.Channels) _remoteChannels.Add(c);
            _ctx.OnReady(this);
            return;
        }
        if (payload[0] == MeshMessageType.Hello) { Close("duplicate hello"); return; }
        _ctx.OnMessage(this, payload);
    }

    // ────────────── 握手：接受方 ──────────────

    private void HandleRequest(string header, float now)
    {
        string[] first = header.Split('\n')[0].Trim().Split(' ');
        if (first.Length < 2 || first[0] != "GET" || !first[1].Equals(Path, StringComparison.Ordinal))
        {
            Reject("404 Not Found", "not found", countFailure: false);
            return;
        }

        DateTime utc = DateTime.UtcNow;
        if (_ctx.Lockout.IsLocked(RemoteIp, utc, out TimeSpan remaining))
        {
            Reject("429 Too Many Requests", $"locked {Math.Ceiling(remaining.TotalSeconds)}s", countFailure: false);
            return;
        }

        string dialerId = WsFraming.GetHeader(header, "X-MeSL-Server") ?? "";
        string target = WsFraming.GetHeader(header, "X-MeSL-Target") ?? "";
        string nonce = WsFraming.GetHeader(header, "X-MeSL-Nonce") ?? "";
        string mac = WsFraming.GetHeader(header, "X-MeSL-Auth") ?? "";
        string? wsKey = WsFraming.GetHeader(header, "Sec-WebSocket-Key");
        string? version = WsFraming.GetHeader(header, "Sec-WebSocket-Version");
        long.TryParse(WsFraming.GetHeader(header, "X-MeSL-Ts"), out long ts);

        PeerConfig? peer = _ctx.FindPeer(dialerId);
        if (peer == null || target != _ctx.LocalId)
        {
            Reject("401 Unauthorized", "unknown peer or target", countFailure: true);
            return;
        }
        if (!PeerAuth.VerifyRequest(peer.Secret, dialerId, _ctx.LocalId, ts, nonce, mac, utc, _ctx.Nonces, out string err))
        {
            Reject("401 Unauthorized", err, countFailure: true);
            return;
        }
        if (IPAddress.TryParse(peer.Host, out IPAddress pinned) && !AddressEquals(pinned, RemoteIp))
        {
            Reject("403 Forbidden", "source address not pinned peer host", countFailure: true);
            return;
        }
        if (!MeshPolicy.ShouldDial(dialerId, _ctx.LocalId))
        {
            Reject("409 Conflict", "wrong dial direction", countFailure: false);
            return;
        }
        if (string.IsNullOrEmpty(wsKey) || version != "13")
        {
            Reject("400 Bad Request", "websocket upgrade required", countFailure: false);
            return;
        }

        _ctx.Lockout.Reset(RemoteIp);
        PeerId = dialerId;
        string accept = WsFraming.ComputeAccept(wsKey!);
        string response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n" +
            $"X-MeSL-Server: {_ctx.LocalId}\r\n" +
            $"X-MeSL-Auth: {PeerAuth.SignResponse(peer.Secret, _ctx.LocalId, dialerId, nonce, accept)}\r\n" +
            "\r\n";
        if (!SendRaw(Encoding.ASCII.GetBytes(response))) return;
        _state = State.Open;
        _lastRecv = now;
        _ctx.OnAuthenticated(this);
        SendHello();
    }

    private void Reject(string status, string reason, bool countFailure)
    {
        if (countFailure) _ctx.Lockout.RegisterFailure(RemoteIp, DateTime.UtcNow);
        Log.RateLimitedWarn("reject-" + RemoteIp, $"[MeSL] 拒绝来自 {RemoteIp} 的链路: {status} ({reason})");
        SendRaw(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        Close(status);
    }

    // ────────────── 握手：拨号方 ──────────────

    private void SendRequest()
    {
        PeerConfig peer = _ctx.FindPeer(PeerId!)!;
        byte[] keyBytes = new byte[16];
        Rng.GetBytes(keyBytes);
        _wsKey = Convert.ToBase64String(keyBytes);
        _nonce = PeerAuth.NewNonce();
        long ts = PeerAuth.ToUnix(DateTime.UtcNow);
        string request =
            $"GET {Path} HTTP/1.1\r\n" +
            $"Host: {peer.Host}:{peer.Port}\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Key: {_wsKey}\r\n" +
            "Sec-WebSocket-Version: 13\r\n" +
            $"X-MeSL-Server: {_ctx.LocalId}\r\n" +
            $"X-MeSL-Target: {peer.ServerId}\r\n" +
            $"X-MeSL-Ts: {ts}\r\n" +
            $"X-MeSL-Nonce: {_nonce}\r\n" +
            $"X-MeSL-Auth: {PeerAuth.SignRequest(peer.Secret, _ctx.LocalId, peer.ServerId, ts, _nonce)}\r\n" +
            "\r\n";
        SendRaw(Encoding.ASCII.GetBytes(request));
    }

    private void HandleResponse(string header)
    {
        string statusLine = header.Split('\n')[0].Trim();
        string[] parts = statusLine.Split(' ');
        if (parts.Length < 2 || parts[1] != "101")
        {
            Close("rejected: " + statusLine);
            return;
        }
        PeerConfig peer = _ctx.FindPeer(PeerId!)!;
        string? accept = WsFraming.GetHeader(header, "Sec-WebSocket-Accept");
        if (accept != WsFraming.ComputeAccept(_wsKey))
        {
            Close("bad Sec-WebSocket-Accept");
            return;
        }
        if (WsFraming.GetHeader(header, "X-MeSL-Server") != peer.ServerId ||
            !PeerAuth.VerifyResponse(peer.Secret, peer.ServerId, _ctx.LocalId, _nonce, accept!, WsFraming.GetHeader(header, "X-MeSL-Auth")))
        {
            Log.Warn($"[MeSL] 节点 {peer.ServerId}（{RemoteIp}）应答鉴权失败：密钥不一致或中间人");
            Close("acceptor auth failed");
            return;
        }
        _state = State.Open;
        _ctx.OnAuthenticated(this);
        SendHello();
    }

    private void SendHello()
    {
        var hello = new MeshHello
        {
            ProtocolVersion = MeshCodec.ProtocolVersion,
            ServerId = _ctx.LocalId,
            Channels = new List<string>(_ctx.Channels.MeshChannels),
            MaxOpusBytes = MeshCodec.MaxOpusBytes,
        };
        SendBinary(MeshCodec.EncodeHello(hello));
    }

    // ────────────── 写 ──────────────

    public void SendBinary(byte[] payload)
    {
        if (_state != State.Open) return;
        SendFrame(WsFraming.OpBinary, payload, payload.Length);
    }

    private void SendFrame(int opcode, byte[] payload, int length)
    {
        byte[]? mask = null;
        if (IsDialer)
        {
            mask = new byte[4];
            Rng.GetBytes(mask);
        }
        SendRaw(WsFraming.Encode(opcode, payload, length, mask));
    }

    /// <summary>
    /// 发送前守卫：TCP 发送缓冲满时本帧直接丢弃（实时语音允许丢帧），持续不可写超过 UnwritableDropSec 判死。
    /// </summary>
    private bool SendRaw(byte[] data)
    {
        if (_state == State.Closed) return false;
        try
        {
            if (!_socket.Poll(0, SelectMode.SelectWrite))
            {
                float now = UnityEngine.Time.time;
                if (_unwritableSince < 0f) _unwritableSince = now;
                else if (now - _unwritableSince > UnwritableDropSec) Close("peer not reading");
                return false;
            }
            _unwritableSince = -1f;
            _socket.Send(data);
            return true;
        }
        catch (Exception ex)
        {
            Close("send: " + ex.Message);
            return false;
        }
    }

    public void Close(string reason)
    {
        if (_state == State.Closed) return;
        if (_state == State.Open)
        {
            try
            {
                if (_socket.Poll(0, SelectMode.SelectWrite))
                    _socket.Send(WsFraming.Encode(WsFraming.OpClose, Array.Empty<byte>(), IsDialer ? new byte[4] : null));
            }
            catch { /* 忽略 */ }
        }
        _state = State.Closed;
        CloseReason = reason;
        try { _socket.Close(); } catch { /* 忽略 */ }
    }

    public void Dispose() => Close("disposed");

    private static bool AddressEquals(IPAddress pinned, string remote)
    {
        if (!IPAddress.TryParse(remote, out IPAddress r)) return false;
        if (r.IsIPv4MappedToIPv6) r = r.MapToIPv4();
        if (pinned.IsIPv4MappedToIPv6) pinned = pinned.MapToIPv4();
        return pinned.Equals(r);
    }
}
