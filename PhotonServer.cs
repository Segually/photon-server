using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace PhotonServer
{
    // Per-connected-peer reliability + identity state. Implements IPlayer so a
    // peer that has not yet entered a room (e.g. during the RoomCreating hook)
    // can still be surfaced to plugins as the acting player.
    public sealed class Peer : Api.IPlayer
    {
        public IPEndPoint EndPoint;
        public UdpClient  Socket;      // socket this peer is connected on
        public ushort     PeerId;
        public int        Challenge;

        // Outgoing reliable sequence numbers, per channel.
        private readonly Dictionary<byte, int> _outSeq = new();
        public int NextOutSeq(byte channel)
        {
            _outSeq.TryGetValue(channel, out int cur);
            cur++;
            _outSeq[channel] = cur;
            return cur;
        }

        public bool InitDone;
        public bool Authenticated;
        // Serialization protocol: detected from the first parameter-bearing message.
        public bool V18;
        public bool ProtocolKnown;
        public readonly DiffieHellman Dh = new();

        // Master vs game-server role for this connection. A fresh connection is a
        // "master" session; after a create/join redirect the client reconnects and
        // authenticates with the issued token, which flips it into game mode.
        public string? SessionToken;
        public bool    GameMode;
        public string? PendingRoom;
        public bool    PendingCreate;
        public long    LastRecvMs;   // for idle-timeout sweeps

        // Room membership.
        public Room?   Room;
        public int     ActorNr;
        public string? UserId;
        public readonly Dictionary<object, object?> PlayerProperties = new();

        // Inbound fragment reassembly, keyed by the fragment start sequence.
        public sealed class FragmentAssembly
        {
            public byte[] Buffer = Array.Empty<byte>();
            public int    Total;
            public int    Count;
            public int    Received;
            public byte   Channel;
        }
        public readonly Dictionary<int, FragmentAssembly> Fragments = new();

        // Inbound reliable commands are delivered at-least-once (client retransmits
        // until ACKed). Track processed (channel, seq) so each is handled exactly once.
        private readonly HashSet<long> _processedReliable = new();
        public bool MarkProcessed(byte channel, int seq)
        {
            long key = ((long)channel << 32) | (uint)seq;
            return _processedReliable.Add(key); // false if already present
        }

        // Outbound reliable commands awaiting ACK, keyed by (channel, seq).
        public sealed class Unacked
        {
            public byte[] CommandBytes = null!;
            public long   FirstSentMs;
            public long   LastSentMs;
            public int    Retries;
        }
        public readonly Dictionary<long, Unacked> UnackedCommands = new();

        public void OnAckReceived(byte channel, int seq)
        {
            long key = ((long)channel << 32) | (uint)seq;
            UnackedCommands.Remove(key);
        }

        public Peer(IPEndPoint ep, UdpClient socket, ushort id, int challenge)
        {
            EndPoint = ep; Socket = socket; PeerId = id; Challenge = challenge;
        }

        // ── IPlayer (plugin-facing read-only view of the acting connection) ──
        int     Api.IPlayer.ActorNr    => ActorNr;
        string? Api.IPlayer.UserId     => UserId;
        bool    Api.IPlayer.IsInactive => false;
        string? Api.IPlayer.NickName   => PlayerProperties.TryGetValue((byte)255, out var n) ? n as string : null;
        string? Api.IPlayer.Address    => EndPoint?.ToString();
        bool    Api.IPlayer.IsV18      => V18;
        System.Collections.Generic.IReadOnlyDictionary<object, object?> Api.IPlayer.Properties => PlayerProperties;
    }

    public sealed partial class PhotonServer
    {
        private readonly ServerConfig _config;
        private readonly int[] _ports;
        private readonly List<UdpClient> _sockets = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Dictionary<IPEndPoint, Peer> _peers = new();
        private readonly Dictionary<string, Room> _rooms = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
        private readonly object _sync = new();
        private ushort _nextPeerId = 0xC300;

        // Address advertised to clients as the "game server" for create/join
        // redirects. Same host as the master since we run combined on one port.
        private readonly string _publicAddress;

        // A session survives the master→game reconnect, carrying the pending room.
        private sealed class Session
        {
            public string? PendingRoom;
            public bool    PendingCreate;
            public long    CreatedMs;
            public string? UserId;
        }

        // Remove a peer's session unless a redirect is still pending on the master
        // connection (the client is about to reconnect with this token).
        private void CleanupSession(Peer peer)
        {
            if (peer.SessionToken == null) return;
            if (!peer.GameMode &&
                _sessions.TryGetValue(peer.SessionToken, out var s) && s.PendingRoom != null)
                return; // keep alive for the imminent game-server reconnect
            _sessions.Remove(peer.SessionToken);
        }

        // Retransmission tuning.
        private const int RetransmitMs = 200;
        // A reliable command that stays unACKed this many times AND for longer
        // than UnackedDropMs means the client is gone (Photon does the same:
        // sentCountAllowance + disconnect timeout).
        private const int MaxRetries     = 7;
        private const int UnackedDropMs  = 6000;

        // The server always sends packets with peer id 0x0000 in the header
        // (observed on the wire).
        private const ushort ServerHeaderPeerId = 0x0000;

        public PhotonServer(ServerConfig config)
        {
            _config = config;
            _hooks = new Api.HookRegistry((ctx, ex) => Log($"[Plugins] {ctx} threw: {ex.Message}"));
            _ports = config.Ports;
            // The game-server port the client redirects to is fixed by the SDK's
            // ServerPortOverrides (UDP GameServer = 27002), so advertise that.
            int gamePort = _ports.Contains(27002) ? 27002 : _ports[0];
            _publicAddress = $"{config.PublicHost}:{gamePort}";
            foreach (int p in _ports)
                _sockets.Add(new UdpClient(new IPEndPoint(IPAddress.Any, p)));
        }

        public void Run()
        {
            Log($"[Server] Photon UDP server listening on ports: {string.Join(", ", _ports)}");
            Log($"[Server] game-server redirect address (config PublicHost): {_publicAddress}");
            if (_config.PublicHost is "127.0.0.1" or "localhost" or "0.0.0.0")
                Log("[Server] NOTE: PublicHost is loopback — set it to your public IP/DNS in " +
                    "config.json so remote clients can reach the game server after redirect.");
            StartRetransmitThread();
            LoadPlugins();
            StartConsoleThread();

            // One receive loop per bound socket; all funnel into the same handler
            // under the global lock.
            var threads = new List<Thread>();
            foreach (var sock in _sockets)
            {
                var s = sock;
                var t = new Thread(() => ReceiveLoop(s)) { IsBackground = true };
                t.Start();
                threads.Add(t);
            }
            foreach (var t in threads) t.Join();
        }

        private void ReceiveLoop(UdpClient sock)
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                byte[] data;
                IPEndPoint from;
                try
                {
                    data = sock.Receive(ref remote);
                    from = new IPEndPoint(remote.Address, remote.Port);
                }
                catch (SocketException) { continue; }
                try { lock (_sync) HandleDatagram(data, from, sock); }
                catch (Exception e) { Log($"[Server] handler error: {e}"); }
            }
        }

        private uint Now   => (uint)_clock.ElapsedMilliseconds;
        private long NowMs => _clock.ElapsedMilliseconds;

        // ── Outbound reliable retransmission ─────────────────────────────────
        private void StartRetransmitThread()
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(50);
                    lock (_sync)
                    {
                        long now = NowMs;

                        // Expire redirect sessions the client never came back for.
                        foreach (var kv in _sessions.Where(s => s.Value.PendingRoom != null &&
                                     now - s.Value.CreatedMs > 30000).ToList())
                            _sessions.Remove(kv.Key);

                        SweepInactiveActors(now);

                        foreach (var peer in _peers.Values.ToList())
                        {
                            // Idle-timeout sweep: drop peers that have gone silent
                            // (crashed clients, lost network, killed apps).
                            if (_config.PeerTimeoutMs > 0 && now - peer.LastRecvMs > _config.PeerTimeoutMs)
                            {
                                DropPeer(peer, $"idle timeout ({now - peer.LastRecvMs}ms)");
                                continue;
                            }

                            bool dead = false;
                            foreach (var kv in peer.UnackedCommands.ToList())
                            {
                                var u = kv.Value;
                                if (u.Retries >= MaxRetries && now - u.FirstSentMs > UnackedDropMs)
                                {
                                    dead = true;
                                    break;
                                }
                                // Back off a little so a dead peer isn't flooded, but
                                // keep retrying until it is declared dead.
                                int interval = Math.Min(RetransmitMs << Math.Min(u.Retries / 3, 2), 800);
                                if (now - u.LastSentMs < interval) continue;
                                u.Retries++;
                                u.LastSentMs = now;
                                SendPacket(peer, new[] { u.CommandBytes });
                            }
                            if (dead)
                                DropPeer(peer, "reliable command never ACKed (peer unreachable)");
                        }
                    }
                }
            }) { IsBackground = true, Name = "Retransmit" };
            t.Start();
        }

        // Single teardown path for a peer: client Disconnect, idle timeout and
        // unreachable-peer detection all go through here. Caller holds _sync.
        private void DropPeer(Peer peer, string? reason)
        {
            // Ignore if already removed (e.g. timeout racing a Disconnect).
            if (!_peers.TryGetValue(peer.EndPoint, out var cur) || !ReferenceEquals(cur, peer)) return;
            if (reason != null) Log($"[{peer.EndPoint}] {reason} — dropping");
            peer.UnackedCommands.Clear();
            LeaveRoom(peer, willComeBack: true);
            _peers.Remove(peer.EndPoint);
            CleanupSession(peer);
            _hooks.RaisePlayerDisconnected(peer);
        }

        // ── Datagram dispatch ────────────────────────────────────────────────
        private void HandleDatagram(byte[] data, IPEndPoint from, UdpClient sock)
        {
            // 13-byte all-0x7D ping/echo → reflect verbatim (region latency probe).
            if (IsPingEcho(data))
            {
                sock.Send(data, data.Length, from);
                return;
            }

            var pkt = Wire.Parse(data, data.Length);
            if (pkt == null) { Log($"[{from}] unparseable ({data.Length}b): {Hex(data, data.Length)}"); return; }

            _peers.TryGetValue(from, out Peer? peer);
            if (peer != null) peer.LastRecvMs = NowMs;

            foreach (var cmd in pkt.Commands)
            {
                // Every reliable command (except Connect, handled specially)
                // must be acknowledged, including control commands like type 12.
                if (peer != null && cmd.IsReliable && cmd.Type != CommandType.Connect)
                    AckIfReliable(peer, pkt, cmd);

                switch (cmd.Type)
                {
                    case CommandType.Connect:
                        peer = HandleConnect(pkt, cmd, from, sock);
                        break;
                    case CommandType.Ack:
                        if (peer != null && cmd.Payload.Length >= 4)
                            peer.OnAckReceived(cmd.Channel, Wire.ReadI32(cmd.Payload, 0));
                        break;
                    case CommandType.Disconnect:
                        Log($"[{from}] DISCONNECT");
                        if (peer != null)
                        {
                            // A clean disconnect may still leave an inactive actor
                            // if the room has a PlayerTtl (the client can rejoin).
                            DropPeer(peer, null);
                        }
                        break;
                    case CommandType.Ping:
                        break;
                    case CommandType.SendReliable:
                    case CommandType.SendUnreliable:
                        if (peer == null) { Log($"[{from}] data from unknown peer, ignoring"); break; }
                        // Skip already-processed reliable retransmits (already ACKed above).
                        if (cmd.IsReliable && !peer.MarkProcessed(cmd.Channel, cmd.ReliableSeq))
                            break;
                        HandleMessage(peer, cmd);
                        break;
                    case CommandType.SendFragment:
                        if (peer == null) break;
                        if (!peer.MarkProcessed(cmd.Channel, cmd.ReliableSeq)) break; // dup fragment
                        HandleFragment(peer, cmd);
                        break;
                    default:
                        LogV($"[{from}] control command {(byte)cmd.Type} ch{cmd.Channel} seq{cmd.ReliableSeq} (acked)");
                        break;
                }
            }
        }

        // ── Connection handshake ─────────────────────────────────────────────
        private Peer HandleConnect(PhotonPacket pkt, PhotonCommand cmd, IPEndPoint from, UdpClient sock)
        {
            // A fresh Connect from an endpoint we still track means the old
            // connection died without us noticing (app killed + restarted on the
            // same port). Tear it down so it doesn't linger as a ghost in a room.
            if (_peers.TryGetValue(from, out var stale))
                DropPeer(stale, "reconnect from same endpoint, replacing stale peer");

            ushort assignedId = _nextPeerId++;
            var peer = new Peer(from, sock, assignedId, pkt.Challenge) { LastRecvMs = NowMs };
            _peers[from] = peer;
            Log($"[{from}] CONNECT → assigned peerId=0x{assignedId:X4} challenge=0x{pkt.Challenge:X8}");

            // Reply: ACK(the connect) + VERIFY_CONNECT(with assigned peer id).
            var ack    = BuildAck(cmd.Channel, cmd.ReliableSeq, pkt.Timestamp);
            var verify = BuildVerifyConnect(peer, cmd);
            SendPacket(peer, new[] { ack, verify });
            return peer;
        }

        // ── Fragment reassembly ──────────────────────────────────────────────
        private void HandleFragment(Peer peer, PhotonCommand cmd)
        {
            if (!peer.Fragments.TryGetValue(cmd.FragStartSeq, out var asm))
            {
                if (cmd.FragTotalLength < 0 || cmd.FragTotalLength > 8 * 1024 * 1024)
                {
                    Log($"[{peer.EndPoint}] fragment with bad total length {cmd.FragTotalLength}");
                    return;
                }
                asm = new Peer.FragmentAssembly
                {
                    Buffer  = new byte[cmd.FragTotalLength],
                    Total   = cmd.FragTotalLength,
                    Count   = cmd.FragCount,
                    Channel = cmd.Channel,
                };
                peer.Fragments[cmd.FragStartSeq] = asm;
            }

            if (cmd.FragOffset >= 0 && cmd.FragOffset + cmd.Payload.Length <= asm.Buffer.Length)
            {
                Array.Copy(cmd.Payload, 0, asm.Buffer, cmd.FragOffset, cmd.Payload.Length);
                asm.Received++;
            }

            if (asm.Received >= asm.Count)
            {
                peer.Fragments.Remove(cmd.FragStartSeq);
                LogV($"[{peer.EndPoint}] fragment complete: {asm.Total}b in {asm.Count} parts");
                var synthetic = new PhotonCommand
                {
                    Type = CommandType.SendReliable,
                    Channel = asm.Channel,
                    Payload = asm.Buffer,
                    ReliableSeq = cmd.FragStartSeq,
                };
                HandleMessage(peer, synthetic);
            }
        }

        // ── Message (F3) handling ────────────────────────────────────────────
        private void HandleMessage(Peer peer, PhotonCommand cmd)
        {
            byte[] payload = cmd.Payload;
            if (payload.Length < 2 || payload[0] != Wire.Magic)
            {
                Log($"[{peer.EndPoint}] non-F3 data on ch{cmd.Channel}: {Hex(payload, payload.Length)}");
                return;
            }

            byte rawType = payload[1];
            var mtype = (MsgType)(rawType & 0x7F);
            bool enc  = (rawType & 0x80) != 0;

            // Init (F3 00) → InitResponse (F3 01 00).
            if (mtype == MsgType.Init)
            {
                Log($"[{peer.EndPoint}] INIT ({payload.Length}b) appId+version");
                peer.InitDone = true;
                var initResp = new byte[] { Wire.Magic, (byte)MsgType.InitResponse, 0x00 };
                SendReliableMessage(peer, cmd.Channel, initResp);
                return;
            }

            if (enc)
            {
                var key = peer.Dh.SharedKey;
                if (key == null) { Log($"[{peer.EndPoint}]   ENC but no key yet"); return; }
                byte[] plain;
                try { plain = PhotonCrypto.Decrypt(Slice(payload, 2), key); }
                catch (Exception e) { Log($"[{peer.EndPoint}]   DECRYPT FAILED: {e.Message}"); return; }

                // Reconstruct a plaintext F3 message: magic + (type without enc bit) + body.
                var full = new byte[2 + plain.Length];
                full[0] = Wire.Magic;
                full[1] = (byte)(rawType & 0x7F);
                Array.Copy(plain, 0, full, 2, plain.Length);
                try
                {
                    var dmsg = ParseMessageDetect(peer, full);
                    LogV($"[{peer.EndPoint}] IN* {dmsg}   (decrypted {plain.Length}b)");
                    if (dmsg != null) DispatchMessage(peer, cmd, dmsg);
                }
                catch (Exception e) { LogParseFail(peer, full, e); }
                return;
            }

            try
            {
                var msg = ParseMessageDetect(peer, payload);
                if (msg == null) { Log($"[{peer.EndPoint}] unparsed msg: {Hex(payload, payload.Length)}"); return; }
                LogV($"[{peer.EndPoint}] IN  {msg}");
                DispatchMessage(peer, cmd, msg);
            }
            catch (Exception e) { LogParseFail(peer, payload, e); }
        }

        // Log the exact bytes when a message fails to parse/dispatch, so any
        // remaining serialization gap is immediately diagnosable.
        private void LogParseFail(Peer peer, byte[] payload, Exception e)
        {
            string proto = peer.ProtocolKnown ? (peer.V18 ? "V18" : "V16") : "?";
            Log($"[{peer.EndPoint}] PARSE FAIL ({proto}) type=0x{(payload.Length > 1 ? payload[1] : 0):X2} " +
                $"{e.Message}  ({payload.Length}b) hex={Hex(payload, Math.Min(payload.Length, 160))}");
        }

        // Parse an F3 message, auto-detecting GpBinaryV16 vs V18 from the first
        // parameter-bearing message (the game uses V18, the probe uses V16).
        private PhotonMessage? ParseMessageDetect(Peer peer, byte[] payload)
        {
            if (peer.ProtocolKnown)
                return peer.V18 ? GpBinaryV18.ParseMessage(payload, out _) : GpBinary.ParseMessage(payload);

            // Prefer V18 only if it consumes the payload exactly; else fall back
            // to V16. Encrypted messages carry no body to detect from — skip.
            byte raw = payload.Length >= 2 ? payload[1] : (byte)0;
            if ((raw & 0x80) == 0 && (raw & 0x7F) != (byte)MsgType.Init)
            {
                try
                {
                    var m18 = GpBinaryV18.ParseMessage(payload, out int consumed);
                    if (m18 != null && consumed == payload.Length && m18.Parameters.Count > 0)
                    {
                        peer.V18 = true; peer.ProtocolKnown = true;
                        Log($"[{peer.EndPoint}] serialization protocol = GpBinaryV18");
                        return m18;
                    }
                }
                catch { /* not V18 */ }
            }

            var m16 = GpBinary.ParseMessage(payload);
            if (m16 != null && m16.Parameters.Count > 0)
            {
                peer.V18 = false; peer.ProtocolKnown = true;
                Log($"[{peer.EndPoint}] serialization protocol = GpBinaryV16");
            }
            return m16;
        }

        // Protocol-aware message builders (route to the peer's serializer).
        private static byte[] BuildOpResp(Peer p, byte op, short ret, object? dbg, Dictionary<byte, object?>? pr)
            => p.V18 ? GpBinaryV18.BuildOpResponse(op, ret, dbg, pr) : GpBinary.BuildOpResponse(op, ret, dbg, pr);
        private static byte[] BuildEvt(Peer p, byte ev, Dictionary<byte, object?>? pr)
            => p.V18 ? GpBinaryV18.BuildEvent(ev, pr) : GpBinary.BuildEvent(ev, pr);
        private static byte[] BuildIntResp(Peer p, byte op, short ret, Dictionary<byte, object?>? pr)
            => p.V18 ? GpBinaryV18.BuildInternalOpResponse(op, ret, pr) : GpBinary.BuildInternalOpResponse(op, ret, pr);

        private void DispatchMessage(Peer peer, PhotonCommand cmd, PhotonMessage msg)
        {
            switch (msg.Type)
            {
                case MsgType.InternalOpRequest:
                    HandleInternalOp(peer, cmd, msg);
                    break;
                case MsgType.OperationRequest:
                    HandleOperation(peer, cmd, msg);
                    break;
                default:
                    Log($"[{peer.EndPoint}]   (no handler for {msg.Type})");
                    break;
            }
        }

        private static byte[] Slice(byte[] b, int start)
        {
            var r = new byte[b.Length - start];
            Array.Copy(b, start, r, 0, r.Length);
            return r;
        }

        private void HandleInternalOp(Peer peer, PhotonCommand cmd, PhotonMessage msg)
        {
            if (msg.Code == 0) // ExchangeKeysForEncryption (Diffie-Hellman)
            {
                if (msg.Parameters.TryGetValue(1, out object? kv) && kv is byte[] clientPub && clientPub.Length == 96)
                {
                    peer.Dh.DeriveShared(clientPub);
                    Log($"[{peer.EndPoint}]   DH exchange — shared key derived ({peer.Dh.SharedKey!.Length}b)");
                    var resp = BuildIntResp(peer, 0, 0,
                        new Dictionary<byte, object?> { [1] = peer.Dh.PublicKey });
                    SendReliableMessage(peer, cmd.Channel, resp);
                }
                else
                {
                    Log($"[{peer.EndPoint}]   DH exchange — missing/short client key");
                }
                return;
            }
            Log($"[{peer.EndPoint}]   INTERNAL op={msg.Code} — no handler");
        }

        private void HandleOperation(Peer peer, PhotonCommand cmd, PhotonMessage msg)
        {
            switch (msg.Code)
            {
                case OpCode.Authenticate:
                {
                    // Optional AppId / AppVersion gating (only present on the first,
                    // master-side auth; the game-server re-auth carries only a token).
                    if (_config.RequireAppId != null &&
                        msg.Parameters.TryGetValue(Param.AppId, out var aid) && aid is string aids &&
                        aids != _config.RequireAppId)
                    {
                        SendReliableMessage(peer, cmd.Channel,
                            BuildOpResp(peer, OpCode.Authenticate, 32755, "Invalid AppId", null));
                        Log($"[{peer.EndPoint}]   AUTH rejected (AppId '{aids}')");
                        break;
                    }
                    if (_config.RequireAppVersion != null &&
                        msg.Parameters.TryGetValue(Param.AppVersion, out var av) && av is string avs &&
                        avs != _config.RequireAppVersion)
                    {
                        SendReliableMessage(peer, cmd.Channel,
                            BuildOpResp(peer, OpCode.Authenticate, 32755, "Invalid AppVersion", null));
                        Log($"[{peer.EndPoint}]   AUTH rejected (AppVersion '{avs}')");
                        break;
                    }

                    // A client-supplied UserId (param 225) enables rejoin across
                    // reconnects; otherwise assign a stable one.
                    string? clientUserId = msg.Parameters.TryGetValue(Param.UserId, out var uid) && uid is string uids && uids.Length > 0
                        ? uids : null;

                    // Plugin gate: blacklist / protocol-restriction mods can reject
                    // the connection here (IP, UserId and protocol are all known).
                    string? authAppId = msg.Parameters.TryGetValue(Param.AppId, out var aidv) && aidv is string aidvs ? aidvs : null;
                    string? authAppVer = msg.Parameters.TryGetValue(Param.AppVersion, out var avv) && avv is string avvs ? avvs : null;
                    var authCtx = new Api.AuthContext(peer.EndPoint.ToString(), clientUserId, peer.V18, authAppId, authAppVer);
                    _hooks.RaisePlayerAuthenticating(authCtx);
                    if (authCtx.Cancel)
                    {
                        SendReliableMessage(peer, cmd.Channel, BuildOpResp(peer, OpCode.Authenticate,
                            authCtx.ReturnCode, authCtx.Message ?? "Authentication rejected", null));
                        Log($"[{peer.EndPoint}]   AUTH rejected by plugin ({authCtx.Message})");
                        break;
                    }

                    peer.Authenticated = true;

                    // A token in [221] means the client is returning after a
                    // create/join redirect — resume that session in game mode.
                    string? inToken = msg.Parameters.TryGetValue(Param.Secret, out var sv) && sv is string ss ? ss : null;
                    if (inToken != null && _sessions.TryGetValue(inToken, out var sess) && sess.PendingRoom != null)
                    {
                        peer.SessionToken  = inToken;
                        peer.GameMode      = true;
                        peer.PendingRoom   = sess.PendingRoom;
                        peer.PendingCreate = sess.PendingCreate;
                        peer.UserId        = clientUserId ?? sess.UserId ?? Guid.NewGuid().ToString();
                        Log($"[{peer.EndPoint}]   AUTH ok (GAME mode, pending room '{sess.PendingRoom}', user {peer.UserId})");
                    }
                    else
                    {
                        peer.UserId       = clientUserId ?? Guid.NewGuid().ToString();
                        peer.SessionToken = Guid.NewGuid().ToString("N");
                        _sessions[peer.SessionToken] = new Session { CreatedMs = NowMs, UserId = peer.UserId };
                        Log($"[{peer.EndPoint}]   AUTH ok (MASTER mode, user {peer.UserId})");
                    }

                    var resp = BuildOpResp(peer, OpCode.Authenticate, 0, null,
                        new Dictionary<byte, object?>
                        {
                            [Param.Secret] = peer.SessionToken,
                            [Param.UserId] = peer.UserId,
                        });
                    SendReliableMessage(peer, cmd.Channel, resp);
                    break;
                }
                case OpCode.JoinLobby:
                {
                    var resp = BuildOpResp(peer, OpCode.JoinLobby, 0, null, null);
                    SendReliableMessage(peer, cmd.Channel, resp);
                    SendReliableMessage(peer, cmd.Channel, BuildGameListEvent(peer));
                    Log($"[{peer.EndPoint}]   JOIN LOBBY ok ({_rooms.Count} room(s) listed)");
                    _hooks.RaiseLobbyJoined(peer);
                    break;
                }
                case OpCode.CreateGame:  HandleCreateGame(peer, cmd, msg); break;
                case OpCode.JoinGame:    HandleJoinGame(peer, cmd, msg, createIfAbsent: false); break;
                case OpCode.JoinRandomGame: HandleJoinRandom(peer, cmd, msg); break;
                case OpCode.RaiseEvent:  HandleRaiseEvent(peer, cmd, msg); break;
                case OpCode.SetProperties: HandleSetProperties(peer, cmd, msg); break;
                case OpCode.Leave:       HandleLeaveOp(peer, cmd, msg); break;
                case OpCode.ChangeGroups:
                    // Interest groups — accept silently.
                    break;
                default:
                    Log($"[{peer.EndPoint}]   op {msg.Code} — no handler yet");
                    break;
            }
        }

        // ── Room operations ──────────────────────────────────────────────────

        // On the master, create/join don't enter a room — they hand the client a
        // game-server address (ourselves) plus its session token, and the client
        // reconnects there before actually entering.
        private bool RedirectIfMaster(Peer peer, PhotonCommand cmd, byte opCode, string roomName, bool isCreate)
        {
            if (peer.GameMode) return false;
            if (peer.SessionToken != null && _sessions.TryGetValue(peer.SessionToken, out var sess))
            {
                sess.PendingRoom   = roomName;
                sess.PendingCreate = isCreate;
            }
            var resp = BuildOpResp(peer, opCode, 0, null, new Dictionary<byte, object?>
            {
                [Param.Address] = _publicAddress,
                [Param.Secret]  = peer.SessionToken,
                [Param.GameId]  = roomName,
            });
            SendReliableMessage(peer, cmd.Channel, resp);
            Log($"[{peer.EndPoint}]   {(isCreate ? "CREATE" : "JOIN")} '{roomName}' → redirect to {_publicAddress}");
            return true;
        }

        private void HandleCreateGame(Peer peer, PhotonCommand cmd, PhotonMessage msg)
        {
            string name = msg.Parameters.TryGetValue(Param.GameId, out var g) && g is string s && s.Length > 0
                ? s : (peer.PendingRoom ?? Guid.NewGuid().ToString("N")[..8]);

            if (RedirectIfMaster(peer, cmd, OpCode.CreateGame, name, isCreate: true)) return;

            var room = new Room(name);
            ApplyRoomOptions(room, msg);

            // Plugin gate: naming-convention / property mods may reject the create
            // (with a client-facing error code) or rewrite the name / limits / props.
            var createCtx = new Api.RoomCreateContext(peer, name, room.MaxPlayers, room.IsOpen, room.IsVisible, room.Properties);
            _hooks.RaiseRoomCreating(createCtx);
            if (createCtx.Cancel)
            {
                SendReliableMessage(peer, cmd.Channel, BuildOpResp(peer, OpCode.CreateGame,
                    createCtx.ReturnCode, createCtx.Message ?? "Room rejected", null));
                Log($"[{peer.EndPoint}]   CREATE '{name}' rejected by plugin ({createCtx.Message})");
                return;
            }
            // Apply any rewrites, keeping the well-known props in sync so the
            // enter-room response and game list reflect the final values.
            name = createCtx.Name;
            room.Name       = name;
            room.MaxPlayers = createCtx.MaxPlayers;
            room.IsOpen     = createCtx.IsOpen;
            room.IsVisible  = createCtx.IsVisible;
            room.Properties[(byte)255] = room.MaxPlayers;
            room.Properties[(byte)253] = room.IsOpen;
            room.Properties[(byte)254] = room.IsVisible;

            if (_rooms.ContainsKey(name))
            {
                // Room exists → error (game already exists = 32766).
                SendReliableMessage(peer, cmd.Channel,
                    BuildOpResp(peer, OpCode.CreateGame, 32766, "Game already exists", null));
                Log($"[{peer.EndPoint}]   CREATE '{name}' FAILED (exists)");
                return;
            }
            if (_config.MaxRooms > 0 && _rooms.Count >= _config.MaxRooms)
            {
                SendReliableMessage(peer, cmd.Channel,
                    BuildOpResp(peer, OpCode.CreateGame, 32760, "Server room limit reached", null));
                Log($"[{peer.EndPoint}]   CREATE '{name}' FAILED (server at {_config.MaxRooms} rooms)");
                return;
            }

            // Reject creation if the requested MaxPlayers exceeds the server's
            // per-room cap.
            if (_config.MaxPeersPerRoom > 0 && room.MaxPlayers > _config.MaxPeersPerRoom)
            {
                SendReliableMessage(peer, cmd.Channel, BuildOpResp(peer, OpCode.CreateGame, 32762,
                    $"MaxPlayers {room.MaxPlayers} exceeds server limit {_config.MaxPeersPerRoom}", null));
                Log($"[{peer.EndPoint}]   CREATE '{name}' FAILED (maxPlayers {room.MaxPlayers} > cap {_config.MaxPeersPerRoom})");
                return;
            }

            _rooms[name] = room;
            EnterRoom(peer, room, msg, OpCode.CreateGame);
            _hooks.RaiseRoomCreated(room);
            Log($"[{peer.EndPoint}]   CREATE '{name}' ok (actor {peer.ActorNr}, master)");
        }

        private void HandleJoinGame(Peer peer, PhotonCommand cmd, PhotonMessage msg, bool createIfAbsent)
        {
            string name = msg.Parameters.TryGetValue(Param.GameId, out var g) && g is string s ? s : (peer.PendingRoom ?? "");

            if (RedirectIfMaster(peer, cmd, OpCode.JoinGame, name, isCreate: false)) return;

            // In game mode a create-turned-join (room already made by us) still enters.
            if (peer.PendingCreate) createIfAbsent = true;

            if (!_rooms.TryGetValue(name, out var room))
            {
                if (createIfAbsent)
                {
                    room = new Room(name);
                    ApplyRoomOptions(room, msg);
                    _rooms[name] = room;
                    _hooks.RaiseRoomCreated(room);
                }
                else
                {
                    // Game does not exist = 32758.
                    SendReliableMessage(peer, cmd.Channel,
                        BuildOpResp(peer, OpCode.JoinGame, 32758, "Game does not exist", null));
                    Log($"[{peer.EndPoint}]   JOIN '{name}' FAILED (missing)");
                    return;
                }
            }
            if (!room.IsOpen)
            {
                SendReliableMessage(peer, cmd.Channel,
                    BuildOpResp(peer, OpCode.JoinGame, 32764, "Game closed", null));
                return;
            }

            // Plugin gate: a join can be rejected, or waved past the capacity limit
            // (e.g. a whitelisted IP allowed into an otherwise-full room).
            var joinCtx = new Api.JoinContext(peer, room);
            _hooks.RaisePlayerJoining(joinCtx);
            if (joinCtx.Cancel)
            {
                SendReliableMessage(peer, cmd.Channel, BuildOpResp(peer, OpCode.JoinGame,
                    joinCtx.ReturnCode, joinCtx.Message ?? "Join rejected", null));
                Log($"[{peer.EndPoint}]   JOIN '{name}' rejected by plugin ({joinCtx.Message})");
                return;
            }
            if (!joinCtx.AllowOverCapacity && RoomIsFull(room))
            {
                SendReliableMessage(peer, cmd.Channel,
                    BuildOpResp(peer, OpCode.JoinGame, 32765, "Game full", null));
                Log($"[{peer.EndPoint}]   JOIN '{name}' FAILED (full)");
                return;
            }
            EnterRoom(peer, room, msg, OpCode.JoinGame);
            Log($"[{peer.EndPoint}]   JOIN '{name}' ok (actor {peer.ActorNr})");
        }

        private void HandleJoinRandom(Peer peer, PhotonCommand cmd, PhotonMessage msg)
        {
            // Master picks a room and redirects; the client then joins it by name
            // on the game server.
            if (!peer.GameMode)
            {
                var pick = _rooms.Values.FirstOrDefault(r => r.IsOpen && r.IsVisible && !RoomIsFull(r));
                if (pick == null)
                {
                    SendReliableMessage(peer, cmd.Channel,
                        BuildOpResp(peer, OpCode.JoinRandomGame, 32760, "No match found", null));
                    return;
                }
                RedirectIfMaster(peer, cmd, OpCode.JoinRandomGame, pick.Name, isCreate: false);
                return;
            }

            if (peer.PendingRoom != null && _rooms.TryGetValue(peer.PendingRoom, out var room))
            {
                EnterRoom(peer, room, msg, OpCode.JoinRandomGame);
                Log($"[{peer.EndPoint}]   JOIN RANDOM → '{room.Name}' (actor {peer.ActorNr})");
            }
        }

        // Add the peer to a room and send the enter-room response + broadcast join.
        private void EnterRoom(Peer peer, Room room, PhotonMessage msg, byte opCode)
        {
            // The joiner's own player properties come in param 249.
            if (msg.Parameters.TryGetValue(Param.PlayerProperties, out var pp) &&
                pp is Dictionary<object, object?> props)
            {
                peer.PlayerProperties.Clear();
                foreach (var kv in props) peer.PlayerProperties[kv.Key] = kv.Value;
            }

            // Rejoin: an inactive actor with the same UserId reclaims its slot and
            // actor number; otherwise a new actor is allocated.
            var existing = room.FindInactiveByUserId(peer.UserId);
            int actorNr;
            bool rejoin = existing != null;
            if (existing != null)
            {
                existing.IsInactive = false;
                existing.Peer = peer;
                actorNr = existing.ActorNr;
                room.RecomputeMaster();
            }
            else
            {
                actorNr = room.AddMember(peer, peer.UserId);
            }
            peer.Room = room;
            peer.ActorNr = actorNr;
            var self = room.Members[actorNr];
            self.Properties.Clear();
            foreach (var kv in peer.PlayerProperties) self.Properties[kv.Key] = kv.Value;

            // Build enter-room response: your actor number, all players' props, room props.
            var allPlayers = new Dictionary<object, object?>();
            foreach (var m in room.Members.Values)
            {
                var pd = new Dictionary<object, object?>();
                foreach (var kv in m.Properties) pd[kv.Key] = kv.Value;
                allPlayers[m.ActorNr] = pd;
            }

            // Room flag bits (param 191). Bit 2 is the "room entered" gate the
            // client checks before firing OnJoinedRoom/OnCreatedRoom; echo the
            // client's requested flags with that bit set.
            byte flags = msg.Parameters.TryGetValue(Param.RoomFlags, out var fv)
                ? Convert.ToByte(fv) : (byte)0;
            flags |= 0x04;

            // Game (room) properties, plus the well-known MasterClientId key (248):
            // the client reads room-property 248 into Room.MasterClientId
            // (RoomInfo.InternalCacheProperties) and IsMasterClient is
            // MasterClientId == LocalActorNr. Without it the first host has
            // MasterClientId=0, is NOT recognised as master, and the game's
            // master-only lobby setup never runs (character never spawns) until a
            // later handoff sets it via the Leave event's param 203.
            var gameProps = ToDict(room.Properties);
            gameProps[(byte)Param.MasterClientKey] = room.MasterClientActor;

            var respParams = new Dictionary<byte, object?>
            {
                [Param.ActorNr]          = actorNr,
                [Param.ActorList]        = room.ActorNumbers,
                [Param.PlayerProperties] = allPlayers,
                [Param.GameProperties]   = gameProps,
                [Param.RoomFlags]        = (int)flags,
            };
            SendReliableMessage(peer, 0, BuildOpResp(peer, opCode, 0, null, respParams));

            // Replay cached events (buffered Instantiates etc.) so the joiner
            // reconstructs existing room state.
            foreach (var ce in room.EventCache)
            {
                SendReliableMessage(peer, 0, BuildEvt(peer, ce.Code, new Dictionary<byte, object?>
                {
                    [Param.Data]    = ce.Data,
                    [Param.ActorNr] = ce.Sender,
                }));
            }
            if (room.EventCache.Count > 0)
                LogV($"[{peer.EndPoint}]   replayed {room.EventCache.Count} cached event(s)");

            // Notify existing members that an actor joined/rejoined (Event 255).
            BroadcastEvent(room, EvCode.Join, new Dictionary<byte, object?>
            {
                [Param.ActorNr]          = actorNr,
                [Param.ActorList]        = room.ActorNumbers,
                [Param.PlayerProperties] = ToDict(peer.PlayerProperties),
            }, exceptActor: actorNr);
            if (rejoin) Log($"[{peer.EndPoint}]   REJOINED as actor {actorNr}");

            _hooks.RaisePlayerJoined(room, self);
        }

        private void HandleLeaveOp(Peer peer, PhotonCommand cmd, PhotonMessage msg)
        {
            SendReliableMessage(peer, cmd.Channel, BuildOpResp(peer, OpCode.Leave, 0, null, null));
            // The client can request to become inactive (willComeBack) via [233].
            bool wantsInactive = msg.Parameters.TryGetValue(Param.IsInactive, out var iv) && iv is bool b && b;
            LeaveRoom(peer, wantsInactive);
        }

        // Remove a peer from its room. When willComeBack is true and the room has a
        // PlayerTtl, the actor is kept as inactive so it can rejoin; otherwise it is
        // removed and its non-global cached events are pruned.
        private void LeaveRoom(Peer peer, bool willComeBack)
        {
            var room = peer.Room;
            if (room == null) return;
            int actorNr = peer.ActorNr;
            int oldMaster = room.MasterClientActor;

            bool goInactive = willComeBack && room.PlayerTtl != 0 && room.Members.ContainsKey(actorNr);
            if (goInactive)
            {
                var m = room.Members[actorNr];
                m.IsInactive    = true;
                m.DeactivatedMs = NowMs;
                m.Peer          = null!;
                room.RecomputeMaster();
            }
            else
            {
                room.RemoveMember(actorNr);
                if (room.CleanupCacheOnLeave)
                    room.EventCache.RemoveAll(ce => ce.Sender == actorNr && !ce.Global);
            }

            _hooks.RaisePlayerLeft(room, peer);

            peer.Room = null;
            peer.ActorNr = 0;

            var leaveParams = new Dictionary<byte, object?>
            {
                [Param.ActorNr]    = actorNr,
                [Param.ActorList]  = room.ActorNumbers,
                [Param.IsInactive] = goInactive,
            };
            // Only advertise a new master when it actually changed: the client fires
            // OnMasterClientSwitched unconditionally whenever this param is present
            // (it does not compare against the current master), so always sending it
            // would spuriously re-fire the callback on every player's departure.
            if (room.MasterClientActor != oldMaster && room.MasterClientActor != 0)
                leaveParams[Param.MasterClientId] = room.MasterClientActor;

            BroadcastEvent(room, EvCode.Leave, leaveParams);
            Log($"[{peer.EndPoint}]   {(goInactive ? "INACTIVE in" : "LEFT")} room '{room.Name}' " +
                $"(actor {actorNr}); {room.PlayerCount} member(s)");

            if (room.PlayerCount == 0)
                DestroyRoom(room, "empty");
        }

        // Remove inactive actors whose PlayerTtl has elapsed, broadcasting a final
        // (permanent) Leave for each. PlayerTtl <= 0 means never expire.
        private void SweepInactiveActors(long now)
        {
            foreach (var room in _rooms.Values.ToList())
            {
                if (room.PlayerTtl <= 0) continue;
                var expired = room.Members.Values
                    .Where(m => m.IsInactive && now - m.DeactivatedMs > room.PlayerTtl)
                    .Select(m => m.ActorNr).ToList();
                foreach (var actorNr in expired)
                {
                    int oldMaster = room.MasterClientActor;
                    room.RemoveMember(actorNr);
                    if (room.CleanupCacheOnLeave)
                        room.EventCache.RemoveAll(ce => ce.Sender == actorNr && !ce.Global);
                    var leaveParams = new Dictionary<byte, object?>
                    {
                        [Param.ActorNr]    = actorNr,
                        [Param.ActorList]  = room.ActorNumbers,
                        [Param.IsInactive] = false,
                    };
                    // Only signal a master change when it actually changed (see LeaveRoom).
                    if (room.MasterClientActor != oldMaster && room.MasterClientActor != 0)
                        leaveParams[Param.MasterClientId] = room.MasterClientActor;
                    BroadcastEvent(room, EvCode.Leave, leaveParams);
                    Log($"[Server]   inactive actor {actorNr} in '{room.Name}' TTL-expired → removed");
                }
                if (room.PlayerCount == 0)
                    DestroyRoom(room, "empty");
            }
        }

        // Remove cached events matching a filter (used by RemoveFromRoomCache).
        // Matches by event code, and if the filter is a hashtable, requires every
        // filter key/value to be present in the cached event's data.
        private static int RemoveMatchingCache(Room room, byte evCode, object? filter)
        {
            var filterDict = filter as Dictionary<object, object?>;
            int before = room.EventCache.Count;
            room.EventCache.RemoveAll(ce =>
            {
                if (ce.Code != evCode) return false;
                if (filterDict == null || filterDict.Count == 0) return true;
                if (ce.Data is not Dictionary<object, object?> cd) return false;
                foreach (var kv in filterDict)
                    if (!cd.TryGetValue(kv.Key, out var v) || !Equals(v, kv.Value)) return false;
                return true;
            });
            return before - room.EventCache.Count;
        }

        private void HandleRaiseEvent(Peer peer, PhotonCommand cmd, PhotonMessage msg)
        {
            var room = peer.Room;
            if (room == null) return;

            byte evCode = msg.Parameters.TryGetValue(Param.Code, out var c) && c is byte b ? b : (byte)0;
            object? data = msg.Parameters.TryGetValue(Param.Data, out var d) ? d : null;
            byte group = msg.Parameters.TryGetValue(Param.ReceiverGroup, out var rg) && rg is byte g ? g : ReceiverGroup.Others;
            byte cache = msg.Parameters.TryGetValue(Param.Cache, out var ca) && ca is byte cb ? cb : EventCaching.DoNotCache;

            // Plugin gate: anti-cheat / protocol mods may drop or rewrite the event
            // before it is cached or routed.
            var raiseCtx = new Api.RaiseEventContext(room, peer, evCode, data, group);
            _hooks.RaiseEventRaising(raiseCtx);
            if (raiseCtx.Cancel) { LogV($"[{peer.EndPoint}]   RAISE ev={evCode} dropped by plugin"); return; }
            evCode = raiseCtx.EventCode;
            data   = raiseCtx.Data;

            // Cache maintenance. Add-to-cache events are also broadcast live;
            // remove-from-cache events only prune the buffer (no broadcast).
            switch (cache)
            {
                case EventCaching.AddToRoomCache:
                case EventCaching.AddToRoomCacheGlobal:
                    room.EventCache.Add(new CachedEvent
                    {
                        Code = evCode, Data = data, Sender = peer.ActorNr,
                        Global = cache == EventCaching.AddToRoomCacheGlobal,
                    });
                    LogV($"[{peer.EndPoint}]   cached ev={evCode} (cache now {room.EventCache.Count})");
                    break;
                case EventCaching.RemoveFromRoomCache:
                    int removed = RemoveMatchingCache(room, evCode, data);
                    LogV($"[{peer.EndPoint}]   removed {removed} cached ev={evCode}");
                    return; // cache-only, do not broadcast
            }

            var evParams = new Dictionary<byte, object?>
            {
                [Param.Data]    = data,
                [Param.ActorNr] = peer.ActorNr,
            };

            // Explicit target actor list overrides the receiver group.
            if (msg.Parameters.TryGetValue(Param.ActorList, out var tl) && tl is object?[] targets)
            {
                foreach (var t in targets)
                    if (t is int ta && room.Members.TryGetValue(ta, out var m) && !m.IsInactive && m.Peer != null)
                        SendReliableMessage(m.Peer, 0, BuildEvt(m.Peer, evCode, evParams));
            }
            else
            {
                switch (group)
                {
                    case ReceiverGroup.All:
                        BroadcastEvent(room, evCode, evParams);
                        break;
                    case ReceiverGroup.Master:
                        if (room.Members.TryGetValue(room.MasterClientActor, out var mc) && mc.Peer != null)
                            SendReliableMessage(mc.Peer, 0, BuildEvt(mc.Peer, evCode, evParams));
                        break;
                    default: // Others
                        BroadcastEvent(room, evCode, evParams, exceptActor: peer.ActorNr);
                        break;
                }
            }
            LogV($"[{peer.EndPoint}]   RAISE ev={evCode} group={group} → routed");
            _hooks.RaiseEventRaised(raiseCtx);
        }

        private void HandleSetProperties(Peer peer, PhotonCommand cmd, PhotonMessage msg)
        {
            var room = peer.Room;
            if (room == null) return;

            var changed = msg.Parameters.TryGetValue(Param.Properties, out var p) && p is Dictionary<object, object?> pd
                ? pd : new Dictionary<object, object?>();
            // In the SetProperties OPERATION the target actor is param 254 (ActorNr);
            // absent/0 means room properties. (Param 253 in the op is ExpectedValues
            // for CAS, NOT the target — 253 is only the target in the outgoing EVENT.)
            // Reading the target from 253 silently filed every player-property set
            // (outfit/ready/nickname) under the room instead of the actor.
            int target = msg.Parameters.TryGetValue(Param.ActorNr, out var t) && t != null
                ? Convert.ToInt32(t) : 0;

            // Plugin gate: moderation mods can reject or rewrite a property set.
            var propsCtx = new Api.PropertiesContext(room, peer, target, changed);
            _hooks.RaisePropertiesChanging(propsCtx);
            if (propsCtx.Cancel)
            {
                SendReliableMessage(peer, cmd.Channel, BuildOpResp(peer, OpCode.SetProperties,
                    propsCtx.ReturnCode, propsCtx.Message ?? "Properties rejected", null));
                Log($"[{peer.EndPoint}]   SETPROPS rejected by plugin ({propsCtx.Message})");
                return;
            }

            // Dedicated nickname gate (player property 255): username-moderation
            // mods get the old/new nick and may reject or sanitize it.
            if (target != 0 && changed.TryGetValue((byte)255, out var nv) && nv is string newNick
                && room.Members.TryGetValue(target, out var nickMember))
            {
                string? oldNick = nickMember.Properties.TryGetValue((byte)255, out var on) ? on as string : null;
                var nickCtx = new Api.NickNameContext(room, nickMember, oldNick, newNick);
                _hooks.RaiseNickNameChanging(nickCtx);
                if (nickCtx.Cancel)
                {
                    SendReliableMessage(peer, cmd.Channel, BuildOpResp(peer, OpCode.SetProperties,
                        nickCtx.ReturnCode, nickCtx.Message ?? "Nickname rejected", null));
                    Log($"[{peer.EndPoint}]   NICKNAME '{newNick}' rejected by plugin ({nickCtx.Message})");
                    return;
                }
                changed[(byte)255] = nickCtx.NewNick; // apply any sanitization
            }

            if (target == 0) // room properties
            {
                foreach (var kv in changed) room.Properties[kv.Key] = kv.Value;
            }
            else if (room.Members.TryGetValue(target, out var m)) // actor properties
            {
                foreach (var kv in changed) m.Properties[kv.Key] = kv.Value;
            }

            SendReliableMessage(peer, cmd.Channel, BuildOpResp(peer, OpCode.SetProperties, 0, null, null));

            // Broadcast the change to everyone INCLUDING the setter: the real server
            // echoes property changes back to the actor that made them (verified
            // against Photon Cloud), and clients rely on that event to apply the
            // change — so excluding the sender leaves its own state/UI unupdated.
            BroadcastEvent(room, EvCode.PropertiesChanged, new Dictionary<byte, object?>
            {
                [Param.Properties]    = changed,
                [Param.TargetActorNr] = target,
                [Param.ActorNr]       = peer.ActorNr,
            });
            Log($"[{peer.EndPoint}]   SETPROPS target={(target == 0 ? "room" : target.ToString())} → broadcast");
        }

        // ── Room helpers ─────────────────────────────────────────────────────

        // A room is full when it has reached its own MaxPlayers or the server-wide
        // MaxPeersPerRoom cap, whichever is lower. 0/-1 mean "no limit" for each.
        private bool RoomIsFull(Room room)
        {
            int effMax = room.MaxPlayers; // 0 = unlimited (Photon convention)
            int cap = _config.MaxPeersPerRoom;
            if (cap > 0) effMax = effMax == 0 ? cap : Math.Min(effMax, cap);
            return effMax > 0 && room.PlayerCount >= effMax;
        }

        private void ApplyRoomOptions(Room room, PhotonMessage msg)
        {
            // Room creation options are nested; MaxPlayers/flags arrive via game
            // properties (248) or dedicated params depending on client version.
            if (msg.Parameters.TryGetValue(Param.GameProperties, out var gp) &&
                gp is Dictionary<object, object?> props)
            {
                foreach (var kv in props) room.Properties[kv.Key] = kv.Value;
                // Well-known room property byte keys: 255=maxPlayers, 253=isOpen, 254=isVisible.
                if (props.TryGetValue((byte)255, out var mp)) room.MaxPlayers = Convert.ToInt32(mp);
                if (props.TryGetValue((byte)253, out var io) && io is bool iob) room.IsOpen = iob;
                if (props.TryGetValue((byte)254, out var iv) && iv is bool ivb) room.IsVisible = ivb;
            }

            // PlayerTtl (235) / EmptyRoomTtl (236) are top-level create params.
            if (msg.Parameters.TryGetValue(Param.PlayerTtl, out var pt)) room.PlayerTtl = Convert.ToInt32(pt);
            // Room flag bit 1 (0x02) = DeleteCacheOnLeave.
            if (msg.Parameters.TryGetValue(Param.RoomFlags, out var rf))
                room.CleanupCacheOnLeave = (Convert.ToInt32(rf) & 0x02) != 0;
        }

        private byte[] BuildGameListEvent(Peer peer)
        {
            var list = new Dictionary<object, object?>();
            foreach (var room in _rooms.Values)
            {
                if (!room.IsVisible) continue;
                list[room.Name] = new Dictionary<object, object?>
                {
                    [(byte)243] = room.PlayerCount,             // player count
                    [(byte)255] = room.MaxPlayers,             // max players
                    [(byte)253] = room.IsOpen,                 // is open
                    [(byte)252] = room.IsVisible,              // is visible
                };
            }
            return BuildEvt(peer, EvCode.GameList, new Dictionary<byte, object?> { [222] = list });
        }

        private static Dictionary<object, object?> ToDict(Dictionary<object, object?> src)
        {
            var d = new Dictionary<object, object?>();
            foreach (var kv in src) d[kv.Key] = kv.Value;
            return d;
        }

        // ── Command builders ─────────────────────────────────────────────────
        // An ACK must carry the SAME channel as the command it acknowledges: the
        // client matches an incoming ACK to an outstanding reliable command by
        // (channel, reliableSeq) — see NCommand.CreateAck / RemoveSentReliableCommand
        // in the client. Acking on the wrong channel leaves the command unacked, so
        // the client retransmits it ~7× and then self-disconnects (~2s).
        private static byte[] BuildAck(byte channel, int ackedSeq, uint sentTime)
        {
            using var ms = new MemoryStream();
            ms.WriteByte((byte)CommandType.Ack);
            ms.WriteByte(channel);   // channel of the acked command
            ms.WriteByte(0x00);      // flags (unreliable)
            ms.WriteByte(0x00);      // reserved
            Wire.WriteI32(ms, 20);   // size (12 header + 8 payload)
            Wire.WriteI32(ms, 0);    // reliable seq (acks are seq 0)
            Wire.WriteI32(ms, ackedSeq);
            Wire.WriteU32(ms, sentTime);
            return ms.ToArray();
        }

        // Server VERIFY_CONNECT: 32-byte payload template observed on the wire,
        // first 2 bytes = assigned peer id.
        private byte[] BuildVerifyConnect(Peer peer, PhotonCommand connectCmd)
        {
            byte[] tmpl = Convert.FromHexString("000004b000028000000000020032000000000000000000000000000000000000");
            tmpl[0] = (byte)(peer.PeerId >> 8);
            tmpl[1] = (byte)peer.PeerId;

            using var ms = new MemoryStream();
            ms.WriteByte((byte)CommandType.VerifyConnect);
            ms.WriteByte(0xFF);                 // channel
            ms.WriteByte(0x01);                 // flags (reliable)
            ms.WriteByte(0x00);                 // reserved
            Wire.WriteI32(ms, 12 + tmpl.Length);// size
            Wire.WriteI32(ms, 1);               // reliable seq (verify is seq 1)
            ms.Write(tmpl, 0, tmpl.Length);
            return ms.ToArray();
        }

        // Payloads larger than this are split into fragment commands (type 8).
        private const int MaxUnfragmented = 1100;

        private void SendReliableMessage(Peer peer, byte channel, byte[] f3Payload)
        {
            if (f3Payload.Length <= MaxUnfragmented)
            {
                int seq = peer.NextOutSeq(channel);
                RegisterAndSend(peer, channel, seq, BuildDataCommand(channel, seq, f3Payload));
                return;
            }

            // Fragment: consecutive reliable sequence numbers, all sharing the
            // first fragment's sequence as the start-sequence.
            int count   = (f3Payload.Length + MaxUnfragmented - 1) / MaxUnfragmented;
            int startSeq = 0;
            for (int i = 0; i < count; i++)
            {
                int seq = peer.NextOutSeq(channel);
                if (i == 0) startSeq = seq;
                int offset = i * MaxUnfragmented;
                int len = Math.Min(MaxUnfragmented, f3Payload.Length - offset);
                var frag = BuildFragmentCommand(channel, seq, startSeq, count, i,
                    f3Payload.Length, offset, f3Payload, offset, len);
                RegisterAndSend(peer, channel, seq, frag);
            }
            LogV($"[{peer.EndPoint}] OUT fragmented {f3Payload.Length}b → {count} parts (startSeq={startSeq})");
        }

        private void RegisterAndSend(Peer peer, byte channel, int seq, byte[] cmdBytes)
        {
            long key = ((long)channel << 32) | (uint)seq;
            peer.UnackedCommands[key] = new Peer.Unacked { CommandBytes = cmdBytes, FirstSentMs = NowMs, LastSentMs = NowMs };
            SendPacket(peer, new[] { cmdBytes });
        }

        private static byte[] BuildDataCommand(byte channel, int seq, byte[] payload)
        {
            using var ms = new MemoryStream();
            ms.WriteByte((byte)CommandType.SendReliable);
            ms.WriteByte(channel);
            ms.WriteByte(0x01);                     // reliable
            ms.WriteByte(0x04);                     // reserved (observed value)
            Wire.WriteI32(ms, 12 + payload.Length);
            Wire.WriteI32(ms, seq);
            ms.Write(payload, 0, payload.Length);
            return ms.ToArray();
        }

        private static byte[] BuildFragmentCommand(byte channel, int seq, int startSeq, int count,
            int fragNum, int totalLen, int fragOffset, byte[] src, int srcOffset, int srcLen)
        {
            using var ms = new MemoryStream();
            ms.WriteByte((byte)CommandType.SendFragment);
            ms.WriteByte(channel);
            ms.WriteByte(0x01);                     // reliable
            ms.WriteByte(0x04);                     // reserved
            Wire.WriteI32(ms, 32 + srcLen);         // size = base(12) + frag header(20) + payload
            Wire.WriteI32(ms, seq);
            Wire.WriteI32(ms, startSeq);
            Wire.WriteI32(ms, count);
            Wire.WriteI32(ms, fragNum);
            Wire.WriteI32(ms, totalLen);
            Wire.WriteI32(ms, fragOffset);
            ms.Write(src, srcOffset, srcLen);
            return ms.ToArray();
        }

        // Send an event to every active member of a room (optionally excluding one
        // actor). The event is serialized per-recipient so mixed-protocol rooms
        // (V16 + V18) are handled correctly. Inactive members are skipped.
        private void BroadcastEvent(Room room, byte eventCode, Dictionary<byte, object?> evParams, int exceptActor = 0)
        {
            foreach (var m in room.Members.Values)
            {
                if (m.ActorNr == exceptActor || m.IsInactive || m.Peer == null) continue;
                SendReliableMessage(m.Peer, 0, BuildEvt(m.Peer, eventCode, evParams));
            }
        }

        private void AckIfReliable(Peer peer, PhotonPacket pkt, PhotonCommand cmd)
        {
            if (!cmd.IsReliable) return;
            var ack = BuildAck(cmd.Channel, cmd.ReliableSeq, pkt.Timestamp);
            SendPacket(peer, new[] { ack });
        }

        // ── Packet framing + send ────────────────────────────────────────────
        private void SendPacket(Peer peer, IReadOnlyList<byte[]> commands)
        {
            using var ms = new MemoryStream();
            Wire.WriteU16(ms, ServerHeaderPeerId);
            ms.WriteByte(0x00);                     // crc disabled
            ms.WriteByte((byte)commands.Count);     // command count
            Wire.WriteU32(ms, Now);                 // server timestamp
            Wire.WriteI32(ms, peer.Challenge);      // echo challenge
            foreach (var c in commands) ms.Write(c, 0, c.Length);
            byte[] datagram = ms.ToArray();
            peer.Socket.Send(datagram, datagram.Length, peer.EndPoint);
        }

        // ── Helpers ──────────────────────────────────────────────────────────
        private static bool IsPingEcho(byte[] data)
        {
            if (data.Length != 13) return false;
            for (int i = 0; i < 12; i++) if (data[i] != 0x7D) return false;
            return true;
        }

        private static string Hex(byte[] b, int len) =>
            BitConverter.ToString(b, 0, Math.Min(len, b.Length)).Replace("-", "");

        // Console log, with optional suspension so a plugin can draw an interactive
        // UI without server chatter interleaving. Suspended lines are buffered
        // (capped) and flushed on ResumeLog. Guarded by its own lock since it is
        // written from handler threads (under _sync) and the console-key thread.
        private readonly object _logSync = new();
        private bool _logSuspended;
        private readonly List<string> _logBuffer = new();

        private void Log(string msg)
        {
            string line = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
            lock (_logSync)
            {
                if (_logSuspended)
                {
                    if (_logBuffer.Count < 5000) _logBuffer.Add(line);
                    return;
                }
            }
            Console.WriteLine(line);
        }

        // Verbose per-packet/per-op logging, gated by config.
        private void LogV(string msg)
        {
            if (_config.VerboseLogging) Log(msg);
        }
    }
}
