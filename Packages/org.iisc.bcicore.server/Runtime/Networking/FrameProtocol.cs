// ============================================================================
//  FrameProtocol.cs — Stateless header parser and CMD-frame builder.
//
//  Wire format (little-endian):
//    [0xAA][0x55][type:1][seq:4][payloadLen:2][payload:payloadLen]
//  Total header = 9 bytes.
//
//  All structs are parsed with manual byte indexing (no unsafe / Marshal)
//  so this compiles cleanly on all Unity backends including IL2CPP.
// ============================================================================
using System;

namespace BciCore
{
    public static class FrameProtocol
    {
        public const byte Magic0 = 0xAA;
        public const byte Magic1 = 0x55;
        public const int  HeaderSize = 9;   // 2 magic + 1 type + 4 seq + 2 payloadLen

        // ── Header parse ─────────────────────────────────────────────────────

        /// <summary>
        /// Validates magic bytes and extracts header fields.
        /// Returns false if magic is wrong (caller should resync).
        /// </summary>
        public static bool TryParseHeader(byte[] buf, int offset,
            out FrameType type, out uint seq, out ushort payloadLen)
        {
            type       = FrameType.Hello;
            seq        = 0;
            payloadLen = 0;

            if (buf == null || buf.Length - offset < HeaderSize) return false;
            if (buf[offset] != Magic0 || buf[offset + 1] != Magic1) return false;

            type       = (FrameType)buf[offset + 2];
            seq        = ReadU32LE(buf, offset + 3);
            payloadLen = ReadU16LE(buf, offset + 7);
            return true;
        }

        // ── CMD frame builder (PC → ESP32) ───────────────────────────────────

        /// <summary>
        /// Builds the 11-byte CMD frame (type 0x08) carrying a 16-bit marker code.
        /// Layout: [AA][55][08][seq=0:4][len=2:2][code:2]  (all LE)
        /// </summary>
        public static byte[] BuildCmdFrame(ushort markerCode)
        {
            var buf = new byte[11];
            buf[0] = Magic0;
            buf[1] = Magic1;
            buf[2] = (byte)FrameType.Cmd;
            // seq = 0 for outgoing commands
            buf[3] = 0; buf[4] = 0; buf[5] = 0; buf[6] = 0;
            buf[7] = 2; buf[8] = 0;  // payloadLen = 2
            buf[9]  = (byte)(markerCode & 0xFF);
            buf[10] = (byte)(markerCode >> 8);
            return buf;
        }

        // ── Payload parsers ──────────────────────────────────────────────────

        /// <summary>Parse HELLO payload → BciConfig.</summary>
        public static BciConfig ParseHello(byte[] payload)
        {
            var cfg = new BciConfig();
            if (payload == null || payload.Length < 14) return cfg;
            cfg.Fs         = ReadU16LE(payload, 0);
            cfg.NumCh      = payload[2];
            // payload[3] = nchips (ignored here)
            cfg.UvPerCount = ReadF32LE(payload, 4);
            cfg.FirLen     = ReadU16LE(payload, 8);
            cfg.DcR        = ReadF32LE(payload, 10);

            // Optional montage descriptor (offset 14+)
            if (payload.Length >= 17)
            {
                cfg.AnalysisCh = payload[14];
                int hlN = payload[15];
                int hrN = payload[16];
                int off = 17;
                cfg.HemiL = new byte[hlN];
                Array.Copy(payload, off, cfg.HemiL, 0, Math.Min(hlN, payload.Length - off));
                off += hlN;
                cfg.HemiR = new byte[hrN];
                Array.Copy(payload, off, cfg.HemiR, 0, Math.Min(hrN, payload.Length - off));
            }
            else
            {
                cfg.HemiL = Array.Empty<byte>();
                cfg.HemiR = Array.Empty<byte>();
            }
            return cfg;
        }

        /// <summary>
        /// Parse RAW payload → int[] counts. Returns null if payload is wrong size.
        /// plen = numCh*4  or  numCh*4+2  (with inline marker appended).
        /// </summary>
        public static int[] ParseRawCounts(byte[] payload, int numCh, out ushort inlineMarker)
        {
            inlineMarker = 0;
            int expected = numCh * 4;
            if (payload == null || (payload.Length != expected && payload.Length != expected + 2))
                return null;
            var counts = new int[numCh];
            for (int i = 0; i < numCh; i++)
                counts[i] = (int)ReadU32LE(payload, i * 4);
            if (payload.Length == expected + 2)
                inlineMarker = ReadU16LE(payload, expected);
            return counts;
        }

        /// <summary>Parse PROC payload → float[] µV values.</summary>
        public static float[] ParseProcUv(byte[] payload, int numCh, out ushort inlineMarker)
        {
            inlineMarker = 0;
            int expected = numCh * 4;
            if (payload == null || (payload.Length != expected && payload.Length != expected + 2))
                return null;
            var vals = new float[numCh];
            for (int i = 0; i < numCh; i++)
                vals[i] = ReadF32LE(payload, i * 4);
            if (payload.Length == expected + 2)
                inlineMarker = ReadU16LE(payload, expected);
            return vals;
        }

