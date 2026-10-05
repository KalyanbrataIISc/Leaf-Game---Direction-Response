// ============================================================================
//  DirectCsvSessionLogger.cs - Real-time direct-to-CSV session logger.
//
//  Approach 1 logging: incoming frame data is formatted directly to CSV text
//  on a background thread (integer/float -> string -> commas) using the
//  existing LogQueue<T> infrastructure.  No post-session conversion step is
//  required; the CSV files are ready to read immediately after the session.
//
//  This is the complement of CsvSessionLogger (binary approach):
//    CsvSessionLogger     -> writes compact .bin files, converts to CSV at end
//    DirectCsvSessionLogger -> writes CSV rows in real-time, no conversion step
//
//  Public surface intentionally mirrors CsvSessionLogger so callers can use
//  either without branching on every write call (polymorphism via interface).
// ============================================================================
using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace BciCore
{
    public sealed class DirectCsvSessionLogger : IDisposable
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        readonly LogQueue<RawLogItem>     _raw;
        readonly LogQueue<SignalLogItem>  _sig;
        readonly LogQueue<FeatureLogItem> _feat;
        readonly LogQueue<HealthLogItem>  _hlth;

        readonly int  _analysisCh;
        readonly int  _numCh;
        bool          _disposed;

        public string SessionDir      { get; }
        public string RawPath         { get; }
        public string SignalsPath     { get; }
        public string FeaturesPath    { get; }
        public string HealthPath      { get; }
        public string ConfigPath      { get; }

        // Kept for API parity with CsvSessionLogger (no bin files produced)
        public string RawBinPath      => null;
        public string SignalsBinPath  => null;
        public string FeaturesBinPath => null;
        public string HealthBinPath   => null;

        readonly DateTime _startTime;
        DateTime?  _endTime;
        BciConfig? _config;
        long  _rawCount, _sigCount, _featCount, _hlthCount;
        uint? _firstSeq, _lastSeq, _lastSeenSeq;
        long  _totalGaps, _gapEvents;
        uint  _lastBoardDrops, _lastBoardBad, _lastBoardMiss, _peakDspUs;
        readonly System.Collections.Generic.HashSet<ushort> _markers = new();
        bool _isCca;

        // --- Column helpers ------------------------------------------------
        static string F(float v)  => v.ToString("G6", Inv);
        static string F(double v) => v.ToString("G9", Inv);

        static string RawHeader(int numCh)
        {
            var sb = new StringBuilder("seq");
            for (int c = 1; c <= numCh; c++) sb.Append(",raw").Append(c);
            sb.Append(",marker");
            return sb.ToString();
        }

        static string SignalsHeader(int numCh)
        {
            var sb = new StringBuilder("seq");
            for (int c = 1; c <= numCh; c++) sb.Append(",ch").Append(c).Append("_uv");
            sb.Append(",marker");
            return sb.ToString();
        }

        const string FeaturesHeader =
            "seq,is_cca,marker,v1,v2,v3,v4,alpha_nf,ssvep_nf,alpha_left,alpha_right,ssvep_right14,ssvep_left18," +
            "a1,a2,a3,a4,a5,a6,a7,a8,a9,a10,a11,a12,a13,a14,a15,a16";

        const string HealthHeader =
            "wall_sec,board_ms,seq,board_drops,pc_gaps,board_bad,board_miss,board_dsp_max,srv_bad,free_heap,marker,event";

        // -------------------------------------------------------------------

        public DirectCsvSessionLogger(string logDir, int numCh = 32, int analysisCh = 16)
        {
            _analysisCh = analysisCh > 0 ? analysisCh : 16;
            _numCh      = numCh > 0 ? numCh : 32;
            string ts   = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            SessionDir  = Path.Combine(logDir, $"eeg_{ts}");
            Directory.CreateDirectory(SessionDir);
            _startTime  = DateTime.Now;
            ConfigPath  = Path.Combine(SessionDir, $"eeg_{ts}_config.txt");
            WriteConfigFile("ACTIVE (Recording in progress...)");

            int nc = _numCh;

            RawPath = Path.Combine(SessionDir, $"eeg_{ts}_raw.csv");
            _raw = new LogQueue<RawLogItem>(RawPath, RawHeader(nc), item =>
            {
                var sb = new StringBuilder(nc * 8 + 16);
                sb.Append(item.Seq);
                for (int i = 0; i < nc; i++)
                    sb.Append(',').Append(item.Counts != null && i < item.Counts.Length ? item.Counts[i] : 0);
                sb.Append(',').Append(item.Marker);
                return sb.ToString();
            });

            SignalsPath = Path.Combine(SessionDir, $"eeg_{ts}_signals.csv");
            _sig = new LogQueue<SignalLogItem>(SignalsPath, SignalsHeader(nc), item =>
            {
                var sb = new StringBuilder(nc * 12 + 16);
                sb.Append(item.Seq);
                for (int i = 0; i < nc; i++)
                    sb.Append(',').Append(F(item.Uv != null && i < item.Uv.Length ? item.Uv[i] : 0f));
                sb.Append(',').Append(item.Marker);
                return sb.ToString();
            });

            FeaturesPath = Path.Combine(SessionDir, $"eeg_{ts}_features.csv");
            _feat = new LogQueue<FeatureLogItem>(FeaturesPath, FeaturesHeader, item =>
            {
                var sb = new StringBuilder(256);
                sb.Append(item.Seq).Append(',')
                  .Append(item.IsCca ? 1 : 0).Append(',')
                  .Append(item.Marker).Append(',')
                  .Append(F(item.V1)).Append(',').Append(F(item.V2)).Append(',')
                  .Append(F(item.V3)).Append(',').Append(F(item.V4)).Append(',')
                  .Append(F(item.AlphaNf)).Append(',').Append(F(item.SsvepNf)).Append(',')
                  .Append(F(item.AlphaLeft)).Append(',').Append(F(item.AlphaRight)).Append(',')
                  .Append(F(item.SsvepRight14)).Append(',').Append(F(item.SsvepLeft18));
                for (int i = 0; i < 16; i++)
                    sb.Append(',').Append(F(item.Alpha != null && i < item.Alpha.Length ? item.Alpha[i] : 0f));
                return sb.ToString();
            });

            HealthPath = Path.Combine(SessionDir, $"eeg_{ts}_health.csv");
            _hlth = new LogQueue<HealthLogItem>(HealthPath, HealthHeader, item =>
                $"{item.WallClockSec},{item.BoardMs},{item.Seq},{item.BoardDrops}," +
                $"{item.PcGaps},{item.BoardBad},{item.BoardMiss},{item.BoardDspMax}," +
                $"{item.SrvBad},{item.FreeHeap},{item.Marker},{item.Event}");

            Debug.Log($"[BciCore] Direct-CSV session logging started -> {SessionDir} (NumCh={numCh}, AnalysisCh={_analysisCh})");
        }

        // --- Write methods (same signatures as CsvSessionLogger) -----------

        public void WriteRaw(uint seq, int[] counts, ushort marker)
        {
            if (_raw == null || counts == null) return;
            _rawCount++;
            if (!_firstSeq.HasValue) _firstSeq = seq;
            if (_lastSeenSeq.HasValue && seq > _lastSeenSeq.Value + 1)
            { _totalGaps += (seq - _lastSeenSeq.Value - 1); _gapEvents++; }
            _lastSeenSeq = seq;
            _lastSeq = seq;
            if (marker != 0) _markers.Add(marker);
            _raw.Enqueue(new RawLogItem(seq, counts, marker));
        }

        public void WriteSignals(uint seq, float[] uv, ushort marker)
        {
            if (_sig == null || uv == null) return;
            _sigCount++;
            if (!_firstSeq.HasValue) _firstSeq = seq;
            if (_rawCount == 0)
            {
                if (_lastSeenSeq.HasValue && seq > _lastSeenSeq.Value + 1)
                { _totalGaps += (seq - _lastSeenSeq.Value - 1); _gapEvents++; }
                _lastSeenSeq = seq;
            }
            _lastSeq = seq;
            if (marker != 0) _markers.Add(marker);
            _sig.Enqueue(new SignalLogItem(seq, uv, marker));
        }

        public void WriteFeatures(uint seq, NfSample nf, float[] alpha)
        {
            if (_feat == null) return;
            _featCount++;
            _isCca = false;
            if (nf.Marker != 0) _markers.Add(nf.Marker);
            _feat.Enqueue(new FeatureLogItem(
                isCca: false, marker: nf.Marker, seq: seq,
                v1: nf.Smi14gt18, v2: nf.Smi18gt14,
                v3: nf.Smi14gt18Shaped, v4: nf.Smi18gt14Shaped,
                aNf: 0f, sNf: 0f, aLeft: 0f, aRight: 0f, sRight14: 0f, sLeft18: 0f,
                alpha: alpha));
        }

        public void WriteCcaFeatures(uint seq, CcaSample cca, float[] alpha)
        {
            if (_feat == null) return;
            _featCount++;
            _isCca = true;
            if (cca.Marker != 0) _markers.Add(cca.Marker);
            _feat.Enqueue(new FeatureLogItem(
                isCca: true, marker: cca.Marker, seq: seq,
                v1: cca.FbAgtB, v2: cca.FbBgtA,
                v3: cca.ScoreA, v4: cca.ScoreB,
                aNf: 0f, sNf: 0f, aLeft: 0f, aRight: 0f, sRight14: 0f, sLeft18: 0f,
                alpha: alpha));
        }

        public void WriteHealth(HealthFrame h, long pcGaps, long srvBad, string evt = "")
        {
            if (_hlth == null) return;
            _hlthCount++;
            _lastBoardDrops = h.BoardDrops;
            _lastBoardBad   = h.BoardBad;
            _lastBoardMiss  = h.BoardMiss;
            if (h.BoardDspMax > _peakDspUs) _peakDspUs = h.BoardDspMax;
            if (h.Marker != 0) _markers.Add(h.Marker);
            DateTime now = DateTime.Now;
            int wallSec  = now.Hour * 3600 + now.Minute * 60 + now.Second;
            _hlth.Enqueue(new HealthLogItem(
                wallSec, h.BoardMs, h.Seq, h.BoardDrops,
                (uint)Math.Min(uint.MaxValue, pcGaps),
                h.BoardBad, h.BoardMiss, h.BoardDspMax,
                (uint)Math.Min(uint.MaxValue, srvBad),
                h.FreeHeap, h.Marker, evt));
        }

        public void SetBoardConfig(BciConfig cfg)
        {
            _config = cfg;
            WriteConfigFile("ACTIVE (Config handshake verified, recording...)");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _endTime  = DateTime.Now;
            _raw?.Dispose();
            _sig?.Dispose();
            _feat?.Dispose();
            _hlth?.Dispose();
            WriteConfigFile("COMPLETED");
        }

                void WriteConfigFile(string status)
        {
            try
            {
                ushort fs = _config.HasValue && _config.Value.Fs > 0 ? _config.Value.Fs : (ushort)250;
                byte nch = _config.HasValue && _config.Value.NumCh > 0 ? _config.Value.NumCh : (byte)_numCh;
                float uvpc = _config.HasValue ? _config.Value.UvPerCount : 0.02235174f;
                ushort firLen = _config.HasValue ? _config.Value.FirLen : (ushort)61;
                float dcr = _config.HasValue ? _config.Value.DcR : 0.9995f;
                byte ach = _config.HasValue && _config.Value.AnalysisCh > 0 ? _config.Value.AnalysisCh : (byte)_analysisCh;

                string hemiLStr = _config.HasValue && _config.Value.HemiL != null && _config.Value.HemiL.Length > 0
                    ? "[" + string.Join(", ", _config.Value.HemiL) + "]"
                    : "[0..15]";
                string hemiRStr = _config.HasValue && _config.Value.HemiR != null && _config.Value.HemiR.Length > 0
                    ? "[" + string.Join(", ", _config.Value.HemiR) + "]"
                    : "[16..31]";

                string txMode;
                if (_rawCount > 0 && _sigCount > 0)
                    txMode = "RAW + PROC (Signal Validation / Dual Stream Mode)";
                else if (_rawCount > 0)
                    txMode = "RAW ONLY (Raw ADC Counts Acquisition / Validation Mode)";
                else if (_sigCount > 0)
                    txMode = "PROC ONLY (Filtered Signal Streaming / Live Monitoring Mode)";
                else
                    txMode = "CONNECTING (Handshake established, waiting for data frames)";

                string featStr = _featCount > 0 ? (_isCca ? "Active (CCA)" : "Active (SMI)") : "None";

                DateTime end = _endTime ?? DateTime.Now;
                double durS = (end - _startTime).TotalSeconds;
                int durM = (int)durS / 60;
                int durSec = (int)durS % 60;
                int durH = durM / 60;
                durM %= 60;
                string durFmt = $"{durS:F2} s ({durH:D2}h {durM:D2}m {durSec:D2}s)";

                string firstS = _firstSeq.HasValue ? _firstSeq.Value.ToString() : "N/A";
                string lastS = _lastSeq.HasValue ? _lastSeq.Value.ToString() : "N/A";
                long totalSamples = Math.Max(_rawCount, _sigCount);
                long expectedSamples = (_firstSeq.HasValue && _lastSeq.HasValue) ? (_lastSeq.Value - _firstSeq.Value + 1) : totalSamples;
                double dropRate = (totalSamples + _totalGaps > 0) ? ((double)_totalGaps / (totalSamples + _totalGaps) * 100.0) : 0.0;

                string markersStr = _markers.Count > 0 ? "[" + string.Join(", ", _markers) + "]" : "None";
                string sessionName = Path.GetFileName(SessionDir);

                var sb = new StringBuilder();
                sb.AppendLine(new string('=', 80));
                sb.AppendLine("  BCI SESSION CONFIGURATION & TELEMETRY REPORT");
                sb.AppendLine(new string('=', 80));
                sb.AppendLine($"Session ID             : {sessionName}");
                sb.AppendLine($"Session Status         : {status}");
                sb.AppendLine($"Start Time             : {_startTime:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"End Time               : {(_endTime.HasValue ? _endTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "ACTIVE (In progress)")}");
                sb.AppendLine($"Session Duration       : {durFmt}");
                sb.AppendLine();
                sb.AppendLine("-- HARDWARE & PROTOCOL CONFIGURATION ------------------------------------------");
                sb.AppendLine($"Sampling Rate (SPS)    : {fs} Hz");
                sb.AppendLine($"Channel Count          : {nch} channels");
                sb.AppendLine($"ADS1299 Chips Count    : 4 chips");
                sb.AppendLine($"Scale Factor (uV/count): {uvpc:F8} uV / count");
                sb.AppendLine($"FIR Filter Length      : {firLen} taps");
                sb.AppendLine($"DC Blocker Constant (R): {dcr:F4}");
                sb.AppendLine();
                sb.AppendLine("-- MONTAGE CONFIGURATION ------------------------------------------------------");
                sb.AppendLine($"Analysis Channels      : {ach}");
                sb.AppendLine($"Left Hemisphere Map    : {hemiLStr}");
                sb.AppendLine($"Right Hemisphere Map   : {hemiRStr}");
                sb.AppendLine();
                sb.AppendLine("-- TRANSMISSION MODE & DATA STREAMS -------------------------------------------");
                sb.AppendLine($"Transmission Mode      : {txMode}");
                sb.AppendLine($"Feature Stream Mode    : {featStr}");
                sb.AppendLine();
                sb.AppendLine("Recorded Data Files:");
                sb.AppendLine($"  * Raw ADC Counts     : {_rawCount:N0} samples  | Output: {sessionName}_raw.csv");
                sb.AppendLine($"  * Filtered Signals   : {_sigCount:N0} samples  | Output: {sessionName}_signals.csv");
                sb.AppendLine($"  * Spectral Features  : {_featCount:N0} hops     | Output: {sessionName}_features.csv");
                sb.AppendLine($"  * Health Telemetry   : {_hlthCount:N0} records   | Output: {sessionName}_health.csv");
                sb.AppendLine();
                sb.AppendLine("-- SEQUENCE & LOSS TELEMETRY --------------------------------------------------");
                sb.AppendLine($"First Sequence Number  : {firstS}");
                sb.AppendLine($"Last Sequence Number   : {lastS}");
                sb.AppendLine($"Expected Samples       : {expectedSamples:N0}");
                sb.AppendLine($"Recorded Samples       : {totalSamples:N0}");
                sb.AppendLine($"Missing / Gap Samples  : {_totalGaps:N0} ({dropRate:F4}% drop rate)");
                sb.AppendLine($"Sequence Gap Events    : {_gapEvents:N0}");
                sb.AppendLine($"Board Ring Drops       : {_lastBoardDrops:N0}");
                sb.AppendLine($"Board Bad SPI Frames   : {_lastBoardBad:N0}");
                sb.AppendLine($"Board Missed DRDY      : {_lastBoardMiss:N0}");
                sb.AppendLine($"Peak DSP Time (board)  : {_peakDspUs} us");
                sb.AppendLine($"Event Markers Logged   : {markersStr}");
                sb.AppendLine(new string('=', 80));

                File.WriteAllText(ConfigPath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BciCore] Failed to write session config report: {ex.Message}");
            }
        }
    }
}
