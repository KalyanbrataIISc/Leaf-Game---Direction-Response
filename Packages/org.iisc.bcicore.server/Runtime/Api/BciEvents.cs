// ============================================================================
//  BciEvents.cs — All event payload structs/classes for BciServer events.
//  Value types (structs) used wherever possible to avoid GC pressure on the
//  hot NF/RAW/PROC paths.
// ============================================================================
namespace BciCore
{
    // ── Connection state ─────────────────────────────────────────────────────
    public enum ConnectionState { Disconnected, Listening, Connected }

    // ── Board configuration (from HELLO frame) ────────────────────────────────
    public struct BciConfig
    {
        public ushort Fs;          // sampling rate (Hz)
        public byte   NumCh;       // number of channels
        public float  UvPerCount;  // µV / ADC count
        public ushort FirLen;      // FIR filter length (for info only)
        public float  DcR;         // DC-blocker pole radius (for info only)
        // Montage (optional — may be zero-length if board did not send it)
        public byte   AnalysisCh;
        public byte[] HemiL;       // left hemisphere channel indices
        public byte[] HemiR;       // right hemisphere channel indices
    }

    // ── Neurofeedback sample (NF frame, type 0x05) ───────────────────────────
    public struct NfSample
    {
        public float Smi14gt18;         // raw SMI: 14 Hz > 18 Hz
        public float Smi18gt14;         // raw SMI: 18 Hz > 14 Hz
        public float Smi14gt18Shaped;   // tanh-shaped / median-subtracted
        public float Smi18gt14Shaped;
        public int   SampleCount;
        public ushort Marker;
        public uint  Seq;
    }

    // ── SSVEP frame (type 0x04) ───────────────────────────────────────────────
    public struct SsvepFrame
    {
        public float[] PowerA;   // per-channel power at freq A (length 8)
        public float[] SnrA;     // per-channel SNR  at freq A
        public float[] PowerB;   // per-channel power at freq B
        public float[] SnrB;     // per-channel SNR  at freq B
        public uint    Seq;
    }

    // ── Health telemetry (HEALTH frame, type 0x06) ───────────────────────────
    public struct HealthFrame
    {
        // Always present (14-byte legacy payload)
        public uint   BoardMs;      // board uptime ms
        public uint   BoardDrops;   // ring-buffer drop count
        public uint   FreeHeap;     // free heap bytes
        public ushort Marker;       // currently latched marker
        // Extended (26-byte payload only)
        public uint   BoardBad;     // bad SPI-status frames
        public uint   BoardMiss;    // missed DRDY events
        public uint   BoardDspMax;  // peak DSP burst µs
        public uint   Seq;
    }

    // ── Per-second statistics snapshot (raised at ~1 Hz) ─────────────────────
    public struct StatsSnapshot
    {
        public int  Sps;           // true hardware sampling rate (SPS)
        public int  WireFps;       // total wire frames / second
        public long GapsRaw;       // cumulative RAW sequence gaps
        public long GapsProc;      // cumulative PROC sequence gaps
        public long BadFrames;     // cumulative malformed frames rejected
        public long BoardDrops;    // board ring drops (from HEALTH)
        public uint FreeHeap;      // most recent heap value
        public ushort Marker;      // most recent marker
        public uint BoardBad;
        public uint BoardMiss;
        public uint BoardDspMax;   // peak DSP burst µs (from HEALTH)
    }

    // ── ML result (ML frame, type 0x09) ──────────────────────────────────────
    public struct MlResult
    {
        public byte  PredClass;
        public float Confidence;
        public float LatencyMs;
        public uint  Seq;
    }

    // ── Alpha (ANALYSIS frame, type 0x03) ────────────────────────────────────
    public struct AlphaFrame
    {
        public float[] Power;  // length = analysis_ch (typically 8)
        public uint    Seq;
    }
}
