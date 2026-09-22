// ============================================================================
//  CsvSessionLogger.cs — High-performance binary session logger.
//
//  During acquisition, all 4 data streams are logged directly to compact binary
//  files (.bin) with zero string formatting, zero StringBuilder allocations,
//  and zero GC churn:
//    • eeg_<ts>_raw.bin      — uint32 seq + 32 int32 counts + uint16 marker (134 B)
//    • eeg_<ts>_signals.bin  — uint32 seq + 32 float32 uv   + uint16 marker (134 B)
//    • eeg_<ts>_features.bin — byte is_cca + seq + marker + scores + alpha (112 B)
//    • eeg_<ts>_health.bin   — wall_sec + board_ms + drop/gap stats + heap  (74 B)
//
//  On session stop / game completion, BinToCsvConverter automatically batch-
//  converts all .bin files into standard research CSVs (.csv).
// ============================================================================
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace BciCore
{
    public readonly struct RawLogItem
    {
        public readonly uint   Seq;
        public readonly int[]  Counts;
        public readonly ushort Marker;
        public RawLogItem(uint seq, int[] counts, ushort marker)
        { Seq = seq; Counts = counts; Marker = marker; }
    }

    public readonly struct SignalLogItem
    {
        public readonly uint    Seq;
        public readonly float[] Uv;
        public readonly ushort  Marker;
        public SignalLogItem(uint seq, float[] uv, ushort marker)
        { Seq = seq; Uv = uv; Marker = marker; }
    }

    public readonly struct HealthLogItem
    {
        public readonly int    WallClockSec;
        public readonly uint   BoardMs;
        public readonly uint   Seq;
        public readonly uint   BoardDrops;
        public readonly uint   PcGaps;
        public readonly uint   BoardBad;
        public readonly uint   BoardMiss;
        public readonly uint   BoardDspMax;
        public readonly uint   SrvBad;
        public readonly uint   FreeHeap;
        public readonly ushort Marker;
        public readonly string Event;

        public HealthLogItem(int wallSec, uint boardMs, uint seq, uint drops, uint gaps,
                             uint bad, uint miss, uint dspMax, uint srvBad, uint heap, ushort marker, string evt)
        {
            WallClockSec = wallSec; BoardMs = boardMs; Seq = seq; BoardDrops = drops;
            PcGaps = gaps; BoardBad = bad; BoardMiss = miss; BoardDspMax = dspMax;
            SrvBad = srvBad; FreeHeap = heap; Marker = marker; Event = evt ?? "";
        }
    }

    public readonly struct FeatureLogItem
    {
        public readonly bool    IsCca;
        public readonly ushort  Marker;
        public readonly uint    Seq;
        public readonly float   V1, V2, V3, V4;
        public readonly float   AlphaNf, SsvepNf;
        public readonly float   AlphaLeft, AlphaRight;
        public readonly float   SsvepRight14, SsvepLeft18;
        public readonly float[] Alpha;

        public FeatureLogItem(bool isCca, ushort marker, uint seq, float v1, float v2, float v3, float v4,
                              float aNf, float sNf, float aLeft, float aRight, float sRight14, float sLeft18, float[] alpha)
        {
            IsCca = isCca; Marker = marker; Seq = seq;
            V1 = v1; V2 = v2; V3 = v3; V4 = v4;
            AlphaNf = aNf; SsvepNf = sNf;
            AlphaLeft = aLeft; AlphaRight = aRight;
            SsvepRight14 = sRight14; SsvepLeft18 = sLeft18;
            Alpha = alpha;
        }
    }

    public sealed class CsvSessionLogger : IDisposable
    {
        readonly BinaryLogQueue<RawLogItem>     _raw;
        readonly BinaryLogQueue<SignalLogItem>  _sig;
        readonly BinaryLogQueue<FeatureLogItem> _feat;
        readonly BinaryLogQueue<HealthLogItem>  _hlth;

        readonly int _analysisCh;
        bool _disposed;

        public string SessionDir      { get; }
        public string RawBinPath      { get; }
        public string SignalsBinPath  { get; }
        public string FeaturesBinPath { get; }
        public string HealthBinPath   { get; }

        public string RawPath         => Path.ChangeExtension(RawBinPath, ".csv");
        public string SignalsPath     => Path.ChangeExtension(SignalsBinPath, ".csv");
        public string FeaturesPath    => Path.ChangeExtension(FeaturesBinPath, ".csv");
        public string HealthPath      => Path.ChangeExtension(HealthBinPath, ".csv");

        public CsvSessionLogger(string logDir, int numCh = 32, int analysisCh = 16)
        {
            _analysisCh = analysisCh > 0 ? analysisCh : 16;
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            SessionDir = Path.Combine(logDir, $"eeg_{ts}");
            Directory.CreateDirectory(SessionDir);

            // ── raw.bin ── (134 B / sample)
            RawBinPath = Path.Combine(SessionDir, $"eeg_{ts}_raw.bin");
            _raw = new BinaryLogQueue<RawLogItem>(RawBinPath, (bw, item) =>
            {
                bw.Write(item.Seq);
                for (int i = 0; i < 32; i++)
                    bw.Write((item.Counts != null && i < item.Counts.Length) ? item.Counts[i] : 0);
                bw.Write(item.Marker);
            });

            // ── signals.bin ── (134 B / sample)
            SignalsBinPath = Path.Combine(SessionDir, $"eeg_{ts}_signals.bin");
            _sig = new BinaryLogQueue<SignalLogItem>(SignalsBinPath, (bw, item) =>
            {
                bw.Write(item.Seq);
                for (int i = 0; i < 32; i++)
                    bw.Write((item.Uv != null && i < item.Uv.Length) ? item.Uv[i] : 0f);
                bw.Write(item.Marker);
            });

            // ── features.bin ── (112 B / sample)
            FeaturesBinPath = Path.Combine(SessionDir, $"eeg_{ts}_features.bin");
            _feat = new BinaryLogQueue<FeatureLogItem>(FeaturesBinPath, (bw, item) =>
            {
                bw.Write(item.IsCca ? (byte)1 : (byte)0);
                bw.Write((byte)0); // reserved
                bw.Write(item.Marker);
                bw.Write(item.Seq);
                bw.Write(item.V1);
                bw.Write(item.V2);
                bw.Write(item.V3);
                bw.Write(item.V4);
                bw.Write(item.AlphaNf);
                bw.Write(item.SsvepNf);
                bw.Write(item.AlphaLeft);
                bw.Write(item.AlphaRight);
                bw.Write(item.SsvepRight14);
                bw.Write(item.SsvepLeft18);
                for (int i = 0; i < 16; i++)
                    bw.Write((item.Alpha != null && i < item.Alpha.Length) ? item.Alpha[i] : 0f);
            });

            // ── health.bin ── (74 B / record)
            HealthBinPath = Path.Combine(SessionDir, $"eeg_{ts}_health.bin");
            byte[] evtBuf = new byte[32];
            _hlth = new BinaryLogQueue<HealthLogItem>(HealthBinPath, (bw, item) =>
            {
                bw.Write(item.WallClockSec);
                bw.Write(item.BoardMs);
                bw.Write(item.Seq);
                bw.Write(item.BoardDrops);
                bw.Write(item.PcGaps);
                bw.Write(item.BoardBad);
                bw.Write(item.BoardMiss);
                bw.Write(item.BoardDspMax);
                bw.Write(item.SrvBad);
                bw.Write(item.FreeHeap);
                bw.Write(item.Marker);

                Array.Clear(evtBuf, 0, 32);
                if (!string.IsNullOrEmpty(item.Event))
                {
                    int bytes = Encoding.UTF8.GetBytes(item.Event, 0, Math.Min(item.Event.Length, 31), evtBuf, 0);
                }
                bw.Write(evtBuf);
            });

            Debug.Log($"[BciCore] Binary session logging started -> {SessionDir} (NumCh={numCh}, AnalysisCh={_analysisCh})");
        }

        // ── Raw frame ────────────────────────────────────────────────────────
        public void WriteRaw(uint seq, int[] counts, ushort marker)
        {
            if (_raw == null || counts == null) return;
            _raw.Enqueue(new RawLogItem(seq, counts, marker));
        }

        // ── PROC frame ────────────────────────────────────────────────────────
        public void WriteSignals(uint seq, float[] uv, ushort marker)
        {
            if (_sig == null || uv == null) return;
            _sig.Enqueue(new SignalLogItem(seq, uv, marker));
        }

        // ── NF sample (features) ──────────────────────────────────────────────
        public void WriteFeatures(uint seq, NfSample nf, float[] alpha)
        {
            if (_feat == null) return;
            _feat.Enqueue(new FeatureLogItem(
                isCca: false, marker: nf.Marker, seq: seq,
                v1: nf.Smi14gt18, v2: nf.Smi18gt14,
                v3: nf.Smi14gt18Shaped, v4: nf.Smi18gt14Shaped,
                aNf: 0f, sNf: 0f, aLeft: 0f, aRight: 0f, sRight14: 0f, sLeft18: 0f,
                alpha: alpha));
        }

        // ── CCA sample (features) ─────────────────────────────────────────────
        public void WriteCcaFeatures(uint seq, CcaSample cca, float[] alpha)
        {
            if (_feat == null) return;
            _feat.Enqueue(new FeatureLogItem(
                isCca: true, marker: cca.Marker, seq: seq,
                v1: cca.FbAgtB, v2: cca.FbBgtA,
                v3: cca.ScoreA, v4: cca.ScoreB,
                aNf: 0f, sNf: 0f, aLeft: 0f, aRight: 0f, sRight14: 0f, sLeft18: 0f,
                alpha: alpha));
        }

        // ── Health / health-event ─────────────────────────────────────────────
        public void WriteHealth(HealthFrame h, long pcGaps, long srvBad, string evt = "")
        {
            if (_hlth == null) return;
            DateTime now = DateTime.Now;
            int wallSec = now.Hour * 3600 + now.Minute * 60 + now.Second;
            _hlth.Enqueue(new HealthLogItem(
                wallSec, h.BoardMs, h.Seq, h.BoardDrops,
                (uint)Math.Min(uint.MaxValue, pcGaps),
                h.BoardBad, h.BoardMiss, h.BoardDspMax,
                (uint)Math.Min(uint.MaxValue, srvBad),
                h.FreeHeap, h.Marker, evt));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _raw?.Dispose();
            _sig?.Dispose();
            _feat?.Dispose();
            _hlth?.Dispose();
        }

        /// <summary>Batch converts all binary files in this session directory to CSV.</summary>
        public async Task ConvertToCsvAsync(Action<float, string> onProgress = null)
        {
            Dispose();
            await BinToCsvConverter.ConvertSessionAsync(SessionDir, onProgress);
        }
    }
}
