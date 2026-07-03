using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PhotonServer
{
    // GpBinaryV18 (Protocol18) codec — the serialization the real game uses.
    // Derived from the game's ExitGames.Client.Photon.Protocol18 decompile.
    //
    // Differences from V16: little-endian fixed-width numbers, LEB128/zigzag
    // "compressed" integers, 1-byte parameter counts, compressed-length prefixes
    // for strings/arrays/byte-arrays, and a distinct type-code table. The F3 +
    // message-type envelope is shared with V16.
    public static class GpBinaryV18
    {
        // Type codes (subset used; see the Protocol18 Read dispatcher).
        private const byte TNull = 8;
        private const byte TBool = 2, TBoolFalse = 0x1b, TBoolTrue = 0x1c;
        private const byte TByte = 3, TByteZero = 0x22;
        private const byte TShort = 4, TShortZero = 0x1d;
        private const byte TFloat = 5, TFloatZero = 0x20;
        private const byte TDouble = 6, TDoubleZero = 0x21;
        private const byte TString = 7;
        private const byte TCompressedInt = 9, TIntZero = 0x1e;
        private const byte TIntB = 0x0b, TIntNB = 0x0c, TIntUS = 0x0d, TIntNUS = 0x0e;
        private const byte TCompressedLong = 0x0a, TLongZero = 0x1f;
        private const byte TLongB = 0x0f, TLongNB = 0x10, TLongUS = 0x11, TLongNUS = 0x12;
        private const byte TCustom = 0x13;
        private const byte TDictionary = 0x14, THashtable = 0x15, TObjectArray = 0x17;
        private const byte TArrayInArray = 0x40;
        private const byte TBoolArray = 0x42, TByteArray = 0x43, TShortArray = 0x44;
        private const byte TFloatArray = 0x45, TDoubleArray = 0x46, TStringArray = 0x47;
        private const byte TIntArray = 0x49, TLongArray = 0x4a;
        private const byte TCustomArray = 0x53, TDictArray = 0x54, THashtableArray = 0x55;

        // ── Message parsing ──────────────────────────────────────────────────

        // Parse a full F3 message. Throws on malformed data. Sets `consumed` to
        // the number of bytes read (used for protocol auto-detection).
        public static PhotonMessage? ParseMessage(byte[] b, out int consumed)
        {
            consumed = 0;
            if (b.Length < 2 || b[0] != Wire.Magic) return null;
            byte rawType = b[1];
            var msg = new PhotonMessage
            {
                Encrypted = (rawType & 0x80) != 0,
                Type      = (MsgType)(rawType & 0x7F),
            };
            if (msg.Encrypted) { consumed = b.Length; return msg; }

            int o = 2;
            switch (msg.Type)
            {
                case MsgType.OperationRequest:
                case MsgType.InternalOpRequest:
                    msg.Code = b[o++];
                    ReadParams(b, ref o, msg);
                    break;
                case MsgType.OperationResponse:
                case MsgType.InternalOpResponse:
                    msg.Code = b[o++];
                    msg.ReturnCode = (short)(b[o] | (b[o + 1] << 8)); o += 2;
                    msg.DebugMessage = ReadValue(b, ref o);
                    ReadParams(b, ref o, msg);
                    break;
                case MsgType.Event:
                    msg.Code = b[o++];
                    ReadParams(b, ref o, msg);
                    break;
                default:
                    break; // Init etc. — no body
            }
            consumed = o;
            return msg;
        }

        private static void ReadParams(byte[] b, ref int o, PhotonMessage msg)
        {
            if (o >= b.Length) return;
            int count = b[o++];               // 1-byte parameter count
            for (int i = 0; i < count; i++)
            {
                byte code = b[o++];
                msg.Parameters[code] = ReadValue(b, ref o);
            }
        }

        // ── Value reading ────────────────────────────────────────────────────

        public static object? ReadValue(byte[] b, ref int o)
        {
            byte t = b[o++];
            return ReadTyped(b, ref o, t);
        }

        private static object? ReadTyped(byte[] b, ref int o, byte t)
        {
            // Inline custom type: high bit set and code < 0xE5.
            if ((t & 0x80) != 0 && t < 0xE5)
                return ReadCustom(b, ref o, (byte)(t & 0x7F));

            switch (t)
            {
                case TNull:       return null;
                case TBool:       return b[o++] != 0;
                case TBoolFalse:  return false;
                case TBoolTrue:   return true;
                case TByte:       return b[o++];
                case TByteZero:   return (byte)0;
                case TShort:      return ReadInt16(b, ref o);
                case TShortZero:  return (short)0;
                case TFloat:      { float f = BitConverter.ToSingle(b, o); o += 4; return f; }
                case TFloatZero:  return 0f;
                case TDouble:     { double d = BitConverter.ToDouble(b, o); o += 8; return d; }
                case TDoubleZero: return 0d;
                case TString:     return ReadString(b, ref o);
                case TCompressedInt: return ReadZigZag32(b, ref o);
                case TIntZero:    return 0;
                case TIntB:       return (int)b[o++];
                case TIntNB:      return -(int)b[o++];
                case TIntUS:      return (int)ReadUInt16(b, ref o);
                case TIntNUS:     return -(int)ReadUInt16(b, ref o);
                case TCompressedLong: return ReadZigZag64(b, ref o);
                case TLongZero:   return 0L;
                case TLongB:      return (long)b[o++];
                case TLongNB:     return -(long)b[o++];
                case TLongUS:     return (long)ReadUInt16(b, ref o);
                case TLongNUS:    return -(long)ReadUInt16(b, ref o);
                case TByteArray:
                {
                    uint len = ReadCompressedUInt32(b, ref o);
                    var a = new byte[len];
                    Array.Copy(b, o, a, 0, (int)len); o += (int)len;
                    return a;
                }
                case TCustom: // explicit custom type: code byte follows
                    return ReadCustom(b, ref o, b[o++]);
                case THashtable:
                {
                    uint n = ReadCompressedUInt32(b, ref o);
                    var h = new Dictionary<object, object?>();
                    for (uint i = 0; i < n; i++)
                    {
                        var k = ReadValue(b, ref o);
                        var v = ReadValue(b, ref o);
                        if (k != null) h[k] = v;
                    }
                    return h;
                }
                case TObjectArray:
                {
                    uint n = ReadCompressedUInt32(b, ref o);
                    var arr = new object?[n];
                    for (uint i = 0; i < n; i++) arr[i] = ReadValue(b, ref o);
                    return arr;
                }
                case TIntArray:
                {
                    uint n = ReadCompressedUInt32(b, ref o);
                    var arr = new int[n];
                    for (uint i = 0; i < n; i++) arr[i] = ReadZigZag32(b, ref o);
                    return arr;
                }
                case TStringArray:
                {
                    uint n = ReadCompressedUInt32(b, ref o);
                    var arr = new string[n];
                    for (uint i = 0; i < n; i++) arr[i] = ReadString(b, ref o);
                    return arr;
                }
                default:
                    throw new NotSupportedException($"V18 GpType 0x{t:X2} not supported");
            }
        }

        private static object ReadCustom(byte[] b, ref int o, byte code)
        {
            uint len = ReadCompressedUInt32(b, ref o);
            var data = new byte[len];
            Array.Copy(b, o, data, 0, (int)len); o += (int)len;
            return new PhotonCustom { Code = code, Data = data };
        }

        private static short ReadInt16(byte[] b, ref int o) { short v = (short)(b[o] | (b[o + 1] << 8)); o += 2; return v; }
        private static ushort ReadUInt16(byte[] b, ref int o) { ushort v = (ushort)(b[o] | (b[o + 1] << 8)); o += 2; return v; }

        private static string ReadString(byte[] b, ref int o)
        {
            uint len = ReadCompressedUInt32(b, ref o);
            if (len == 0) return "";
            var s = Encoding.UTF8.GetString(b, o, (int)len); o += (int)len;
            return s;
        }

        // ── Compressed integers (LEB128 + zigzag) ────────────────────────────

        public static uint ReadCompressedUInt32(byte[] b, ref int o)
        {
            uint value = 0; int shift = 0;
            while (true)
            {
                byte by = b[o++];
                value |= (uint)(by & 0x7F) << shift;
                if ((by & 0x80) == 0) break;
                shift += 7;
            }
            return value;
        }

        public static ulong ReadCompressedUInt64(byte[] b, ref int o)
        {
            ulong value = 0; int shift = 0;
            while (true)
            {
                byte by = b[o++];
                value |= (ulong)(by & 0x7F) << shift;
                if ((by & 0x80) == 0) break;
                shift += 7;
            }
            return value;
        }

        private static int ReadZigZag32(byte[] b, ref int o)
        {
            uint v = ReadCompressedUInt32(b, ref o);
            return (int)(v >> 1) ^ -(int)(v & 1);
        }

        private static long ReadZigZag64(byte[] b, ref int o)
        {
            ulong v = ReadCompressedUInt64(b, ref o);
            return (long)(v >> 1) ^ -(long)(v & 1);
        }

        // ── Message building ─────────────────────────────────────────────────

        public static byte[] BuildOpResponse(byte opCode, short returnCode, object? debug, Dictionary<byte, object?>? p = null)
        {
            using var ms = new MemoryStream();
            ms.WriteByte(Wire.Magic);
            ms.WriteByte((byte)MsgType.OperationResponse);
            ms.WriteByte(opCode);
            ms.WriteByte((byte)returnCode);           // little-endian
            ms.WriteByte((byte)(returnCode >> 8));
            if (debug is string ds) { ms.WriteByte(TString); WriteStringBody(ms, ds); }
            else ms.WriteByte(TNull);
            WriteParams(ms, p);
            return ms.ToArray();
        }

        public static byte[] BuildInternalOpResponse(byte opCode, short returnCode, Dictionary<byte, object?>? p = null)
        {
            using var ms = new MemoryStream();
            ms.WriteByte(Wire.Magic);
            ms.WriteByte((byte)MsgType.InternalOpResponse);
            ms.WriteByte(opCode);
            ms.WriteByte((byte)returnCode);
            ms.WriteByte((byte)(returnCode >> 8));
            ms.WriteByte(TNull);
            WriteParams(ms, p);
            return ms.ToArray();
        }

        public static byte[] BuildEvent(byte eventCode, Dictionary<byte, object?>? p = null)
        {
            using var ms = new MemoryStream();
            ms.WriteByte(Wire.Magic);
            ms.WriteByte((byte)MsgType.Event);
            ms.WriteByte(eventCode);
            WriteParams(ms, p);
            return ms.ToArray();
        }

        private static void WriteParams(Stream s, Dictionary<byte, object?>? p)
        {
            int count = p?.Count ?? 0;
            s.WriteByte((byte)count);                 // 1-byte parameter count
            if (p == null) return;
            foreach (var kv in p) { s.WriteByte(kv.Key); WriteValue(s, kv.Value); }
        }

        // ── Value writing ────────────────────────────────────────────────────

        public static void WriteValue(Stream s, object? v)
        {
            switch (v)
            {
                case null:     s.WriteByte(TNull); break;
                case bool bo:  s.WriteByte(bo ? TBoolTrue : TBoolFalse); break;
                case byte by:  s.WriteByte(TByte); s.WriteByte(by); break;
                case short sh: s.WriteByte(TShort); s.WriteByte((byte)sh); s.WriteByte((byte)(sh >> 8)); break;
                case int i:    s.WriteByte(TCompressedInt); WriteZigZag32(s, i); break;
                case long l:   s.WriteByte(TCompressedLong); WriteZigZag64(s, l); break;
                case float f:  { s.WriteByte(TFloat); var fb = BitConverter.GetBytes(f); s.Write(fb, 0, 4); break; }
                case double d: { s.WriteByte(TDouble); var db = BitConverter.GetBytes(d); s.Write(db, 0, 8); break; }
                case string str: s.WriteByte(TString); WriteStringBody(s, str); break;
                case byte[] ba:
                    s.WriteByte(TByteArray); WriteCompressedUInt32(s, (uint)ba.Length); s.Write(ba, 0, ba.Length);
                    break;
                case int[] ia:
                    s.WriteByte(TIntArray); WriteCompressedUInt32(s, (uint)ia.Length);
                    foreach (var x in ia) WriteZigZag32(s, x);
                    break;
                case string[] sa:
                    s.WriteByte(TStringArray); WriteCompressedUInt32(s, (uint)sa.Length);
                    foreach (var x in sa) WriteStringBody(s, x ?? "");
                    break;
                case Dictionary<object, object?> ht:
                    s.WriteByte(THashtable); WriteCompressedUInt32(s, (uint)ht.Count);
                    foreach (var kv in ht) { WriteValue(s, kv.Key); WriteValue(s, kv.Value); }
                    break;
                case object?[] arr:
                    s.WriteByte(TObjectArray); WriteCompressedUInt32(s, (uint)arr.Length);
                    foreach (var item in arr) WriteValue(s, item);
                    break;
                case PhotonCustom pc:
                    s.WriteByte(TCustom); s.WriteByte(pc.Code);
                    WriteCompressedUInt32(s, (uint)pc.Data.Length); s.Write(pc.Data, 0, pc.Data.Length);
                    break;
                default:
                    throw new NotSupportedException($"V18 cannot serialize {v.GetType()}");
            }
        }

        private static void WriteStringBody(Stream s, string str)
        {
            var bytes = Encoding.UTF8.GetBytes(str);
            WriteCompressedUInt32(s, (uint)bytes.Length);
            s.Write(bytes, 0, bytes.Length);
        }

        public static void WriteCompressedUInt32(Stream s, uint v)
        {
            while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
            s.WriteByte((byte)v);
        }

        private static void WriteCompressedUInt64(Stream s, ulong v)
        {
            while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
            s.WriteByte((byte)v);
        }

        private static void WriteZigZag32(Stream s, int v) => WriteCompressedUInt32(s, (uint)((v << 1) ^ (v >> 31)));
        private static void WriteZigZag64(Stream s, long v) => WriteCompressedUInt64(s, (ulong)((v << 1) ^ (v >> 63)));
    }
}
