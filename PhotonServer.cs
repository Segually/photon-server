using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace PhotonServer
{
    // Per-connected-peer reliability + identity state.
    public sealed class Peer
    {
        public IPEndPoint EndPoint;
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

        // Inbound reliable commands are delivered at-least-once (client retransmits
        // until ACKed). Track processed (channel, seq) so each is handled exactly once.
        private readonly HashSet<long> _processedReliable = new();
        public bool MarkProcessed(byte channel, int seq)
        {
            long key = ((long)channel << 32) | (uint)seq;
            return _processedReliable.Add(key); // false if already present
        }

        public Peer(IPEndPoint ep, ushort id, int challenge)
        {
            EndPoint = ep; PeerId = id; Challenge = challenge;
        }
    }

    public sealed class PhotonServer
    {
        private readonly int _port;
        private readonly UdpClient _udp;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Dictionary<IPEndPoint, Peer> _peers = new();
        private ushort _nextPeerId = 0xC300;

        // The server always sends packets with peer id 0x0000 in the header
        // (observed on the wire).
        private const ushort ServerHeaderPeerId = 0x0000;

        public PhotonServer(int port)
        {
            _port = port;
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        }

        public void Run()
        {
            Log($"[Server] Photon UDP server listening on 0.0.0.0:{_port}");
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                byte[] data;
                IPEndPoint from;
                try
                {
                    data = _udp.Receive(ref remote);
                    from = new IPEndPoint(remote.Address, remote.Port);
                }
                catch (SocketException e)
                {
                    Log($"[Server] recv error: {e.SocketErrorCode}");
                    continue;
                }
                try { HandleDatagram(data, from); }
                catch (Exception e) { Log($"[Server] handler error: {e}"); }
            }
        }

        private uint Now => (uint)_clock.ElapsedMilliseconds;

        // ── Datagram dispatch ────────────────────────────────────────────────
        private void HandleDatagram(byte[] data, IPEndPoint from)
        {
            // 13-byte all-0x7D ping/echo → reflect verbatim (region latency probe).
            if (IsPingEcho(data))
            {
                _udp.Send(data, data.Length, from);
                return;
            }

            var pkt = Wire.Parse(data, data.Length);
            if (pkt == null) { Log($"[{from}] unparseable ({data.Length}b): {Hex(data, data.Length)}"); return; }

            _peers.TryGetValue(from, out Peer? peer);

            foreach (var cmd in pkt.Commands)
            {
                // Every reliable command (except Connect, handled specially)
                // must be acknowledged, including control commands like type 12.
                if (peer != null && cmd.IsReliable && cmd.Type != CommandType.Connect)
                    AckIfReliable(peer, pkt, cmd);

                switch (cmd.Type)
                {
                    case CommandType.Connect:
                        peer = HandleConnect(pkt, cmd, from);
                        break;
                    case CommandType.Ack:
                        break;
                    case CommandType.Disconnect:
                        Log($"[{from}] DISCONNECT");
                        if (peer != null) _peers.Remove(from);
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
                    default:
                        Log($"[{from}] control command {(byte)cmd.Type} ch{cmd.Channel} seq{cmd.ReliableSeq} (acked)");
                        break;
                }
            }
        }

        // ── Connection handshake ─────────────────────────────────────────────
        private Peer HandleConnect(PhotonPacket pkt, PhotonCommand cmd, IPEndPoint from)
        {
            ushort assignedId = _nextPeerId++;
            var peer = new Peer(from, assignedId, pkt.Challenge);
            _peers[from] = peer;
            Log($"[{from}] CONNECT → assigned peerId=0x{assignedId:X4} challenge=0x{pkt.Challenge:X8}");

            // Reply: ACK(the connect) + VERIFY_CONNECT(with assigned peer id).
            var ack    = BuildAck(cmd.ReliableSeq, pkt.Timestamp);
            var verify = BuildVerifyConnect(peer, cmd);
            SendPacket(peer, new[] { ack, verify });
            return peer;
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
                Log($"[{peer.EndPoint}] IN* {dmsg}   (decrypted {plain.Length}b)");
                if (dmsg != null) DispatchMessage(peer, cmd, dmsg);
                return;
            }

            var msg = GpBinary.ParseMessage(payload);
            if (msg == null) { Log($"[{peer.EndPoint}] unparsed msg: {Hex(payload, payload.Length)}"); return; }

            Log($"[{peer.EndPoint}] IN  {msg}");
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
                case 230: // Authenticate
                {
                    peer.Authenticated = true;
                    // Success. [221]=secret token, [1]=assigned userId (GUID).
                    var resp = GpBinary.BuildOpResponse(230, 0, null,
                        new Dictionary<byte, object?>
                        {
                            [221] = Guid.NewGuid().ToString("N"),          // session secret
                            [1]   = Guid.NewGuid().ToString(),             // userId
                        });
                    SendReliableMessage(peer, cmd.Channel, resp);
                    Log($"[{peer.EndPoint}]   AUTHENTICATE ok");
                    break;
                }
                case 229: // JoinLobby
                {
                    var resp = GpBinary.BuildOpResponse(229, 0, null, null);
                    SendReliableMessage(peer, cmd.Channel, resp);
                    // Event 230 = GameList (empty room list for now).
                    var gameList = GpBinary.BuildEvent(230,
                        new Dictionary<byte, object?> { [222] = new Dictionary<object, object?>() });
                    SendReliableMessage(peer, cmd.Channel, gameList);
                    Log($"[{peer.EndPoint}]   JOIN LOBBY ok (empty room list)");
                    break;
                }
                default:
                    Log($"[{peer.EndPoint}]   op {msg.Code} — no handler yet");
                    break;
            }
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

        private void SendReliableMessage(Peer peer, byte channel, byte[] f3Payload)
        {
            int seq = peer.NextOutSeq(channel);
            using var ms = new MemoryStream();
            ms.WriteByte((byte)CommandType.SendReliable);
            ms.WriteByte(channel);
            ms.WriteByte(0x01);                     // reliable
            ms.WriteByte(0x04);                     // reserved (observed value)
            Wire.WriteI32(ms, 12 + f3Payload.Length);
            Wire.WriteI32(ms, seq);
            ms.Write(f3Payload, 0, f3Payload.Length);
            SendPacket(peer, new[] { ms.ToArray() });
            Log($"[{peer.EndPoint}] OUT ch{channel} seq={seq} ({f3Payload.Length}b) {Hex(f3Payload, Math.Min(f3Payload.Length, 32))}");
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
            _udp.Send(datagram, datagram.Length, peer.EndPoint);
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
    }
}