        /// <summary>Parse ANALYSIS payload → float[] alpha power (length analysisCh).</summary>
        public static float[] ParseAlpha(byte[] payload, int analysisCh)
        {
            if (payload == null || payload.Length != analysisCh * 4) return null;
            var alpha = new float[analysisCh];
            for (int i = 0; i < analysisCh; i++)
                alpha[i] = ReadF32LE(payload, i * 4);
            return alpha;
        }

        /// <summary>Parse SSVEP payload → SsvepFrame (4×analysisCh floats).</summary>
        public static SsvepFrame ParseSsvep(byte[] payload, int analysisCh, uint seq)
        {
            var sf = new SsvepFrame { Seq = seq };
            sf.PowerA = new float[analysisCh];
            sf.SnrA   = new float[analysisCh];
            sf.PowerB = new float[analysisCh];
            sf.SnrB   = new float[analysisCh];
            if (payload == null || payload.Length != analysisCh * 4 * 4) return sf;
            int off = 0;
            for (int i = 0; i < analysisCh; i++, off += 4) sf.PowerA[i] = ReadF32LE(payload, off);
            for (int i = 0; i < analysisCh; i++, off += 4) sf.SnrA[i]   = ReadF32LE(payload, off);
            for (int i = 0; i < analysisCh; i++, off += 4) sf.PowerB[i] = ReadF32LE(payload, off);
            for (int i = 0; i < analysisCh; i++, off += 4) sf.SnrB[i]   = ReadF32LE(payload, off);
            return sf;
        }

        /// <summary>
        /// Parse NF payload → NfSample.
        /// Supports 12-byte (2 floats + int), 14-byte (+ marker), and 22-byte (4 floats + int + marker) layouts.
        /// </summary>
        public static NfSample ParseNf(byte[] payload, uint seq)
        {
            var nf = new NfSample { Seq = seq };
            if (payload == null) return nf;

            if (payload.Length >= 22)
            {
                nf.Smi14gt18       = ReadF32LE(payload, 0);
                nf.Smi18gt14       = ReadF32LE(payload, 4);
                nf.Smi14gt18Shaped = ReadF32LE(payload, 8);
                nf.Smi18gt14Shaped = ReadF32LE(payload, 12);
                nf.SampleCount     = (int)ReadU32LE(payload, 16);
                nf.Marker          = ReadU16LE(payload, 20);
            }
            else if (payload.Length >= 14)
            {
                nf.Smi14gt18   = ReadF32LE(payload, 0);
                nf.Smi18gt14   = ReadF32LE(payload, 4);
                nf.SampleCount = (int)ReadU32LE(payload, 8);
                nf.Marker      = ReadU16LE(payload, 12);
            }
            else if (payload.Length >= 12)
            {
                nf.Smi14gt18   = ReadF32LE(payload, 0);
                nf.Smi18gt14   = ReadF32LE(payload, 4);
                nf.SampleCount = (int)ReadU32LE(payload, 8);
            }
            return nf;
        }

        /// <summary>Parse HEALTH payload (14-byte legacy or 26-byte extended).</summary>
        public static HealthFrame ParseHealth(byte[] payload, uint seq)
        {
            var h = new HealthFrame { Seq = seq };
            if (payload == null || payload.Length < 14) return h;
            h.BoardMs    = ReadU32LE(payload, 0);
            h.BoardDrops = ReadU32LE(payload, 4);
            h.FreeHeap   = ReadU32LE(payload, 8);
            h.Marker     = ReadU16LE(payload, 12);
            if (payload.Length >= 26)
            {
                h.BoardBad    = ReadU32LE(payload, 14);
                h.BoardMiss   = ReadU32LE(payload, 18);
                h.BoardDspMax = ReadU32LE(payload, 22);
            }
            return h;
        }

        /// <summary>Parse ML result (9-byte payload).</summary>
        public static MlResult ParseMl(byte[] payload, uint seq)
        {
            var ml = new MlResult { Seq = seq };
            if (payload == null || payload.Length < 9) return ml;
            ml.PredClass  = payload[0];
            ml.Confidence = ReadF32LE(payload, 1);
            ml.LatencyMs  = ReadF32LE(payload, 5);
            return ml;
        }

        // ── Little-endian primitive readers ──────────────────────────────────

        public static uint   ReadU32LE(byte[] b, int o) =>
            (uint)(b[o] | (b[o+1]<<8) | (b[o+2]<<16) | (b[o+3]<<24));

        public static ushort ReadU16LE(byte[] b, int o) =>
            (ushort)(b[o] | (b[o+1]<<8));

        public static float ReadF32LE(byte[] b, int o)
        {
            // Reinterpret 4 LE bytes as IEEE 754 float.
            // BitConverter.ToSingle handles endianness correctly on all platforms.
            if (System.BitConverter.IsLittleEndian)
                return System.BitConverter.ToSingle(b, o);
            // Big-endian: swap bytes first
            byte[] tmp = { b[o+3], b[o+2], b[o+1], b[o] };
            return System.BitConverter.ToSingle(tmp, 0);
        }
    }
}
