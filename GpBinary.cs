using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PhotonServer
{
    // GpBinaryV16 type codes (derived from observed wire data).
    public enum GpType : byte
    {
        Null       = 0x2A, // '*'
        Byte       = 0x62, // 'b'
        Short      = 0x6B, // 'k'
        Int        = 0x69, // 'i'
        Long       = 0x6C, // 'l'
        Float      = 0x66, // 'f'
        Double     = 0x64, // 'd'
        Bool       = 0x6F, // 'o'
        String     = 0x73, // 's'
        ByteArray  = 0x78, // 'x'
        Array      = 0x79, // 'y'  (typed array)
        ObjectArray= 0x7A, // 'z'
        Hashtable  = 0x68, // 'h'
        Dictionary = 0x44, // 'D'
        StringArray= 0x61, // 'a'
        Custom     = 0x63, // 'c' — registered custom type (Vector3, Quaternion, …)
        IntArray   = 0x6E, // 'n' (unverified; kept for completeness)
    }

    // A registered custom type kept opaque: the server forwards/caches it without
    // interpreting the payload (e.g. PUN Vector3=0x56, Quaternion=0x51).
    public sealed class PhotonCustom
    {
        public byte   Code = 0;
        public byte[] Data = Array.Empty<byte>();
    }

    // A parsed operation/event message.
    public sealed class PhotonMessage
    {
        public MsgType Type;
        public bool    Encrypted;
        public byte    Code;          // opCode or eventCode
        public short   ReturnCode;    // op responses only
        public object? DebugMessage;  // op responses only
        public Dictionary<byte, object?> Parameters = new();

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append(Type);
            if (Encrypted) sb.Append("[ENC]");
            sb.Append($" code={Code}");
            if (Type == MsgType.OperationResponse || Type == MsgType.InternalOpResponse)
                sb.Append($" ret={ReturnCode} dbg={GpBinary.Fmt(DebugMessage)}");
            sb.Append(" {");
            foreach (var kv in Parameters)
                sb.Append($" {kv.Key}={GpBinary.Fmt(kv.Value)},");
            sb.Append(" }");
            return sb.ToString();
        }
    }

    public static class GpBinary
    {
        // ── Reading ──────────────────────────────────────────────────────────

        public static PhotonMessage? ParseMessage(byte[] b)
        {
            if (b.Length < 2 || b[0] != Wire.Magic) return null;
            byte rawType = b[1];
            var msg = new PhotonMessage
            {
                Encrypted = (rawType & 0x80) != 0,
                Type      = (MsgType)(rawType & 0x7F),
            };
            if (msg.Encrypted) return msg; // cannot decode without key yet

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
                    msg.ReturnCode = (short)Wire.ReadU16(b, o); o += 2;
                    msg.DebugMessage = ReadValue(b, ref o);
                    ReadParams(b, ref o, msg);
                    break;
                case MsgType.Event:
                    msg.Code = b[o++];
                    ReadParams(b, ref o, msg);
                    break;
                default:
                    // Init / InitResponse / other: leave raw
                    break;
            }
            return msg;
        }

        private static void ReadParams(byte[] b, ref int o, PhotonMessage msg)
        {
            if (o + 2 > b.Length) return;
            int count = Wire.ReadU16(b, o); o += 2;
            for (int i = 0; i < count && o < b.Length; i++)
            {
                byte pc = b[o++];
                msg.Parameters[pc] = ReadValue(b, ref o);
            }
        }

        public static object? ReadValue(byte[] b, ref int o)
        {
            GpType t = (GpType)b[o++];
            return ReadTyped(b, ref o, t);
        }

        private static object? ReadTyped(byte[] b, ref int o, GpType t)
        {
            switch (t)
            {
                case GpType.Null:      return null;
                case GpType.Byte:      return b[o++];
                case GpType.Short:     { short v = (short)Wire.ReadU16(b, o); o += 2; return v; }
                case GpType.Int:       { int v = Wire.ReadI32(b, o); o += 4; return v; }
                case GpType.Long:      { long v = ReadI64(b, o); o += 8; return v; }
                case GpType.Float:     { int bits = Wire.ReadI32(b, o); o += 4; return BitConverter.Int32BitsToSingle(bits); }
                case GpType.Double:    { long bits = ReadI64(b, o); o += 8; return BitConverter.Int64BitsToDouble(bits); }
                case GpType.Bool:      return b[o++] != 0;
                case GpType.String:    { int ln = Wire.ReadU16(b, o); o += 2; var s = Encoding.UTF8.GetString(b, o, ln); o += ln; return s; }
                case GpType.ByteArray: { int ln = Wire.ReadI32(b, o); o += 4; var a = new byte[ln]; Array.Copy(b, o, a, 0, ln); o += ln; return a; }
                case GpType.Array:
                {
                    int ln = Wire.ReadU16(b, o); o += 2;
                    GpType et = (GpType)b[o++];
                    var arr = new object?[ln];
                    for (int i = 0; i < ln; i++) arr[i] = ReadTyped(b, ref o, et);
                    return arr;
                }
                case GpType.ObjectArray:
                {
                    int ln = Wire.ReadU16(b, o); o += 2;
                    var arr = new object?[ln];
                    for (int i = 0; i < ln; i++) arr[i] = ReadValue(b, ref o);
                    return arr;
                }
                case GpType.StringArray:
                {
                    int ln = Wire.ReadU16(b, o); o += 2;
                    var arr = new string[ln];
                    for (int i = 0; i < ln; i++) { int sl = Wire.ReadU16(b, o); o += 2; arr[i] = Encoding.UTF8.GetString(b, o, sl); o += sl; }
                    return arr;
                }
                case GpType.Hashtable:
                {
                    int ln = Wire.ReadU16(b, o); o += 2;
                    var h = new Dictionary<object, object?>();
                    for (int i = 0; i < ln; i++) { var k = ReadValue(b, ref o); var v = ReadValue(b, ref o); if (k != null) h[k] = v; }
                    return h;
                }
                case GpType.Custom:
                {
                    byte code = b[o++];
                    int ln = Wire.ReadU16(b, o); o += 2;
                    var data = new byte[ln];
                    Array.Copy(b, o, data, 0, ln); o += ln;
                    return new PhotonCustom { Code = code, Data = data };
                }
                case GpType.Dictionary:
                {
                    GpType kt = (GpType)b[o++]; GpType vt = (GpType)b[o++];
                    int ln = Wire.ReadU16(b, o); o += 2;
                    var d = new Dictionary<object, object?>();
                    for (int i = 0; i < ln; i++)
                    {
                        var k = (kt == 0) ? ReadValue(b, ref o) : ReadTyped(b, ref o, kt);
                        var v = (vt == 0) ? ReadValue(b, ref o) : ReadTyped(b, ref o, vt);
                        if (k != null) d[k] = v;
                    }
                    return d;
                }
                default:
                    throw new NotSupportedException($"GpType 0x{(byte)t:X2} not supported");
            }
        }

        private static long ReadI64(byte[] b, int o)
        {
            return ((long)Wire.ReadU32(b, o) << 32) | Wire.ReadU32(b, o + 4);
        }

        // ── Writing ──────────────────────────────────────────────────────────

        public static byte[] BuildOpResponse(byte opCode, short returnCode, object? debug, Dictionary<byte, object?>? p = null)
        {
            using var ms = new MemoryStream();
            ms.WriteByte(Wire.Magic);
            ms.WriteByte((byte)MsgType.OperationResponse);
            ms.WriteByte(opCode);
            Wire.WriteU16(ms, (ushort)returnCode);
            WriteValue(ms, debug);
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

        public static byte[] BuildInternalOpResponse(byte opCode, short returnCode, Dictionary<byte, object?>? p = null)
        {
            using var ms = new MemoryStream();
            ms.WriteByte(Wire.Magic);
            ms.WriteByte((byte)MsgType.InternalOpResponse);
            ms.WriteByte(opCode);
            Wire.WriteU16(ms, (ushort)returnCode);
            WriteValue(ms, null);
            WriteParams(ms, p);
            return ms.ToArray();
        }

        private static void WriteParams(Stream s, Dictionary<byte, object?>? p)
        {
            int count = p?.Count ?? 0;
            Wire.WriteU16(s, (ushort)count);
            if (p == null) return;
            foreach (var kv in p) { s.WriteByte(kv.Key); WriteValue(s, kv.Value); }
        }

        public static void WriteValue(Stream s, object? v)
        {
            switch (v)
            {
                case null:    s.WriteByte((byte)GpType.Null); break;
                case byte b:  s.WriteByte((byte)GpType.Byte); s.WriteByte(b); break;
                case bool bo: s.WriteByte((byte)GpType.Bool); s.WriteByte((byte)(bo ? 1 : 0)); break;
                case short sh:s.WriteByte((byte)GpType.Short); Wire.WriteU16(s, (ushort)sh); break;
                case int i:   s.WriteByte((byte)GpType.Int); Wire.WriteI32(s, i); break;
                case long l:  s.WriteByte((byte)GpType.Long); WriteI64(s, l); break;
                case float f: s.WriteByte((byte)GpType.Float); Wire.WriteI32(s, BitConverter.SingleToInt32Bits(f)); break;
                case double d:s.WriteByte((byte)GpType.Double); WriteI64(s, BitConverter.DoubleToInt64Bits(d)); break;
                case string str:
                {
                    s.WriteByte((byte)GpType.String);
                    var bytes = Encoding.UTF8.GetBytes(str);
                    Wire.WriteU16(s, (ushort)bytes.Length);
                    s.Write(bytes, 0, bytes.Length);
                    break;
                }
                case byte[] ba:
                    s.WriteByte((byte)GpType.ByteArray);
                    Wire.WriteI32(s, ba.Length);
                    s.Write(ba, 0, ba.Length);
                    break;
                case int[] ia:
                    s.WriteByte((byte)GpType.Array);
                    Wire.WriteU16(s, (ushort)ia.Length);
                    s.WriteByte((byte)GpType.Int);
                    foreach (var i in ia) Wire.WriteI32(s, i);
                    break;
                case float[] fa:
                    s.WriteByte((byte)GpType.Array);
                    Wire.WriteU16(s, (ushort)fa.Length);
                    s.WriteByte((byte)GpType.Float);
                    foreach (var f in fa) Wire.WriteI32(s, BitConverter.SingleToInt32Bits(f));
                    break;
                case bool[] boa:
                    s.WriteByte((byte)GpType.Array);
                    Wire.WriteU16(s, (ushort)boa.Length);
                    s.WriteByte((byte)GpType.Bool);
                    foreach (var b2 in boa) s.WriteByte((byte)(b2 ? 1 : 0));
                    break;
                case string[] sa:
                    s.WriteByte((byte)GpType.StringArray);
                    Wire.WriteU16(s, (ushort)sa.Length);
                    foreach (var str2 in sa)
                    {
                        var sb = Encoding.UTF8.GetBytes(str2 ?? "");
                        Wire.WriteU16(s, (ushort)sb.Length);
                        s.Write(sb, 0, sb.Length);
                    }
                    break;
                case Dictionary<object, object?> ht:
                {
                    s.WriteByte((byte)GpType.Hashtable);
                    Wire.WriteU16(s, (ushort)ht.Count);
                    foreach (var kv in ht) { WriteValue(s, kv.Key); WriteValue(s, kv.Value); }
                    break;
                }
                case object?[] arr:
                {
                    s.WriteByte((byte)GpType.ObjectArray);
                    Wire.WriteU16(s, (ushort)arr.Length);
                    foreach (var item in arr) WriteValue(s, item);
                    break;
                }
                case PhotonCustom pc:
                    s.WriteByte((byte)GpType.Custom);
                    s.WriteByte(pc.Code);
                    Wire.WriteU16(s, (ushort)pc.Data.Length);
                    s.Write(pc.Data, 0, pc.Data.Length);
                    break;
                default:
                    throw new NotSupportedException($"Cannot serialize {v.GetType()}");
            }
        }

        private static void WriteI64(Stream s, long v)
        {
            Wire.WriteU32(s, (uint)(v >> 32));
            Wire.WriteU32(s, (uint)v);
        }

        // ── Debug formatting ─────────────────────────────────────────────────
        public static string Fmt(object? v)
        {
            switch (v)
            {
                case null: return "null";
                case string s: return $"\"{s}\"";
                case byte[] b: return $"bytes[{b.Length}]:{BitConverter.ToString(b, 0, Math.Min(b.Length, 32))}";
                case Dictionary<object, object?> h:
                {
                    var sb = new StringBuilder("{");
                    foreach (var kv in h) sb.Append($" {Fmt(kv.Key)}={Fmt(kv.Value)},");
                    sb.Append(" }");
                    return sb.ToString();
                }
                case object?[] arr:
                {
                    var parts = new List<string>();
                    foreach (var it in arr) parts.Add(Fmt(it));
                    return $"[{string.Join(", ", parts)}]";
                }
                case PhotonCustom pc: return $"custom(0x{pc.Code:X2})[{pc.Data.Length}]";
                default: return v.ToString() ?? "?";
            }
        }
    }
}
