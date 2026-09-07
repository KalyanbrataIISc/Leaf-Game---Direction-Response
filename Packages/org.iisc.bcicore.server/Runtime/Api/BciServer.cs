// ============================================================================
//  BciServer.cs — Single public API surface for the BciCore package.
//
//  Usage (from LeafGameController or CalibrationScreen):
//
//    BciServer.StartServer();           // begins listening on port 5005
//    BciServer.EnterCalibrationMode();  // routes RAW/PROC/ALPHA/SSVEP events
//    BciServer.ExitCalibrationMode();   // stops routing heavy calibration events
//    BciServer.SendMarker(20);          // sends CMD frame to ESP32
//    BciServer.StopServer();            // closes listener, tears down threads
//
//  All events are raised on the Unity main thread (via MainThreadDispatcher).
// ============================================================================
using System;
using System.IO;
using UnityEngine;

namespace BciCore
{
    public static class BciServer
    {
        // ── Configuration ─────────────────────────────────────────────────────
        public const int DefaultPort    = 5005;
        public const string DefaultLogSubDir = "BciCoreLogs";

        // ── State ─────────────────────────────────────────────────────────────
        public static BciConfig        Config          { get; private set; }
        public static ConnectionState  State           { get; private set; } = ConnectionState.Disconnected;
        public static bool             CalibrationMode { get; private set; }
        public static bool             IsUsingProcessedSignals { get; internal set; }

        // ── Shared ring buffer (accessed by CalibrationScreen) ────────────────
        public static ChannelRingBuffer RingBuffer { get; private set; }

        // ── Session logger ────────────────────────────────────────────────────
        static CsvSessionLogger _logger;
        public static string CurrentSessionDir => _logger?.SessionDir;
        static bool             _enableLogging = true;
        static string           _logDir;
        // Buffered alpha for features.csv correlation (seq → alpha[])
        static System.Collections.Generic.Dictionary<uint, float[]> _pendingAlpha
            = new System.Collections.Generic.Dictionary<uint, float[]>(256);

        // ── Internal listener ─────────────────────────────────────────────────
        static EspTcpListener _listener;

        // Running stat counters carried across HEALTH frames (for logger)
        static long _pcGaps;
        static long _srvBad;

        // ── Direct logger helpers (called from background process thread) ───
        internal static void LogRaw(uint seq, int[] counts, ushort marker) => _logger?.WriteRaw(seq, counts, marker);
        internal static void LogProc(uint seq, float[] uv, ushort marker) => _logger?.WriteSignals(seq, uv, marker);

        /// <summary>
        /// Log a software-side trigger-sent event to health.csv immediately at the moment
        /// the trigger is dispatched — independent of firmware echo-back or marker latching.
        /// Safe to call from the main thread; the LogQueue handles async I/O.
        /// </summary>
        public static void LogTriggerSent(string name, int code)
        {
            _logger?.WriteHealth(
                new HealthFrame { Seq = 0, Marker = (ushort)(code & 0xFFFF) },
                _pcGaps, _srvBad,
                $"trigger_sent:{name}");
            Debug.Log($"[BciCore] Trigger sent → '{name}' = {code}");
        }

        // ── Public Events (main thread) ───────────────────────────────────────

        /// <summary>ESP32 sent a HELLO frame; Config is updated.</summary>
        public static event Action<BciConfig>              OnHello;

        /// <summary>RAW frame: float[] µV values per channel. Only during CalibrationMode.</summary>
        public static event Action<float[], ushort, uint>  OnRawFrame;

        /// <summary>RAW frame: raw int32 counts per channel. Only during CalibrationMode.</summary>
        public static event Action<int[], ushort, uint>    OnRawCounts;

        /// <summary>PROC frame: board-filtered µV per channel. Only during CalibrationMode.</summary>
        public static event Action<float[], ushort, uint>  OnProcFrame;

        /// <summary>ANALYSIS frame: alpha band power per analysis channel. Only during CalibrationMode.</summary>
        public static event Action<AlphaFrame>             OnAlpha;

        /// <summary>SSVEP frame: power and SNR at both frequencies. Only during CalibrationMode.</summary>
        public static event Action<SsvepFrame>             OnSsvep;

        /// <summary>NF frame: smi_14gt18_shaped, smi_18gt14_shaped. Always active.</summary>
        public static event Action<NfSample>               OnNfSample;

        /// <summary>HEALTH telemetry (board uptime, drops, heap). Always active.</summary>
        public static event Action<HealthFrame>            OnHealth;

        /// <summary>Board latched a marker code. Always active.</summary>
        public static event Action<ushort, uint>           OnMarker;

        /// <summary>ML result frame. Always active.</summary>
        public static event Action<MlResult>               OnMlResult;

        /// <summary>Per-second stats snapshot. Always active.</summary>
        public static event Action<StatsSnapshot>          OnStats;

