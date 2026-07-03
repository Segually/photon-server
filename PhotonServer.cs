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
    // Per-connected-peer reliability + identity state.
    public sealed class Peer
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
    }

    public sealed class PhotonServer
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
        private const int MaxRetries   = 12;

        // The server always sends packets with peer id 0x0000 in the header
        // (observed on the wire).
        private const ushort ServerHeaderPeerId = 0x0000;

        public PhotonServer(ServerConfig config)
        {
            _config = config;
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
                            // (crashed clients that never sent a Disconnect).
                            if (_config.PeerTimeoutMs > 0 && now - peer.LastRecvMs > _config.PeerTimeoutMs)
                            {
                                Log($"[{peer.EndPoint}] idle timeout ({now - peer.LastRecvMs}ms) — dropping");
                                LeaveRoom(peer, willComeBack: true);
                                _peers.Remove(peer.EndPoint);
                                CleanupSession(peer);
                                continue;
                            }

                            foreach (var kv in peer.UnackedCommands.ToList())
                            {
                                var u = kv.Value;
                                if (now - u.LastSentMs < RetransmitMs) continue;
                                if (u.Retries >= MaxRetries)
                                {
                                    peer.UnackedCommands.Remove(kv.Key);
                                    continue;
                                }
                                u.Retries++;
                                u.LastSentMs = now;
                                SendPacket(peer, new[] { u.CommandBytes });
                            }
                        }
                    }
                }
            }) { IsBackground = true, Name = "Retransmit" };
            t.Start();
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
                            LeaveRoom(peer, willComeBack: true);
                            _peers.Remove(from);
                            CleanupSession(peer);
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
            ushort assignedId = _nextPeerId++;
            var peer = new Peer(from, sock, assignedId, pkt.Challenge) { LastRecvMs = NowMs };
            _peers[from] = peer;
            Log($"[{from}] CONNECT → assigned peerId=0x{assignedId:X4} challenge=0x{pkt.Challenge:X8}");

            // Reply: ACK(the connect) + VERIFY_CONNECT(with assigned peer id).
            var ack    = BuildAck(cmd.ReliableSeq, pkt.Timestamp);
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
                var dmsg = GpBinary.ParseMessage(full);
                LogV($"[{peer.EndPoint}] IN* {dmsg}   (decrypted {plain.Length}b)");
                if (dmsg != null) DispatchMessage(peer, cmd, dmsg);
                return;
            }

            var msg = GpBinary.ParseMessage(payload);
            if (msg == null) { Log($"[{peer.EndPoint}] unparsed msg: {Hex(payload, payload.Length)}"); return; }

            LogV($"[{peer.EndPoint}] IN  {msg}");
            DispatchMessage(peer, cmd, msg);
        }

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
                    var resp = GpBinary.BuildInternalOpResponse(0, 0,
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
                            GpBinary.BuildOpResponse(OpCode.Authenticate, 32755, "Invalid AppId", null));
                        Log($"[{peer.EndPoint}]   AUTH rejected (AppId '{aids}')");
                        break;
                    }
                    if (_config.RequireAppVersion != null &&
                        msg.Parameters.TryGetValue(Param.AppVersion, out var av) && av is string avs &&
                        avs != _config.RequireAppVersion)
                    {
                        SendReliableMessage(peer, cmd.Channel,
                            GpBinary.BuildOpResponse(OpCode.Authenticate, 32755, "Invalid AppVersion", null));
                        Log($"[{peer.EndPoint}]   AUTH rejected (AppVersion '{avs}')");
                        break;
                    }

                    peer.Authenticated = true;

                    // A client-supplied UserId (param 225) enables rejoin across
                    // reconnects; otherwise assign a stable one.
                    string? clientUserId = msg.Parameters.TryGetValue(Param.UserId, out var uid) && uid is string uids && uids.Length > 0
                        ? uids : null;

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

                    var resp = GpBinary.BuildOpResponse(OpCode.Authenticate, 0, null,
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
                    var resp = GpBinary.BuildOpResponse(OpCode.JoinLobby, 0, null, null);
                    SendReliableMessage(peer, cmd.Channel, resp);
                    SendReliableMessage(peer, cmd.Channel, BuildGameListEvent());
                    Log($"[{peer.EndPoint}]   JOIN LOBBY ok ({_rooms.Count} room(s) listed)");
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
            var resp = GpBinary.BuildOpResponse(opCode, 0, null, new Dictionary<byte, object?>
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

            if (_rooms.ContainsKey(name))
            {
                // Room exists → error (game already exists = 32766).
                SendReliableMessage(peer, cmd.Channel,
                    GpBinary.BuildOpResponse(OpCode.CreateGame, 32766, "Game already exists", null));
                Log($"[{peer.EndPoint}]   CREATE '{name}' FAILED (exists)");
                return;
            }
            if (_config.MaxRooms > 0 && _rooms.Count >= _config.MaxRooms)
            {
                SendReliableMessage(peer, cmd.Channel,
                    GpBinary.BuildOpResponse(OpCode.CreateGame, 32760, "Server room limit reached", null));
                Log($"[{peer.EndPoint}]   CREATE '{name}' FAILED (server at {_config.MaxRooms} rooms)");
                return;
            }

            var room = new Room(name);
            ApplyRoomOptions(room, msg);

            // Reject creation if the requested MaxPlayers exceeds the server's
            // per-room cap.
            if (_config.MaxPeersPerRoom > 0 && room.MaxPlayers > _config.MaxPeersPerRoom)
            {
                SendReliableMessage(peer, cmd.Channel, GpBinary.BuildOpResponse(OpCode.CreateGame, 32762,
                    $"MaxPlayers {room.MaxPlayers} exceeds server limit {_config.MaxPeersPerRoom}", null));
                Log($"[{peer.EndPoint}]   CREATE '{name}' FAILED (maxPlayers {room.MaxPlayers} > cap {_config.MaxPeersPerRoom})");
                return;
            }

            _rooms[name] = room;
            EnterRoom(peer, room, msg, OpCode.CreateGame);
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
                }
                else
                {
                    // Game does not exist = 32758.
                    SendReliableMessage(peer, cmd.Channel,
                        GpBinary.BuildOpResponse(OpCode.JoinGame, 32758, "Game does not exist", null));
                    Log($"[{peer.EndPoint}]   JOIN '{name}' FAILED (missing)");
                    return;
                }
            }
            if (!room.IsOpen)
            {
                SendReliableMessage(peer, cmd.Channel,
                    GpBinary.BuildOpResponse(OpCode.JoinGame, 32764, "Game closed", null));
                return;
            }
            if (RoomIsFull(room))
            {
                SendReliableMessage(peer, cmd.Channel,
                    GpBinary.BuildOpResponse(OpCode.JoinGame, 32765, "Game full", null));
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
                        GpBinary.BuildOpResponse(OpCode.JoinRandomGame, 32760, "No match found", null));
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

            var respParams = new Dictionary<byte, object?>
            {
                [Param.ActorNr]          = actorNr,
                [Param.ActorList]        = room.ActorNumbers,
                [Param.PlayerProperties] = allPlayers,
                [Param.GameProperties]   = ToDict(room.Properties),
                [Param.RoomFlags]        = (int)flags,
            };
            SendReliableMessage(peer, 0, GpBinary.BuildOpResponse(opCode, 0, null, respParams));

            // Replay cached events (buffered Instantiates etc.) so the joiner
            // reconstructs existing room state.
            foreach (var ce in room.EventCache)
            {
                var evt = GpBinary.BuildEvent(ce.Code, new Dictionary<byte, object?>
                {
                    [Param.Data]    = ce.Data,
                    [Param.ActorNr] = ce.Sender,
                });
                SendReliableMessage(peer, 0, evt);
            }
            if (room.EventCache.Count > 0)
                LogV($"[{peer.EndPoint}]   replayed {room.EventCache.Count} cached event(s)");

            // Notify existing members that an actor joined/rejoined (Event 255).
            var joinEvent = GpBinary.BuildEvent(EvCode.Join, new Dictionary<byte, object?>
            {
                [Param.ActorNr]          = actorNr,
                [Param.ActorList]        = room.ActorNumbers,
                [Param.PlayerProperties] = ToDict(peer.PlayerProperties),
            });
            BroadcastEvent(room, joinEvent, exceptActor: actorNr);
            if (rejoin) Log($"[{peer.EndPoint}]   REJOINED as actor {actorNr}");
        }

        private void HandleLeaveOp(Peer peer, PhotonCommand cmd, PhotonMessage msg)
        {
            SendReliableMessage(peer, cmd.Channel, GpBinary.BuildOpResponse(OpCode.Leave, 0, null, null));
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

            peer.Room = null;
            peer.ActorNr = 0;

            var leaveEvent = GpBinary.BuildEvent(EvCode.Leave, new Dictionary<byte, object?>
            {
                [Param.ActorNr]        = actorNr,
                [Param.ActorList]      = room.ActorNumbers,
                [Param.MasterClientId] = room.MasterClientActor,
                [Param.IsInactive]     = goInactive,
            });
            BroadcastEvent(room, leaveEvent);
            Log($"[{peer.EndPoint}]   {(goInactive ? "INACTIVE in" : "LEFT")} room '{room.Name}' " +
                $"(actor {actorNr}); {room.PlayerCount} member(s)");

            if (room.PlayerCount == 0)
            {
                _rooms.Remove(room.Name);
                Log($"[Server]   room '{room.Name}' empty → removed");
            }
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
                    room.RemoveMember(actorNr);
                    if (room.CleanupCacheOnLeave)
                        room.EventCache.RemoveAll(ce => ce.Sender == actorNr && !ce.Global);
                    var leaveEvent = GpBinary.BuildEvent(EvCode.Leave, new Dictionary<byte, object?>
                    {
                        [Param.ActorNr]        = actorNr,
                        [Param.ActorList]      = room.ActorNumbers,
                        [Param.MasterClientId] = room.MasterClientActor,
                        [Param.IsInactive]     = false,
                    });
                    BroadcastEvent(room, leaveEvent);
                    Log($"[Server]   inactive actor {actorNr} in '{room.Name}' TTL-expired → removed");
                }
                if (room.PlayerCount == 0)
                {
                    _rooms.Remove(room.Name);
                    Log($"[Server]   room '{room.Name}' empty → removed");
                }
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
            byte[] evBytes = GpBinary.BuildEvent(evCode, evParams);

            // Explicit target actor list overrides the receiver group.
            if (msg.Parameters.TryGetValue(Param.ActorList, out var tl) && tl is object?[] targets)
            {
                foreach (var t in targets)
                    if (t is int ta && room.Members.TryGetValue(ta, out var m) && !m.IsInactive && m.Peer != null)
                        SendReliableMessage(m.Peer, 0, evBytes);
            }
            else
            {
                switch (group)
                {
                    case ReceiverGroup.All:
                        BroadcastEvent(room, evBytes);
                        break;
                    case ReceiverGroup.Master:
                        if (room.Members.TryGetValue(room.MasterClientActor, out var mc))
                            SendReliableMessage(mc.Peer, 0, evBytes);
                        break;
                    default: // Others
                        BroadcastEvent(room, evBytes, exceptActor: peer.ActorNr);
                        break;
                }
            }
            LogV($"[{peer.EndPoint}]   RAISE ev={evCode} group={group} → routed");
        }

        private void HandleSetProperties(Peer peer, PhotonCommand cmd, PhotonMessage msg)
        {
            var room = peer.Room;
            if (room == null) return;

            var changed = msg.Parameters.TryGetValue(Param.Properties, out var p) && p is Dictionary<object, object?> pd
                ? pd : new Dictionary<object, object?>();
            int target = msg.Parameters.TryGetValue(Param.TargetActorNr, out var t) && t is int ti ? ti : 0;

            if (target == 0) // room properties
            {
                foreach (var kv in changed) room.Properties[kv.Key] = kv.Value;
            }
            else if (room.Members.TryGetValue(target, out var m)) // actor properties
            {
                foreach (var kv in changed) m.Properties[kv.Key] = kv.Value;
            }

            SendReliableMessage(peer, cmd.Channel, GpBinary.BuildOpResponse(OpCode.SetProperties, 0, null, null));

            // Broadcast the change to everyone (Event 253).
            var evt = GpBinary.BuildEvent(EvCode.PropertiesChanged, new Dictionary<byte, object?>
            {
                [Param.Properties]    = changed,
                [Param.TargetActorNr] = target,
                [Param.ActorNr]       = peer.ActorNr,
            });
            BroadcastEvent(room, evt, exceptActor: peer.ActorNr);
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

        private byte[] BuildGameListEvent()
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
            return GpBinary.BuildEvent(EvCode.GameList, new Dictionary<byte, object?> { [222] = list });
        }

        private static Dictionary<object, object?> ToDict(Dictionary<object, object?> src)
        {
            var d = new Dictionary<object, object?>();
            foreach (var kv in src) d[kv.Key] = kv.Value;
            return d;
        }

        // ── Command builders ─────────────────────────────────────────────────
        private static byte[] BuildAck(int ackedSeq, uint sentTime)
        {
            using var ms = new MemoryStream();
            ms.WriteByte((byte)CommandType.Ack);
            ms.WriteByte(0xFF);      // channel
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
            peer.UnackedCommands[key] = new Peer.Unacked { CommandBytes = cmdBytes, LastSentMs = NowMs };
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
        // actor). Inactive members have no live peer and are skipped.
        private void BroadcastEvent(Room room, byte[] eventPayload, int exceptActor = 0)
        {
            foreach (var m in room.Members.Values)
            {
                if (m.ActorNr == exceptActor || m.IsInactive || m.Peer == null) continue;
                SendReliableMessage(m.Peer, 0, eventPayload);
            }
        }

        private void AckIfReliable(Peer peer, PhotonPacket pkt, PhotonCommand cmd)
        {
            if (!cmd.IsReliable) return;
            var ack = BuildAck(cmd.ReliableSeq, pkt.Timestamp);
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

        private static void Log(string msg) =>
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg}");

        // Verbose per-packet/per-op logging, gated by config.
        private void LogV(string msg)
        {
            if (_config.VerboseLogging) Log(msg);
        }
    }
}
