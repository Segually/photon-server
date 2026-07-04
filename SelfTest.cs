using System;
using System.Collections.Generic;
using System.IO;

namespace PhotonServer
{
    // Validates the GpBinaryV18 codec: round-trips (write then read) plus a
    // hand-built message mimicking exactly what the game sends on the wire.
    public static class SelfTest
    {
        private static int _fail;

        public static void RunV18()
        {
            Console.WriteLine("== GpBinaryV18 self-test ==");

            // 1) Round-trip an event with a representative mix of value types.
            var p = new Dictionary<byte, object?>
            {
                [1]  = 12345,
                [2]  = "hello world",
                [3]  = new byte[] { 1, 2, 3, 250, 255 },
                [4]  = true,
                [5]  = (byte)200,
                [6]  = -7,
                [7]  = 3.5f,
                [8]  = new int[] { -1, 0, 1, 1000 },
                [9]  = new Dictionary<object, object?> { [(byte)10] = "nested", [(byte)11] = 42 },
                [12] = new PhotonCustom { Code = 0x56, Data = new byte[] { 0xAA, 0xBB, 0xCC } },
            };
            var evBytes = GpBinaryV18.BuildEvent(230, p);
            var ev = GpBinaryV18.ParseMessage(evBytes, out int consumed);
            Check("event round-trips fully consumed", consumed == evBytes.Length);
            Check("event code", ev!.Code == 230);
            Check("int param", (int)ev.Parameters[1]! == 12345);
            Check("string param", (string)ev.Parameters[2]! == "hello world");
            Check("byte[] param", ((byte[])ev.Parameters[3]!).Length == 5 && ((byte[])ev.Parameters[3]!)[4] == 255);
            Check("bool param", (bool)ev.Parameters[4]! == true);
            Check("byte param", (byte)ev.Parameters[5]! == 200);
            Check("neg int param", (int)ev.Parameters[6]! == -7);
            Check("float param", Math.Abs((float)ev.Parameters[7]! - 3.5f) < 0.0001f);
            Check("int[] param", ((int[])ev.Parameters[8]!)[3] == 1000 && ((int[])ev.Parameters[8]!)[0] == -1);
            Check("hashtable param", ((Dictionary<object, object?>)ev.Parameters[9]!).Count == 2);
            Check("custom param", ((PhotonCustom)ev.Parameters[12]!).Code == 0x56 && ((PhotonCustom)ev.Parameters[12]!).Data.Length == 3);

            // 2) Op response round-trip (returnCode + null debug + params).
            var resp = GpBinaryV18.BuildOpResponse(230, 0, null,
                new Dictionary<byte, object?> { [221] = "secret-token", [225] = "user-1" });
            var rp = GpBinaryV18.ParseMessage(resp, out int c2);
            Check("opresp consumed", c2 == resp.Length);
            Check("opresp type", rp!.Type == MsgType.OperationResponse);
            Check("opresp secret", (string)rp.Parameters[221]! == "secret-token");

            // 3) Hand-built message EXACTLY as the game sends the DH key exchange:
            //    F3 06 (InternalOperationRequest) opcode=0 paramCount=1
            //    paramCode=1 type=0x43(ByteArray) len=compressed(96) [96 bytes].
            var dh = new MemoryStream();
            dh.WriteByte(0xF3);
            dh.WriteByte(0x06);         // InternalOperationRequest
            dh.WriteByte(0x00);         // opcode 0
            dh.WriteByte(0x01);         // 1 parameter (single byte count in V18)
            dh.WriteByte(0x01);         // param code 1
            dh.WriteByte(0x43);         // type ByteArray
            GpBinaryV18.WriteCompressedUInt32(dh, 96);   // length 96 → single byte 0x60
            var key = new byte[96];
            for (int i = 0; i < 96; i++) key[i] = (byte)i;
            dh.Write(key, 0, 96);
            var dhBytes = dh.ToArray();

            var dhMsg = GpBinaryV18.ParseMessage(dhBytes, out int c3);
            Check("DH length prefix is single byte 0x60", dhBytes[6] == 0x60);
            Check("DH consumed fully", c3 == dhBytes.Length);
            Check("DH type", dhMsg!.Type == MsgType.InternalOpRequest);
            Check("DH key length", ((byte[])dhMsg.Parameters[1]!).Length == 96);
            Check("DH key content", ((byte[])dhMsg.Parameters[1]!)[95] == 95);

            // 4) Event-relay shape: reproduce what HandleRaiseEvent pushes back
            //    out — the event data at param 245, sender actor at 254 — using the
            //    exact PUN Instantiate (ev 202) and RPC (ev 200) hashtables observed
            //    on the wire, including nested custom types and a mixed object[].
            //    This is the V18 path a second player receives on relay/cache-replay.
            var instantiateData = new Dictionary<object, object?>
            {
                [(byte)0] = "NetworkPlayer",
                [(byte)1] = new PhotonCustom { Code = 0x56, Data = new byte[12] }, // Vector3
                [(byte)2] = new PhotonCustom { Code = 0x51, Data = new byte[16] }, // Quaternion
                [(byte)6] = 92006,
                [(byte)7] = 10002,
            };
            var evInst = GpBinaryV18.BuildEvent(202, new Dictionary<byte, object?>
            {
                [245] = instantiateData,
                [254] = 1,
            });
            var ri = GpBinaryV18.ParseMessage(evInst, out int c4);
            Check("relay(202) consumed", c4 == evInst.Length);
            Check("relay(202) sender", (int)ri!.Parameters[254]! == 1);
            var idata = (Dictionary<object, object?>)ri.Parameters[245]!;
            Check("relay(202) prefab", (string)idata[(byte)0]! == "NetworkPlayer");
            Check("relay(202) pos custom", ((PhotonCustom)idata[(byte)1]!).Code == 0x56 && ((PhotonCustom)idata[(byte)1]!).Data.Length == 12);
            Check("relay(202) rot custom", ((PhotonCustom)idata[(byte)2]!).Code == 0x51 && ((PhotonCustom)idata[(byte)2]!).Data.Length == 16);
            Check("relay(202) viewId", (int)idata[(byte)7]! == 10002);

            var rpcData = new Dictionary<object, object?>
            {
                [(byte)0] = 10002,                                                   // viewID
                [(byte)2] = 92008,                                                   // timestamp
                [(byte)5] = (byte)56,                                                // rpc shortcut (byte!)
                [(byte)4] = new object?[] { new PhotonCustom { Code = 0x56, Data = new byte[12] }, 191 },
            };
            var evRpc = GpBinaryV18.BuildEvent(200, new Dictionary<byte, object?>
            {
                [245] = rpcData,
                [254] = 1,
            });
            var rr = GpBinaryV18.ParseMessage(evRpc, out int c5);
            Check("relay(200) consumed", c5 == evRpc.Length);
            var rdata = (Dictionary<object, object?>)rr!.Parameters[245]!;
            Check("relay(200) viewId", (int)rdata[(byte)0]! == 10002);
            Check("relay(200) rpc shortcut stays byte", rdata[(byte)5] is byte s && s == 56);
            var rargs = (object?[])rdata[(byte)4]!;
            Check("relay(200) arg0 custom", ((PhotonCustom)rargs[0]!).Code == 0x56);
            Check("relay(200) arg1 int", (int)rargs[1]! == 191);

            Console.WriteLine(_fail == 0 ? "ALL PASS" : $"{_fail} FAILED");
            Environment.Exit(_fail == 0 ? 0 : 1);
        }

        private static void Check(string name, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
            if (!ok) _fail++;
        }
    }
}
