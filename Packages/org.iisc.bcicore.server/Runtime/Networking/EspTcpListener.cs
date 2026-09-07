// ============================================================================
//  EspTcpListener.cs — Unity-side TCP server (PC is server; ESP32 connects in).
//
//  Architecture mirrors eeg_tcp_server.py exactly:
//    • _recvThread  — thin: reads bytes, puts (type,seq,payload) on _frameQueue.
//                     Never does heavy processing; never calls Unity APIs.
//    • _processThread — drains _frameQueue, dispatches to internal callbacks,
//                     updates stats counters. Also never calls Unity APIs.
//    • MainThreadDispatcher.Enqueue() — marshals every event to the main thread
//                     before raising the public BciServer events.
//
//  Reconnect: after a client disconnects, EspTcpListener stays listening and
//  accepts the next connection automatically, resetting all per-connection state.
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace BciCore
{
    internal sealed class EspTcpListener : IDisposable
    {
        // ── Configuration ────────────────────────────────────────────────────
        const float RecvTimeoutSec = 1.5f;
        const int   FrameQueueMax  = 4096;

        // ── External callbacks (set by BciServer, called on main thread) ─────
        public Action<BciConfig>                OnHello;
        public Action<float[], ushort, uint>    OnRawFrame;    // (uvValues, marker, seq)
        public Action<int[], ushort, uint>      OnRawCounts;   // (counts, marker, seq)
        public Action<float[], ushort, uint>    OnProcFrame;   // (uvValues, marker, seq)
        public Action<AlphaFrame>               OnAlpha;
        public Action<SsvepFrame>               OnSsvep;
        public Action<NfSample>                 OnNfSample;
        public Action<HealthFrame>              OnHealth;
        public Action<ushort, uint>             OnMarker;
        public Action<MlResult>                 OnMlResult;
        public Action<StatsSnapshot>            OnStats;
        public Action<ConnectionState>          OnStateChanged;

        // ── State ─────────────────────────────────────────────────────────────
        TcpListener        _listener;
        TcpClient          _client;
        NetworkStream      _stream;
        readonly object    _sendLock = new object();
        volatile bool      _disposed;
        volatile bool      _running;
        Thread             _acceptThread;

        // Config from HELLO
        BciConfig _cfg = new BciConfig { Fs = 250, NumCh = 32, UvPerCount = 0.02235174f };
        int       _analysisCh = 8;

        // Last time an NF frame was received (UnityElapsed seconds) — for IsReceivingNf
        volatile double _lastNfSec = -1.0;

        /// <summary>True if an NF frame has arrived within the last 2 s.</summary>
        public bool IsReceivingNf => (_lastNfSec > 0) && ((UnityElapsed() - _lastNfSec) < 2.0);

        // ── Start / Stop ──────────────────────────────────────────────────────

        public void StartListening(int port)
        {
            if (_running) return;
            _running = true;
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start(backlog: 5);

            _acceptThread = new Thread(() => AcceptLoop(port)) { IsBackground = true, Name = "BciCore-accept" };
            _acceptThread.Start();
        }

        public void StopListening()
        {
            _disposed = true;
            _running  = false;
            try { _listener?.Stop(); } catch { }
            try { _client?.Close();  } catch { }
        }

        public void Dispose() => StopListening();

        // ── Accept loop (runs until disposed) ────────────────────────────────

        void AcceptLoop(int port)
        {
            NotifyState(ConnectionState.Listening);
            while (!_disposed)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (Exception e)
                {
                    if (_disposed) break;
                    Debug.LogWarning($"[BciCore] Accept error or network reset: {e.Message}. Re-binding listener in 1s...");
                    Thread.Sleep(1000);
                    try { _listener?.Stop(); } catch { }
                    try
                    {
                        _listener = new TcpListener(IPAddress.Any, port);
                        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                        _listener.Start(backlog: 5);
                        NotifyState(ConnectionState.Listening);
                    }
                    catch (Exception restartEx)
                    {
                        Debug.LogWarning($"[BciCore] Re-bind failed: {restartEx.Message}");
                    }
                    continue;
                }

                if (client == null) continue;

                client.NoDelay = true;
                client.ReceiveTimeout = 2000;
                client.SendTimeout    = 1000;
                try
                {
                    client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                }
                catch { }

                _client = client;
                NotifyState(ConnectionState.Connected);
                Debug.Log($"[BciCore] ESP32 connected from {((IPEndPoint)client.Client.RemoteEndPoint).Address}");

                HandleClient(client);

                _client = null;
                NotifyState(ConnectionState.Listening);
                Debug.Log("[BciCore] ESP32 disconnected. Waiting for reconnect...");
            }
            NotifyState(ConnectionState.Disconnected);
        }

        // ── Per-connection handler ────────────────────────────────────────────

        void HandleClient(TcpClient client)
        {
            using (client)
            {
                Socket socket = client.Client;
                var stream = client.GetStream();
                _stream = stream;

                // Per-connection state (mirrors Python handle() locals)
                var frameQ   = new BlockingCollection<(FrameType, uint, byte[])>(FrameQueueMax);
                var recvDone = new ManualResetEventSlim(false);

                // ── Receive thread ────────────────────────────────────────────
                var recvThread = new Thread(() =>
                {
                    try   { RecvLoop(socket, frameQ); }
                    catch { }
                    finally
                    {
                        frameQ.CompleteAdding();
                        recvDone.Set();
                    }
                }) { IsBackground = true, Name = "BciCore-recv" };
                recvThread.Start();

                // ── Process loop ──────────────────────────────────────────────
                ProcessLoop(frameQ, recvDone);
                recvThread.Join(1000);
                _stream = null;
            }
        }

        // ── Receive thread: only reads bytes, never processes ─────────────────

        void RecvLoop(Socket socket, BlockingCollection<(FrameType, uint, byte[])> q)
        {
            // Resync to first 0xAA 0x55
            if (!ResyncMagic(socket)) return;

            bool firstFrame = true;
            var  hdrBuf     = new byte[FrameProtocol.HeaderSize];

            while (!_disposed)
            {
                // Read header
                if (firstFrame)
                {
                    hdrBuf[0] = FrameProtocol.Magic0;
                    hdrBuf[1] = FrameProtocol.Magic1;
                    if (!ReadExact(socket, hdrBuf, 2, FrameProtocol.HeaderSize - 2)) return;
                    firstFrame = false;
                }
                else
                {
                    if (!ReadExact(socket, hdrBuf, 0, FrameProtocol.HeaderSize)) return;
                    if (hdrBuf[0] != FrameProtocol.Magic0 || hdrBuf[1] != FrameProtocol.Magic1)
                    {
                        if (!ResyncMagic(socket)) return;
                        firstFrame = true;
                        continue;
                    }
                }

                if (!FrameProtocol.TryParseHeader(hdrBuf, 0, out FrameType ftype, out uint seq, out ushort plen))
                    continue;

                byte[] payload = new byte[plen];
                if (plen > 0 && !ReadExact(socket, payload, 0, plen)) return;

                try { q.Add((ftype, seq, payload)); }
                catch (InvalidOperationException) { return; }   // queue completed
            }
        }

        // ── Process loop: decodes frames, fires events ─────────────────────

        void ProcessLoop(BlockingCollection<(FrameType, uint, byte[])> q, ManualResetEventSlim recvDone)
        {
            // Per-connection stats counters
            long gapsRaw = 0, gapsProc = 0, badFrames = 0;
            uint? expRaw = null, expProc = null;

            long   frameCnt       = 0;
            uint?  lastSampleSeq  = null;
            uint?  curSampleSeq   = null;
            double lastPerfSec    = UnityElapsed();
            double lastRawSec     = -1.0;

            // Health state (updated from HEALTH frames)
            uint hBoardDrops = 0, hFreeHeap = 0, hBoardBad = 0, hBoardMiss = 0, hBoardDspMax = 0;
            ushort hMarker = 0;

            while (!_disposed)
            {
                (FrameType ftype, uint seq, byte[] payload) frame;
                try
                {
                    if (!q.TryTake(out frame, millisecondsTimeout: 2000))
                    {
                        if (!recvDone.IsSet) continue;
                        break;
                    }
                }
                catch { break; }

                var (ftype, seq, payload) = frame;
                int plen = payload.Length;
                int nch  = _cfg.NumCh;
                int ach  = _analysisCh;

                // ── Sequence gap tracking ─────────────────────────────────────
                if (ftype == FrameType.Raw)
                {
                    if (expRaw.HasValue && seq != expRaw.Value)
                        gapsRaw += (long)((seq - expRaw.Value) & 0xFFFFFFFF);
                    expRaw = (seq + 1) & 0xFFFFFFFF;
                    curSampleSeq = seq;
                }
                else if (ftype == FrameType.Proc)
                {
                    if (expProc.HasValue && seq != expProc.Value)
                        gapsProc += (long)((seq - expProc.Value) & 0xFFFFFFFF);
                    expProc = (seq + 1) & 0xFFFFFFFF;
                    curSampleSeq = seq;
                }

                // ── Frame dispatch ────────────────────────────────────────────
                switch (ftype)
                {
                    case FrameType.Hello:
                        _cfg = FrameProtocol.ParseHello(payload);
                        _analysisCh = _cfg.AnalysisCh > 0 ? _cfg.AnalysisCh : 8;
                        BciServer.RingBuffer?.Resize(_cfg.NumCh);
                        var cfgCopy = _cfg;
                        MainThreadDispatcher.Enqueue(() => OnHello?.Invoke(cfgCopy));
                        break;

                    case FrameType.Raw:
                        var counts = FrameProtocol.ParseRawCounts(payload, nch, out ushort rawMarker);
                        if (counts != null)
                        {
                            lastRawSec = UnityElapsed();
                            BciServer.IsUsingProcessedSignals = false;

                            BciServer.LogRaw(seq, counts, rawMarker);

                            // Convert to µV and push to ring buffer
                            float uvpc = _cfg.UvPerCount > 0 ? _cfg.UvPerCount : 0.02235174f;
                            var uv = new float[nch];
                            for (int i = 0; i < nch; i++) uv[i] = counts[i] * uvpc;
                            BciServer.RingBuffer?.Write(uv);

                            if (BciServer.CalibrationMode)
                            {
                                var uvCopy  = uv;
                                var cntCopy = counts;
                                uint seqCopy = seq; ushort mkCopy = rawMarker;
                                MainThreadDispatcher.Enqueue(() =>
                                {
                                    OnRawCounts?.Invoke(cntCopy, mkCopy, seqCopy);
                                    OnRawFrame?.Invoke(uvCopy, mkCopy, seqCopy);
                                });
                            }
                        }
                        else { badFrames++; }
                        break;

                    case FrameType.Proc:
                        var proc = FrameProtocol.ParseProcUv(payload, nch, out ushort procMarker);
                        if (proc != null)
                        {
                            BciServer.LogProc(seq, proc, procMarker);

                            // If raw frames are not active (either not received yet or none in last 0.5s),
                            // route processed frames to RingBuffer so waveforms and FFT plot live.
                            bool rawActive = (lastRawSec > 0 && (UnityElapsed() - lastRawSec) < 0.5);
                            if (!rawActive)
                            {
                                BciServer.RingBuffer?.Write(proc);
                                BciServer.IsUsingProcessedSignals = true;
                            }

                            if (BciServer.CalibrationMode)
                            {
                                var procCopy = proc; uint seqCopy = seq; ushort mkCopy = procMarker;
                                MainThreadDispatcher.Enqueue(() => OnProcFrame?.Invoke(procCopy, mkCopy, seqCopy));
                            }
                        }
                        else { badFrames++; }
                        break;

                    case FrameType.Analysis:
                        var alpha = FrameProtocol.ParseAlpha(payload, ach);
                        if (alpha != null)
                        {
                            var af = new AlphaFrame { Power = alpha, Seq = seq };
                            if (BciServer.CalibrationMode)
                                MainThreadDispatcher.Enqueue(() => OnAlpha?.Invoke(af));
                        }
                        else { badFrames++; }
                        break;

                    case FrameType.Ssvep:
                        if (plen == ach * 4 * 4)
                        {
                            var sf = FrameProtocol.ParseSsvep(payload, ach, seq);
                            if (BciServer.CalibrationMode)
                                MainThreadDispatcher.Enqueue(() => OnSsvep?.Invoke(sf));
                        }
                        else { badFrames++; }
                        break;

                    case FrameType.Nf:
                        if (plen == 12 || plen == 14 || plen == 22)
                        {
                            var nf = FrameProtocol.ParseNf(payload, seq);
                            _lastNfSec = UnityElapsed();
                            MainThreadDispatcher.Enqueue(() => OnNfSample?.Invoke(nf));
                        }
                        else { badFrames++; }
                        break;

                    case FrameType.Health:
                        if (plen == 14 || plen == 26)
                        {
                            var hf = FrameProtocol.ParseHealth(payload, seq);
                            hBoardDrops = hf.BoardDrops; hFreeHeap  = hf.FreeHeap;
                            hMarker     = hf.Marker;      hBoardBad  = hf.BoardBad;
                            hBoardMiss  = hf.BoardMiss;  hBoardDspMax = hf.BoardDspMax;
                            MainThreadDispatcher.Enqueue(() => OnHealth?.Invoke(hf));
                        }
                        else { badFrames++; }
                        break;

                    case FrameType.Marker:
                        if (plen == 2)
                        {
                            ushort code = FrameProtocol.ReadU16LE(payload, 0);
                            uint seqCopy = seq;
                            MainThreadDispatcher.Enqueue(() => OnMarker?.Invoke(code, seqCopy));
                        }
                        else { badFrames++; }
                        break;

                    case FrameType.Ml:
                        if (plen == 9)
                        {
                            var ml = FrameProtocol.ParseMl(payload, seq);
                            MainThreadDispatcher.Enqueue(() => OnMlResult?.Invoke(ml));
                        }
                        else { badFrames++; }
                        break;

                    default:
                        badFrames++;
                        break;
                }

                frameCnt++;

                // ── Per-second stats ──────────────────────────────────────────
                double now = UnityElapsed();
                double dt  = now - lastPerfSec;
                if (dt >= 1.0)
                {
                    int sps = (lastSampleSeq.HasValue && curSampleSeq.HasValue
                        ? (int)Math.Round(((long)((curSampleSeq.Value - lastSampleSeq.Value) & 0xFFFFFFFF)) / dt)
                        : (int)Math.Round(frameCnt / dt));

                    var snap = new StatsSnapshot
                    {
                        Sps        = sps,
                        WireFps    = (int)Math.Round(frameCnt / dt),
                        GapsRaw    = gapsRaw,
                        GapsProc   = gapsProc,
                        BadFrames  = badFrames,
                        BoardDrops = hBoardDrops,
                        FreeHeap   = hFreeHeap,
                        Marker     = hMarker,
                        BoardBad   = hBoardBad,
                        BoardMiss  = hBoardMiss,
                        BoardDspMax = hBoardDspMax,
                    };
                    MainThreadDispatcher.Enqueue(() => OnStats?.Invoke(snap));

                    lastSampleSeq = curSampleSeq;
                    lastPerfSec   = now;
                    frameCnt      = 0;
                }
            }
        }

        // ── SendMarker (can be called from any thread) ─────────────────────────

        public bool SendMarker(ushort code)
        {
            var stream = _stream;
            if (stream == null) return false;
            try
            {
                byte[] buf = FrameProtocol.BuildCmdFrame(code);
                lock (_sendLock) { stream.Write(buf, 0, buf.Length); }
                Debug.Log($"[BciCore] Marker sent: {code}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BciCore] SendMarker failed: {e.Message}");
                return false;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        static bool ReadExact(Socket socket, byte[] buf, int offset, int count, int timeoutMs = 2000)
        {
            int read = 0;
            while (read < count)
            {
                try
                {
                    if (!socket.Poll(timeoutMs * 1000, SelectMode.SelectRead))
                        return false; // Timed out waiting for data
                    if (socket.Available == 0)
                        return false; // Remote closed gracefully (EOF)

                    int n = socket.Receive(buf, offset + read, count - read, SocketFlags.None);
                    if (n <= 0) return false;
                    read += n;
                }
                catch
                {
                    return false;
                }
            }
            return true;
        }

        static bool ResyncMagic(Socket socket, int timeoutMs = 2000)
        {
            byte prev = 0;
            var  buf  = new byte[1];
            for (int i = 0; i < 65536; i++)   // give up after 64 KB of garbage
            {
                try
                {
                    if (!socket.Poll(timeoutMs * 1000, SelectMode.SelectRead))
                        return false;
                    if (socket.Available == 0)
                        return false;

                    int n = socket.Receive(buf, 0, 1, SocketFlags.None);
                    if (n <= 0) return false;
                }
                catch
                {
                    return false;
                }
                if (prev == FrameProtocol.Magic0 && buf[0] == FrameProtocol.Magic1) return true;
                prev = buf[0];
            }
            return false;
        }

        void NotifyState(ConnectionState s) =>
            MainThreadDispatcher.Enqueue(() => OnStateChanged?.Invoke(s));

        // Monotonic elapsed seconds — avoids calling Unity Time on background threads.
        static readonly long  _epoch = System.Diagnostics.Stopwatch.GetTimestamp();
        static readonly double _freq  = System.Diagnostics.Stopwatch.Frequency;
        static double UnityElapsed() => (System.Diagnostics.Stopwatch.GetTimestamp() - _epoch) / _freq;
    }
}
