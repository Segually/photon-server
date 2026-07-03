using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PhotonServer
{
    // ── Photon reliable-UDP command types (ENet-derived) ─────────────────────
    public enum CommandType : byte
    {
        Ack            = 1,
        Connect        = 2,
        VerifyConnect  = 3,
        Disconnect     = 4,
        Ping           = 5,
        SendReliable   = 6,
        SendUnreliable = 7,
        SendFragment   = 8,
    }

    // ── Photon message types (the byte after the 0xF3 magic) ─────────────────
    // High bit 0x80 = encrypted payload.
    public enum MsgType : byte
    {
        Init                     = 0,
        InitResponse             = 1,
        OperationRequest         = 2,
        OperationResponse        = 3,
        Event                    = 4,
        InternalOpRequest        = 6,
        InternalOpResponse       = 7,
        Message                  = 5,
    }

    // ── A parsed inbound command ─────────────────────────────────────────────
    public sealed class PhotonCommand
    {
        public CommandType Type;
        public byte   Channel;
        public byte   Flags;
        public byte   Reserved;
        public int    Size;
        public int    ReliableSeq;
        public int    UnreliableSeq;   // only for SendUnreliable

        // Fragment fields (only for SendFragment, command type 8).
        public int FragStartSeq;
        public int FragCount;
        public int FragNumber;
        public int FragTotalLength;
        public int FragOffset;

        public byte[] Payload = Array.Empty<byte>();

        public bool IsReliable => (Flags & 1) != 0;
    }

    // ── A parsed inbound packet (header + commands) ──────────────────────────
    public sealed class PhotonPacket
    {
        public ushort PeerId;
        public byte   CrcEnabled;
        public byte   CommandCount;
        public uint   Timestamp;
        public int    Challenge;
        public List<PhotonCommand> Commands = new();
    }

    public static class Wire
    {
        public const byte Magic = 0xF3;

        // ── Big-endian primitive helpers ─────────────────────────────────────
        public static void WriteU16(Stream s, ushort v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        public static void WriteU32(Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8));  s.WriteByte((byte)v);
        }
        public static void WriteI32(Stream s, int v) => WriteU32(s, (uint)v);

        public static ushort ReadU16(byte[] b, int o) => (ushort)((b[o] << 8) | b[o + 1]);
        public static uint   ReadU32(byte[] b, int o) => (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);
        public static int    ReadI32(byte[] b, int o) => (int)ReadU32(b, o);

        // ── Packet parsing ───────────────────────────────────────────────────
        public static PhotonPacket? Parse(byte[] b, int len)
        {
            if (len < 12) return null;
            var p = new PhotonPacket
            {
                PeerId       = ReadU16(b, 0),
                CrcEnabled   = b[2],
                CommandCount = b[3],
                Timestamp    = ReadU32(b, 4),
                Challenge    = ReadI32(b, 8),
            };

            int o = 12;
            for (int i = 0; i < p.CommandCount; i++)
            {
                if (o + 12 > len) break;
                var c = new PhotonCommand
                {
                    Type        = (CommandType)b[o],
                    Channel     = b[o + 1],
                    Flags       = b[o + 2],
                    Reserved    = b[o + 3],
                    Size        = ReadI32(b, o + 4),
                    ReliableSeq = ReadI32(b, o + 8),
                };
                int payloadStart = o + 12;
                if (c.Type == CommandType.SendUnreliable)
                {
                    if (o + 16 > len) break;
                    c.UnreliableSeq = ReadI32(b, o + 12);
                    payloadStart = o + 16;
                }
                else if (c.Type == CommandType.SendFragment)
                {
                    if (o + 32 > len) break;
                    c.FragStartSeq    = ReadI32(b, o + 12);
                    c.FragCount       = ReadI32(b, o + 16);
                    c.FragNumber      = ReadI32(b, o + 20);
                    c.FragTotalLength = ReadI32(b, o + 24);
                    c.FragOffset      = ReadI32(b, o + 28);
                    payloadStart = o + 32;
                }
                int end = o + c.Size;
                if (c.Size < 12 || end > len) break;
                int payLen = end - payloadStart;
                if (payLen > 0)
                {
                    c.Payload = new byte[payLen];
                    Array.Copy(b, payloadStart, c.Payload, 0, payLen);
                }
                p.Commands.Add(c);
                o = end;
            }
            return p;
        }
    }
}
