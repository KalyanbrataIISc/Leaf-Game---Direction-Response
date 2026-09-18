// ============================================================================
//  CsvSessionLogger.cs — Writes the same 4-file CSV output as eeg_tcp_server.py.
//
//  Files written per connection run (timestamped):
//    eeg_<ts>_raw.csv      — seq + 32 raw counts + marker
//    eeg_<ts>_signals.csv  — seq + 32 filtered µV + marker    (from PROC frames)
//    eeg_<ts>_features.csv — seq, marker, CCA/SMI scores, alpha[analysisCh]
//    eeg_<ts>_health.csv   — wall_clock, board_ms, board_drops, pc_gaps, ... (1 Hz + markers)
//
//  All I/O is on a dedicated background thread via LogQueue; calling code
//  never blocks.
// ============================================================================
using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace BciCore
{
    public sealed class CsvSessionLogger : IDisposable
    {
        readonly LogQueue _raw;
        readonly LogQueue _sig;
        LogQueue          _feat;
        readonly LogQueue _hlth;
        readonly int      _analysisCh;
        readonly object   _featLock = new object();

        public string SessionDir   { get; }
        public string RawPath      { get; }
        public string SignalsPath  { get; }
        public string FeaturesPath { get; }
        public string HealthPath   { get; }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public CsvSessionLogger(string logDir, int numCh = 32, int analysisCh = 16, bool ssvepPerChannel = false)
        {
            _analysisCh = analysisCh > 0 ? analysisCh : 16;
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            SessionDir = Path.Combine(logDir, $"eeg_{ts}");
            Directory.CreateDirectory(SessionDir);

            // ── raw ──
            RawPath = Path.Combine(SessionDir, $"eeg_{ts}_raw.csv");
            string rawHdr = "seq," + Join("raw", numCh) + ",marker";
            _raw = new LogQueue(RawPath, rawHdr);

            // ── signals ──
            SignalsPath = Path.Combine(SessionDir, $"eeg_{ts}_signals.csv");
            string sigHdr = "seq," + Join("filt", numCh) + ",marker";
            _sig = new LogQueue(SignalsPath, sigHdr);

            // ── features (lazy initialised on first sample to auto-detect CCA vs SMI header) ──
            FeaturesPath = Path.Combine(SessionDir, $"eeg_{ts}_features.csv");
            _feat = null;

            // ── health ──
            HealthPath = Path.Combine(SessionDir, $"eeg_{ts}_health.csv");
            const string hlthHdr = "wall_clock,board_ms,seq,board_drops,pc_gaps,board_bad,"
                                 + "board_miss,board_dspmax_us,srv_bad,free_heap,marker,event";
            _hlth = new LogQueue(HealthPath, hlthHdr);

            Debug.Log($"[BciCore] Logging session started -> {SessionDir} (NumCh={numCh}, AnalysisCh={_analysisCh})");
        }

        void EnsureFeatureHeader(bool isCca)
        {
            if (_feat != null) return;
            lock (_featLock)
            {
                if (_feat != null) return;
                string featHdr = isCca
                    ? "seq,marker,fb_AgtB,fb_BgtA,score_A,score_B,"
                    + "alphaNF,ssvepNF,alphaLeft,alphaRight,ssvepRight14,ssvepLeft18,"
                    + JoinIndexed("alpha", _analysisCh)
                    : "seq,marker,smi_14gt18,smi_18gt14,smi_14gt18_shaped,smi_18gt14_shaped,"
                    + "alphaNF,ssvepNF,alphaLeft,alphaRight,ssvepRight14,ssvepLeft18,"
                    + JoinIndexed("alpha", _analysisCh);
                _feat = new LogQueue(FeaturesPath, featHdr);
                Debug.Log($"[BciCore] features.csv header initialised (Mode: {(isCca ? "CCA" : "SMI")}, AnalysisCh: {_analysisCh})");
            }
        }

        // ── Raw frame ────────────────────────────────────────────────────────
        public void WriteRaw(uint seq, int[] counts, ushort marker)
        {
            if (_raw == null || counts == null) return;
            _raw.Enqueue($"{seq},{IntArr(counts)},{marker}");
        }

        // ── PROC frame (signals.csv) ─────────────────────────────────────────
        public void WriteSignals(uint seq, float[] uv, ushort marker)
        {
            if (_sig == null || uv == null) return;
            _sig.Enqueue($"{seq},{FloatArr(uv)},{marker}");
        }

        // ── NF sample (features.csv) ─────────────────────────────────────────
        public void WriteFeatures(uint seq, NfSample nf, float[] alpha)
        {
            EnsureFeatureHeader(isCca: false);
            string alphaStr = (alpha != null && alpha.Length > 0) ? FloatArr(alpha) : Zeros(_analysisCh);
            _feat.Enqueue(
                $"{seq},{nf.Marker},{F(nf.Smi14gt18)},{F(nf.Smi18gt14)},"
              + $"{F(nf.Smi14gt18Shaped)},{F(nf.Smi18gt14Shaped)},"
              + "0.000000,0.000000,0.000000,0.000000,0.000000,0.000000,"
              + alphaStr);
        }

        // ── CCA sample (features.csv) ────────────────────────────────────────
        public void WriteCcaFeatures(uint seq, CcaSample cca, float[] alpha)
        {
            EnsureFeatureHeader(isCca: true);
            string alphaStr = (alpha != null && alpha.Length > 0) ? FloatArr(alpha) : Zeros(_analysisCh);
            _feat.Enqueue(
                $"{seq},{cca.Marker},{F(cca.FbAgtB)},{F(cca.FbBgtA)},"
              + $"{F(cca.ScoreA)},{F(cca.ScoreB)},"
              + "0.000000,0.000000,0.000000,0.000000,0.000000,0.000000,"
              + alphaStr);
        }

        // ── Health / health-event ────────────────────────────────────────────
        public void WriteHealth(HealthFrame h, long pcGaps, long srvBad, string evt = "")
        {
            if (_hlth == null) return;
            _hlth.Enqueue(
                $"{DateTime.Now:HH:mm:ss},{h.BoardMs},{h.Seq},{h.BoardDrops},"
              + $"{pcGaps},{h.BoardBad},{h.BoardMiss},{h.BoardDspMax},"
              + $"{srvBad},{h.FreeHeap},{h.Marker},{evt}");
        }

        // ── Dispose ──────────────────────────────────────────────────────────
        public void Dispose()
        {
            EnsureFeatureHeader(isCca: true);
            _raw?.Dispose(); _sig?.Dispose(); _feat?.Dispose(); _hlth?.Dispose();
        }

        // ── Helpers ──────────────────────────────────────────────────────────
        static string F(float v)   => v.ToString("F6", Inv);
        static string Join(string prefix, int n)
        {
            var sb = new System.Text.StringBuilder(n * 8);
            for (int i = 0; i < n; i++) { if (i > 0) sb.Append(','); sb.Append(prefix).Append(i + 1); }
            return sb.ToString();
        }
        static string JoinIndexed(string prefix, int n) => Join(prefix, n);
        static string IntArr(int[] a)
        {
            var sb = new System.Text.StringBuilder(a.Length * 8);
            for (int i = 0; i < a.Length; i++) { if (i > 0) sb.Append(','); sb.Append(a[i]); }
            return sb.ToString();
        }
        static string FloatArr(float[] a)
        {
            var sb = new System.Text.StringBuilder(a.Length * 12);
            for (int i = 0; i < a.Length; i++) { if (i > 0) sb.Append(','); sb.Append(F(a[i])); }
            return sb.ToString();
        }
        static string Zeros(int n)
        {
            var sb = new System.Text.StringBuilder(n * 10);
            for (int i = 0; i < n; i++) { if (i > 0) sb.Append(','); sb.Append("0.000000"); }
            return sb.ToString();
        }
    }
}