        /// <summary>Connection state changed (Listening / Connected / Disconnected).</summary>
        public static event Action<ConnectionState>        OnConnectionStateChanged;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        /// <summary>
        /// Start the TCP server and begin listening for the ESP32.
        /// Optionally configure the log directory (pass null to use default).
        /// </summary>
        public static void StartServer(int port = DefaultPort, string logDir = null, bool enableLogging = true)
        {
            if (_listener != null) return;   // already running

            _enableLogging = enableLogging;
            _logDir = string.IsNullOrWhiteSpace(logDir)
                ? Path.Combine(Application.persistentDataPath, DefaultLogSubDir)
                : logDir;

            Config      = new BciConfig { Fs = 250, NumCh = 32, UvPerCount = 0.02235174f };
            RingBuffer  = new ChannelRingBuffer(Config.NumCh, windowSamples: 2500);
            _pcGaps = 0; _srvBad = 0;

            _listener = new EspTcpListener();

            // Wire internal callbacks
            _listener.OnHello       = InternalOnHello;
            _listener.OnRawFrame    = InternalOnRawFrame;
            _listener.OnRawCounts   = InternalOnRawCounts;
            _listener.OnProcFrame   = InternalOnProcFrame;
            _listener.OnAlpha       = InternalOnAlpha;
            _listener.OnSsvep       = InternalOnSsvep;
            _listener.OnNfSample    = InternalOnNf;
            _listener.OnHealth      = InternalOnHealth;
            _listener.OnMarker      = InternalOnMarker;
            _listener.OnMlResult    = InternalOnMl;
            _listener.OnStats       = InternalOnStats;
            _listener.OnStateChanged = InternalOnStateChanged;

            _listener.StartListening(port);
            Debug.Log($"[BciCore] TCP server started on port {port}. Waiting for ESP32…");
        }

        /// <summary>Enable RAW/PROC/ANALYSIS/SSVEP event routing (for calibration display).</summary>
        public static void EnterCalibrationMode()
        {
            CalibrationMode = true;
            Debug.Log("[BciCore] Calibration mode ON — raw/proc/alpha/ssvep events active.");
        }

        /// <summary>Disable heavy calibration event routing (during trials).</summary>
        public static void ExitCalibrationMode()
        {
            CalibrationMode = false;
            Debug.Log("[BciCore] Calibration mode OFF.");
        }

        /// <summary>Send a marker command frame to the ESP32 (CMD, type 0x08).</summary>
        public static bool SendMarker(int code)
        {
            if (_listener == null) return false;
            return _listener.SendMarker((ushort)(code & 0xFFFF));
        }

        /// <summary>
        /// Returns true if the board is currently streaming NF frames.
        /// Useful for diagnosing silent NF stalls in BciCore mode.
        /// </summary>
        public static bool IsReceivingNf => _listener?.IsReceivingNf ?? false;

        /// <summary>Stop listening and release all resources.</summary>
        public static void StopServer()
        {
            _listener?.Dispose();
            _listener = null;
            _logger?.Dispose();
            _logger = null;
            IsUsingProcessedSignals = false;
            State = ConnectionState.Disconnected;
            Debug.Log("[BciCore] Server stopped.");
        }

        // ── Internal callbacks (main thread after dispatch) ───────────────────

        static void InternalOnHello(BciConfig cfg)
        {
            Config = cfg;
            RingBuffer?.Resize(cfg.NumCh);

            // Start a new log session on every new connection
            if (_enableLogging)
            {
                _logger?.Dispose();
                _pendingAlpha.Clear();
                _logger = new CsvSessionLogger(_logDir, cfg.NumCh);
            }

            OnHello?.Invoke(cfg);
        }

        static void InternalOnRawFrame(float[] uv, ushort marker, uint seq)
            => OnRawFrame?.Invoke(uv, marker, seq);

        static void InternalOnRawCounts(int[] counts, ushort marker, uint seq)
            => OnRawCounts?.Invoke(counts, marker, seq);

        static void InternalOnProcFrame(float[] uv, ushort marker, uint seq)
            => OnProcFrame?.Invoke(uv, marker, seq);

        static void InternalOnAlpha(AlphaFrame af)
        {
            // Buffer for features.csv correlation with the NF frame
            if (_pendingAlpha.Count > 256) _pendingAlpha.Clear();
            _pendingAlpha[af.Seq] = af.Power;
            OnAlpha?.Invoke(af);
        }

        static void InternalOnSsvep(SsvepFrame sf)
            => OnSsvep?.Invoke(sf);

        static void InternalOnNf(NfSample nf)
        {
            // Pop buffered alpha for this seq
            _pendingAlpha.TryGetValue(nf.Seq, out float[] alpha);
            _pendingAlpha.Remove(nf.Seq);
            _logger?.WriteFeatures(nf.Seq, nf, alpha);
            OnNfSample?.Invoke(nf);
        }

        static void InternalOnHealth(HealthFrame h)
        {
            _logger?.WriteHealth(h, _pcGaps, _srvBad);
            OnHealth?.Invoke(h);
        }

        static void InternalOnMarker(ushort code, uint seq)
        {
            // Log marker as a health event for the health.csv
            _logger?.WriteHealth(new HealthFrame { Seq = seq, Marker = code }, _pcGaps, _srvBad, "marker");
            OnMarker?.Invoke(code, seq);
        }

        static void InternalOnMl(MlResult ml)
            => OnMlResult?.Invoke(ml);

        static void InternalOnStats(StatsSnapshot snap)
        {
            _pcGaps = snap.GapsRaw + snap.GapsProc;
            _srvBad = snap.BadFrames;
            OnStats?.Invoke(snap);
        }

        static void InternalOnStateChanged(ConnectionState s)
        {
            State = s;
            if (s == ConnectionState.Connected && _enableLogging && _logger == null)
            {
                _logger = new CsvSessionLogger(_logDir, Config.NumCh);
            }
            OnConnectionStateChanged?.Invoke(s);
        }
    }
}
