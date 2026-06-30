using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace IronNestVR
{
    /// <summary>
    /// Shared co-op packet codec. Replaces the per-subsystem copy-pasted Put*/Get* helpers with one
    /// bounds-checked Writer/Reader. The on-wire encoding is BYTE-IDENTICAL to the old helpers — little-endian 4-byte
    /// int, IEEE-754 float via SingleToInt32Bits, string = [int32 byte-length][utf8 bytes], Vector3 = 3 floats,
    /// Quaternion = 4 floats, bool = 1 byte — so migrating a subsystem changes no packet on the wire.
    ///
    /// Writer/Reader are VALUE TYPES (stack, no per-packet heap alloc). Integer/float read/write use direct bit ops
    /// (no BitConverter scratch). String WRITES encode through a reusable scratch (no transient byte[]); the high-rate
    /// streams (pose / value / group / map-drag / entity-move) carry no strings and are fully allocation-free. String
    /// READS allocate the result string (inherent).
    ///
    /// Buffer sizing: each subsystem KEEPS its current _buf size; the Writer's logical `limit` defaults to the buffer
    /// length, so every aggregate/trim boundary stays byte-identical. The bounds check converts a would-be overflow
    /// from a silent throw/corruption into a clean drop (Overflow) without changing which packets transmit.
    /// </summary>
    internal static class CoopWire
    {
        public const int MaxString = 200;   // default UTF-8 byte cap per string; ALWAYS pass the call-site's real cap

        public static bool Finite(float f)   => !float.IsNaN(f) && !float.IsInfinity(f);
        public static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);

        // ---------------------------------------------------------------- writer ----
        // Bounds-checked against a LOGICAL limit (<= the physical buffer). A write past the limit sets Overflow and
        // writes nothing; all-or-drop senders check `w.Overflow` after building and drop the packet if set.
        public struct Writer
        {
            private readonly Il2CppStructArray<byte> _b;
            private readonly int _limit;
            public int Pos;
            public bool Overflow;

            // limit < 0 => use the full physical buffer. Subsystems whose behavior depended on the old buffer size
            // pass that size so the boundary is byte-identical.
            public Writer(Il2CppStructArray<byte> buf, int limit = -1)
            {
                _b = buf;
                _limit = limit < 0 ? buf.Length : Math.Min(limit, buf.Length);
                Pos = 0; Overflow = false;
            }

            public int Length => Pos;
            public int Remaining => _limit - Pos;

            private bool Room(int n) { if (Pos + n <= _limit) return true; Overflow = true; return false; }

            public void Byte(byte v) { if (!Room(1)) return; _b[Pos++] = v; }
            public void Bool(bool v) { Byte((byte)(v ? 1 : 0)); }
            public void Int(int v)   { if (!Room(4)) return; _b[Pos]=(byte)v; _b[Pos+1]=(byte)(v>>8); _b[Pos+2]=(byte)(v>>16); _b[Pos+3]=(byte)(v>>24); Pos+=4; }
            public void Float(float v) { Int(BitConverter.SingleToInt32Bits(v)); }
            public void Vec(Vector3 v)   { Float(v.x); Float(v.y); Float(v.z); }
            public void Quat(Quaternion q) { Float(q.x); Float(q.y); Float(q.z); Float(q.w); }
            // Reserved for the optional/last CoopP2P origin-trailer migration: the transport still
            // uses its own PutU64 for now, so this has no production caller yet — kept (and SelfTest-covered) so the
            // trailer can adopt CoopWire without re-adding it. NOT a sign P2P was half-migrated.
            public void U64(ulong v) { if (!Room(8)) return; for (int i=0;i<8;i++) _b[Pos+i]=(byte)(v>>(8*i)); Pos+=8; }

            // All-or-drop string: delegates to TryStr and SETS Overflow on failure (so the drop-path fires).
            public void Str(string s, int max = MaxString) { if (!TryStr(s, max)) Overflow = true; }

            // TRUE transactional string write for trim loops (CoopOrders): preflights, writes NOTHING and leaves Pos
            // AND Overflow unchanged if it would not fit, returning false. Truncation is byte-identical to the old
            // helpers: encode the FULL string, then take min(byteCount, max) bytes (may cut mid-codepoint, as before).
            public bool TryStr(string s, int max = MaxString, int reserveTail = 0)
            {
                s ??= "";
                int full = System.Text.Encoding.UTF8.GetByteCount(s);
                byte[] enc = EncodeScratch(full);
                int wrote = full == 0 ? 0 : System.Text.Encoding.UTF8.GetBytes(s, 0, s.Length, enc, 0);
                int n = wrote > max ? max : wrote;
                if (Pos + 4 + n + reserveTail > _limit) return false;   // preflight fails -> no write, Overflow unchanged
                Int(n);                                                 // guaranteed to fit -> won't touch Overflow
                for (int i = 0; i < n; i++) _b[Pos + i] = enc[i];
                Pos += n;
                return true;
            }

            // Back-patch (replaces CoopOrders.PutIntAt): reserve a 4-byte int slot now, fill it in once the real value
            // is known (e.g. the number of lines that actually fit).
            public int Mark() { int at = Pos; Int(0); return at; }
            public void Patch(int at, int v)
            {
                if (at < 0 || at + 4 > _limit) return;
                _b[at]=(byte)v; _b[at+1]=(byte)(v>>8); _b[at+2]=(byte)(v>>16); _b[at+3]=(byte)(v>>24);
            }
        }

        // ---------------------------------------------------------------- reader ----
        // A truncated / malformed read sets Bad and returns default; callers check `r.Bad` once at the end instead of
        // null-checking every field. Mirrors the old GetStr semantics (null/out-of-range -> reject the packet).
        public struct Reader
        {
            private readonly Il2CppStructArray<byte> _b;
            private readonly int _len;
            public int Pos;
            public bool Bad;

            public Reader(Il2CppStructArray<byte> buf, int len, int start = 0) { _b = buf; _len = len; Pos = start; Bad = false; }

            public int Remaining => _len - Pos;
            private bool Have(int n) { if (!Bad && Pos + n <= _len) return true; Bad = true; return false; }

            public void Skip(int n) { if (Have(n)) Pos += n; }
            public byte Byte()      { if (!Have(1)) return 0; return _b[Pos++]; }
            public bool Bool()      { return Byte() != 0; }
            public int Int()        { if (!Have(4)) return 0; int v=_b[Pos]|(_b[Pos+1]<<8)|(_b[Pos+2]<<16)|(_b[Pos+3]<<24); Pos+=4; return v; }
            public float Float()    { return BitConverter.Int32BitsToSingle(Int()); }
            public Vector3 Vec()    { float x=Float(),y=Float(),z=Float(); return new Vector3(x,y,z); }
            public Quaternion Quat(){ float x=Float(),y=Float(),z=Float(),w=Float(); return new Quaternion(x,y,z,w); }

            public string Str(int max = MaxString)
            {
                int n = Int();
                if (Bad || n < 0 || n > max || !Have(n)) { Bad = true; return null; }
                if (n == 0) return "";
                byte[] s = DecodeScratch(n);
                for (int i = 0; i < n; i++) s[i] = _b[Pos + i];
                Pos += n;
                return System.Text.Encoding.UTF8.GetString(s, 0, n);
            }
        }

        // Reusable scratch buffers (main thread; [ThreadStatic] just in case a future tick path differs).
        [ThreadStatic] private static byte[] _enc;   // string ENCODE (write side)
        [ThreadStatic] private static byte[] _dec;   // string DECODE (read side)
        private static byte[] EncodeScratch(int n) { if (_enc == null || _enc.Length < n) _enc = new byte[Math.Max(256, n)]; return _enc; }
        private static byte[] DecodeScratch(int n) { if (_dec == null || _dec.Length < n) _dec = new byte[Math.Max(256, n)]; return _dec; }

        // ---------------------------------------------------------------- self-test ----
        // Round-trip + boundary check, run in-game (no Il2Cpp array exists outside the runtime). Returns false + an
        // error string on the first failed assertion. WIRED at startup in Plugin.Load — logs "[wire] CoopWire
        // self-test: PASS/FAIL" once on boot. (The standalone scratchpad harness proved byte-identical encoding over
        // managed byte[]; this in-runtime check additionally exercises a real Il2CppStructArray<byte>.)
        public static bool SelfTest(out string err)
        {
            err = null;
            try
            {
                var buf = new Il2CppStructArray<byte>(2048);

                // (1) round-trip every type
                var w = new Writer(buf);
                w.Byte(0xA5);
                w.Bool(true); w.Bool(false);
                w.Int(0); w.Int(-1); w.Int(int.MinValue); w.Int(int.MaxValue); w.Int(0x12345678);
                w.Float(0f); w.Float(-1.5f); w.Float(3.14159f);
                w.Vec(new Vector3(1f, -2f, 3.5f));
                w.Quat(new Quaternion(0.1f, 0.2f, 0.3f, 0.4f));
                w.U64(0x0102030405060708UL);
                w.Str("");                                   // empty
                w.Str("hello", 200);                         // ascii
                w.Str("café — 漢字", 200); // multibyte UTF-8
                if (w.Overflow) { err = "unexpected overflow during round-trip write"; return false; }
                int wireLen = w.Length;

                var r = new Reader(buf, wireLen);
                if (r.Byte() != 0xA5) { err = "byte mismatch"; return false; }
                if (r.Bool() != true || r.Bool() != false) { err = "bool mismatch"; return false; }
                if (r.Int() != 0 || r.Int() != -1 || r.Int() != int.MinValue || r.Int() != int.MaxValue || r.Int() != 0x12345678) { err = "int mismatch"; return false; }
                if (r.Float() != 0f || r.Float() != -1.5f || r.Float() != 3.14159f) { err = "float mismatch"; return false; }
                var v = r.Vec(); if (v.x != 1f || v.y != -2f || v.z != 3.5f) { err = "vec mismatch"; return false; }
                var q = r.Quat(); if (q.x != 0.1f || q.y != 0.2f || q.z != 0.3f || q.w != 0.4f) { err = "quat mismatch"; return false; }
                // U64 has no reader (trailer-only); skip its 8 bytes to stay aligned.
                r.Skip(8);
                if (r.Str() != "") { err = "empty-string mismatch"; return false; }
                if (r.Str(200) != "hello") { err = "ascii-string mismatch"; return false; }
                if (r.Str(200) != "café — 漢字") { err = "utf8-string mismatch"; return false; }
                if (r.Bad) { err = "reader went Bad during round-trip"; return false; }
                if (r.Remaining != 0) { err = "reader did not consume the whole packet (remaining=" + r.Remaining + ")"; return false; }

                // (2) all-or-drop Str overflow sets Overflow
                var sb = new Il2CppStructArray<byte>(16);
                var w2 = new Writer(sb);
                w2.Str(new string('x', 100), 200);   // 4 + 100 > 16
                if (!w2.Overflow) { err = "Str did not set Overflow past the buffer"; return false; }

                // (3) transactional TryStr fails WITHOUT setting Overflow, and leaves Pos unchanged
                var w3 = new Writer(sb);
                w3.Byte(1);                            // Pos = 1
                int posBefore = w3.Length;
                bool fit = w3.TryStr(new string('y', 100), 200);
                if (fit) { err = "TryStr claimed an oversized string fit"; return false; }
                if (w3.Overflow) { err = "TryStr set Overflow (must stay the all-or-drop signal)"; return false; }
                if (w3.Length != posBefore) { err = "TryStr advanced Pos on failure"; return false; }

                // (4) truncated read sets Bad
                var w4 = new Writer(buf); w4.Int(50);   // claims a 50-byte string but writes no payload
                var r4 = new Reader(buf, w4.Length);
                r4.Str(200);
                if (!r4.Bad) { err = "Reader did not flag a truncated string"; return false; }

                return true;
            }
            catch (Exception e) { err = "exception: " + e; return false; }
        }
    }
}
