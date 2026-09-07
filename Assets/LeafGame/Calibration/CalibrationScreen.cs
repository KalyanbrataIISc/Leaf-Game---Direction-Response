// ============================================================================
//  CalibrationScreen.cs — Replicates the Python BCI Telemetry V2 GUI inside Unity.
//
//  Layout (IMGUI — matches existing LeafGameController IMGUI style):
//
//  ┌─────────────────────────────────────────────────────────────────────────┐
//  │  BCI Telemetry V2                          ● Connecting / Connected     │
//  ├───────────┬─────────────────────────────────────────────────────────────┤
//  │ NETWORK   │  [Multi-Channel Raw]  [FFT Spectrum]                        │
//  │ Frames/s  │  Controls (X Min/Max, Autoscale, etc.)                      │
//  │ …metrics  │  ─────────────────────────────────────────────────────────  │
//  │           │  [ Waveform Grid or Overlaid FFT Power Spectrum ]           │
//  │ PACKET    │  ─────────────────────────────────────────────────────────  │
//  │   LOSS    │  Channel Selectors (CH1..CH32 with colored indicator boxes) │
//  │ …metrics  │                                                             │
//  ├───────────┴─────────────────────────────────────────────────────────────┤
//  │                         [Proceed to Game ▶]                             │
//  └─────────────────────────────────────────────────────────────────────────┘
//
//  Channels 9 to 16 are selected by default on start. The operator can toggle
//  any subset of channels independently for both Raw and FFT views.
// ============================================================================
using System;
using UnityEngine;
using BciCore;

namespace LeafGame
{
    public sealed class CalibrationScreen : MonoBehaviour
    {
        // ── Injected by LeafGameController ────────────────────────────────────
        public Action OnProceedPressed;
        public Action OnBackPressed;

        // ── Inspector tweaks ──────────────────────────────────────────────────
        [Header("Display")]
        [SerializeField] float fftUpdateIntervalSec = 0.08f;    // ~12.5 Hz (matches PyQt refresh)
        [SerializeField] int   fftWindowSamples     = 500;      // 500 samples (2 s at 250 SPS, matches FFT_NPTS in bci_gui_v2.py)
        [SerializeField] int   rawWindowSamples     = 1250;     // 5 s at 250 SPS
        [SerializeField] float defaultYRangeUv      = 200f;

        // ── Tab state ─────────────────────────────────────────────────────────
        enum Tab { Raw, Fft }
        Tab   _tab = Tab.Raw;

        // ── Channel selection (CH9..CH16 enabled by default) ─────────────────
        bool[]  _rawChEnabled;   // per channel toggle for raw view
        bool[]  _fftChEnabled;   // per channel toggle for FFT view
        int     _numCh = 32;

        // ── Raw plot options ──────────────────────────────────────────────────
        bool  _removeDc    = true;
        bool  _autoscaleY  = true;
        float _fixedYRange = 200f;

        // ── FFT state (Real-Time FFT matching bci_gui_v2.py) ──────────────────
        float     _fftTimer;
        float[][] _fftPower;          // real-time power spectrum per channel
        float[]   _fftFreq;
        float     _fftXMax        = 60f;
        bool      _fftAutoscaleY  = true;
        float     _fftFixedY      = 100f;
        float     _curFftYMax     = 100f;

        // ── Stats snapshot ────────────────────────────────────────────────────
        StatsSnapshot   _stats;
        ConnectionState _connState = ConnectionState.Disconnected;
        string          _cachedIp  = "127.0.0.1";

        // ── Scroll position for channel waveforms ────────────────────────────
        Vector2 _plotScroll;

        // ── Working buffers ───────────────────────────────────────────────────
        float[] _sampleBuf;

        // ── IMGUI styles ──────────────────────────────────────────────────────
        GUIStyle _titleStyle, _bodyStyle, _bodyBoldStyle, _labelStyle, _headerStyle, _chActiveStyle;
        GUIStyle _btnStyle, _btnActiveStyle, _smallBtnStyle, _tickLabelStyle, _centerLabelStyle, _legendStyle;
        float    _lastBuiltScale = -1f;

        // ── Colors ────────────────────────────────────────────────────────────
        static readonly Color[] ChColors = {
            new Color(0.0f, 0.75f, 1.0f), new Color(0.0f, 1.0f, 0.5f),
            new Color(1.0f, 0.5f, 0.0f), new Color(1.0f, 0.2f, 0.4f),
            new Color(0.8f, 0.0f, 1.0f), new Color(1.0f, 1.0f, 0.0f),
            new Color(0.0f, 1.0f, 1.0f), new Color(1.0f, 0.4f, 0.8f),
            new Color(0.4f, 0.8f, 1.0f), new Color(0.6f, 1.0f, 0.2f),
            new Color(1.0f, 0.7f, 0.2f), new Color(0.2f, 0.6f, 1.0f),
            new Color(1.0f, 0.3f, 0.0f), new Color(0.0f, 0.9f, 0.7f),
            new Color(0.9f, 0.2f, 1.0f), new Color(1.0f, 0.6f, 0.6f),
            new Color(0.5f, 1.0f, 0.9f), new Color(1.0f, 0.9f, 0.3f),
            new Color(0.3f, 0.5f, 1.0f), new Color(0.9f, 0.6f, 0.0f),
            new Color(0.4f, 1.0f, 0.4f), new Color(1.0f, 0.1f, 0.6f),
            new Color(0.1f, 0.8f, 0.8f), new Color(0.8f, 0.8f, 0.3f),
            new Color(0.6f, 0.3f, 1.0f), new Color(1.0f, 0.5f, 0.5f),
            new Color(0.2f, 1.0f, 0.7f), new Color(0.8f, 0.1f, 0.3f),
            new Color(0.3f, 0.9f, 0.3f), new Color(0.9f, 0.4f, 0.1f),
            new Color(0.1f, 0.5f, 0.9f), new Color(0.7f, 0.0f, 0.7f),
        };

        // ─────────────────────────────────────────────────────────────────────

        void OnEnable()
        {
            _fixedYRange  = defaultYRangeUv;
            _numCh        = BciServer.Config.NumCh > 0 ? BciServer.Config.NumCh : 32;
            _rawChEnabled = new bool[_numCh];
            _fftChEnabled = new bool[_numCh];
            _sampleBuf    = new float[rawWindowSamples];
            _fftPower     = new float[_numCh][];

            _cachedIp     = GetLocalIpAddress();

            // Initially, only channels 9 to 16 (0-indexed 8..15) are selected
            SetDefaultChannelSelection();

            BciServer.OnStats                  += OnStats;
            BciServer.OnConnectionStateChanged += OnConnState;
            BciServer.OnHello                  += OnHello;
            BciServer.EnterCalibrationMode();
        }

        void OnDisable()
        {
            BciServer.OnStats                  -= OnStats;
            BciServer.OnConnectionStateChanged -= OnConnState;
            BciServer.OnHello                  -= OnHello;
        }

        void SetDefaultChannelSelection()
        {
            for (int i = 0; i < _numCh; i++)
            {
                // Channels 9 to 16 (0-based indices 8 through 15)
                bool isDefault = (i >= 8 && i <= 15);
                _rawChEnabled[i] = isDefault;
                _fftChEnabled[i] = isDefault;
            }
        }

        // ── Event handlers (main thread) ──────────────────────────────────────

        void OnStats(StatsSnapshot s)       => _stats = s;
        void OnConnState(ConnectionState s) => _connState = s;

        void OnHello(BciConfig cfg)
        {
            if (cfg.NumCh == _numCh) return;
            _numCh        = cfg.NumCh;
            _rawChEnabled = new bool[_numCh];
            _fftChEnabled = new bool[_numCh];
            _fftPower     = new float[_numCh][];
            SetDefaultChannelSelection();
        }

        // ── Update: Real-time FFT spectrum calculation ───────────────────────

        void Update()
        {
            if (_tab != Tab.Fft) return;
            _fftTimer -= Time.unscaledDeltaTime;
            if (_fftTimer > 0) return;
            _fftTimer = fftUpdateIntervalSec;

            float fs = BciServer.Config.Fs > 0 ? BciServer.Config.Fs : 250f;

            for (int c = 0; c < _numCh; c++)
            {
                if (!_fftChEnabled[c])
                {
                    _fftPower[c] = null;
                    continue;
                }

                int n = BciServer.RingBuffer?.ReadRecent(c, _sampleBuf, fftWindowSamples) ?? 0;
                if (n < 64) continue;

                // Real-time FFT matching get_fft() in bci_gui_v2.py
                _fftPower[c] = FftAnalyzer.ComputeRealtimePowerSpectrum(_sampleBuf, n, fs, out _fftFreq);
            }
        }

        // ── Scale Factor for Tablets & High-DPI Displays ─────────────────────

        float GetUiScale()
        {
            float resScale = Mathf.Min(Screen.width / 1280f, Screen.height / 720f);
            float dpiScale = Screen.dpi > 120f ? (Screen.dpi / 160f) : 1.0f;
            float s = Mathf.Max(resScale, dpiScale);
            return Mathf.Clamp(s, 1.0f, 2.5f);
        }

        // ── IMGUI ─────────────────────────────────────────────────────────────

        void OnGUI()
        {
            float s = GetUiScale();
            BuildStyles(s);

            Rect full = new Rect(0, 0, Screen.width, Screen.height);

            // Background
            GUI.color = new Color(0.07f, 0.08f, 0.12f);
            GUI.DrawTexture(full, Texture2D.whiteTexture);
            GUI.color = Color.white;

            float lw = Mathf.Max(Mathf.Round(260f * s), Mathf.Round(Screen.width * 0.18f));   // left panel width
            float bh = Mathf.Round(50f * s);    // bottom bar height
            float th = Mathf.Round(38f * s);    // title bar height

            Rect titleR  = new Rect(0, 0, Screen.width, th);
            Rect leftR   = new Rect(0, th, lw, Screen.height - th - bh);
            Rect plotR   = new Rect(lw, th, Screen.width - lw, Screen.height - th - bh);
            Rect bottomR = new Rect(0, Screen.height - bh, Screen.width, bh);

            DrawTitleBar(titleR, s);
            DrawLeftPanel(leftR, s);
            DrawPlotArea(plotR, s);
            DrawBottomBar(bottomR, s);
        }

        // ── Title bar ─────────────────────────────────────────────────────────

        void DrawTitleBar(Rect r, float s)
        {
            GUI.color = new Color(0.11f, 0.13f, 0.19f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;

            float pad = Mathf.Round(12f * s);
            GUI.Label(new Rect(r.x + pad, r.y + (r.height - 26f * s) * 0.5f, 340f * s, 26f * s), "BCI Telemetry V2", _titleStyle);

            // Connection indicator with IP
            bool connected = _connState == ConnectionState.Connected;
            GUI.color = connected
                ? new Color(0.2f, 1f, 0.35f)
                : (_connState == ConnectionState.Listening ? new Color(1f, 0.85f, 0.2f) : new Color(1f, 0.4f, 0.3f));

            string statusText = connected
                ? $"● Connected ({_cachedIp}:{BciServer.DefaultPort})"
                : (_connState == ConnectionState.Listening ? $"◌ Listening on {_cachedIp}:{BciServer.DefaultPort}…" : "○ Disconnected");

            float stW = Mathf.Round(420f * s);
            GUI.Label(new Rect(r.xMax - stW - pad, r.y + (r.height - 26f * s) * 0.5f, stW, 26f * s), statusText, _bodyBoldStyle);
            GUI.color = Color.white;
        }

        // ── Left metrics panel ────────────────────────────────────────────────

        void DrawLeftPanel(Rect r, float s)
        {
            GUI.color = new Color(0.09f, 0.10f, 0.15f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;

            float pad    = Mathf.Round(12f * s);
            float y      = r.y + pad;
            float lx     = r.x + pad;
            float rw     = r.width - pad * 2;
            float rowH   = Mathf.Round(22f * s);
            float hdrH   = Mathf.Round(24f * s);
            float labelW = Mathf.Round(rw * 0.60f);
            float valW   = rw - labelW;

            void SectionHeader(string text, Color headerCol)
            {
                GUI.color = headerCol;
                GUI.Label(new Rect(lx, y, rw, hdrH), text, _headerStyle);
                GUI.color = Color.white;
                y += hdrH + Mathf.Round(2f * s);
            }

            void Row(string label, string value, Color? valCol = null)
            {
                GUI.color = new Color(0.72f, 0.78f, 0.88f);
                GUI.Label(new Rect(lx, y, labelW, rowH), label, _labelStyle);
                GUI.color = valCol ?? Color.white;
                GUI.Label(new Rect(lx + labelW, y, valW, rowH), value, _bodyBoldStyle);
                GUI.color = Color.white;
                y += rowH;
            }

            SectionHeader("NETWORK", new Color(0.18f, 0.78f, 1.0f));
            Row("Signal Source:", BciServer.IsUsingProcessedSignals ? "PROCESSED" : "RAW",
                BciServer.IsUsingProcessedSignals ? new Color(0.3f, 0.9f, 1.0f) : new Color(0.4f, 1.0f, 0.6f));
            Row("Frames / Sec:", _stats.Sps > 0 ? $"{_stats.Sps} fps" : "0 fps", _stats.Sps > 200 ? new Color(0.3f, 1.0f, 0.5f) : Color.white);
            Row("Worst |D| µV:", "0.00e+00");
            Row("Alpha rel|D|:", "0.00e+00");
            Row("SSVEP P rel|D|:", "0.00e+00");
            Row("Free Heap:", _stats.FreeHeap > 0 ? _stats.FreeHeap.ToString("N0") : "—");
            Row("Curr Marker:", _stats.Marker.ToString());

            y += Mathf.Round(8f * s);
            SectionHeader("PACKET LOSS", new Color(0.30f, 0.95f, 0.55f));
            Row("Board ring drops:", _stats.BoardDrops.ToString(), _stats.BoardDrops > 0 ? new Color(1f, 0.35f, 0.35f) : Color.white);
            Row("PC seq gaps:", (_stats.GapsRaw + _stats.GapsProc).ToString(), (_stats.GapsRaw + _stats.GapsProc) > 0 ? new Color(1f, 0.35f, 0.35f) : Color.white);
            Row("Board SPI bad:", _stats.BoardBad.ToString(), _stats.BoardBad > 0 ? new Color(1f, 0.35f, 0.35f) : Color.white);
            Row("Board missed DRDY:", _stats.BoardMiss.ToString(), _stats.BoardMiss > 0 ? new Color(1f, 0.35f, 0.35f) : Color.white);
            Row("Server malformed:", _stats.BadFrames.ToString(), _stats.BadFrames > 0 ? new Color(1f, 0.35f, 0.35f) : Color.white);
            Row("DSP peak (µs):", _stats.BoardDspMax.ToString());
        }

        // ── Main Plot Area & Tabs ─────────────────────────────────────────────

        void DrawPlotArea(Rect r, float s)
        {
            // Tab bar
            float tabH = Mathf.Round(34f * s);
            Rect tabBar = new Rect(r.x, r.y, r.width, tabH);
            GUI.color = new Color(0.12f, 0.14f, 0.20f);
            GUI.DrawTexture(tabBar, Texture2D.whiteTexture);
            GUI.color = Color.white;

            float rawBtnW = Mathf.Round(180f * s);
            float fftBtnW = Mathf.Round(150f * s);
            float btnH    = tabH - Mathf.Round(6f * s);
            float btnY    = r.y + Mathf.Round(3f * s);

            string rawBtnText = BciServer.IsUsingProcessedSignals ? "Multi-Channel Proc" : "Multi-Channel Raw";
            if (GUI.Button(new Rect(r.x + Mathf.Round(6f * s), btnY, rawBtnW, btnH), rawBtnText,
                    _tab == Tab.Raw ? _btnActiveStyle : _btnStyle))
                _tab = Tab.Raw;

            if (GUI.Button(new Rect(r.x + rawBtnW + Mathf.Round(12f * s), btnY, fftBtnW, btnH), "FFT Spectrum",
                    _tab == Tab.Fft ? _btnActiveStyle : _btnStyle))
                _tab = Tab.Fft;

            Rect content = new Rect(r.x, r.y + tabH, r.width, r.height - tabH);

            if (_tab == Tab.Raw) DrawRawTab(content, s);
            else                 DrawFftTab(content, s);
        }

        // ── Multi-Channel Raw Tab ─────────────────────────────────────────────

        void DrawRawTab(Rect r, float s)
        {
            // Top options row
            float optH = Mathf.Round(30f * s);
            Rect optR = new Rect(r.x + 6 * s, r.y + 4 * s, r.width - 12 * s, optH);

            _removeDc   = GUI.Toggle(new Rect(optR.x, optR.y + 2 * s, 180 * s, 24 * s), _removeDc, " Remove DC (Centred)", _bodyStyle);
            _autoscaleY = GUI.Toggle(new Rect(optR.x + 185 * s, optR.y + 2 * s, 130 * s, 24 * s), _autoscaleY, " Autoscale Y", _bodyStyle);

            if (!_autoscaleY)
            {
                GUI.Label(new Rect(optR.x + 325 * s, optR.y + 2 * s, 95 * s, 24 * s), "±Range µV:", _labelStyle);
                string rangeStr = GUI.TextField(new Rect(optR.x + 425 * s, optR.y + 2 * s, 70 * s, 24 * s), _fixedYRange.ToString("F0"), _bodyStyle);
                if (float.TryParse(rangeStr, out float parsed) && parsed > 0) _fixedYRange = parsed;
            }

            // Bottom channel selector
            float selH = Mathf.Round(88f * s);
            Rect selR = new Rect(r.x + 6 * s, r.yMax - selH, r.width - 12 * s, selH);
            string selTitle = BciServer.IsUsingProcessedSignals ? "PROCESSED Channels:" : "RAW Channels:";
            DrawChannelSelector(selR, _rawChEnabled, selTitle, s);

            // Waveform plots area
            Rect plotsR = new Rect(r.x + 6 * s, r.y + optH + 6 * s, r.width - 12 * s, r.height - optH - selH - 12 * s);
            DrawWaveformGrid(plotsR, s);
        }

        void DrawWaveformGrid(Rect r, float s)
        {
            int visible = 0;
            for (int c = 0; c < _numCh; c++) if (_rawChEnabled[c]) visible++;

            if (visible == 0)
            {
                GUI.color = new Color(0.08f, 0.10f, 0.15f);
                GUI.DrawTexture(r, Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(new Rect(r.x + 20 * s, r.y + 20 * s, 450 * s, 30 * s), "No channels selected. Please select channels below.", _bodyStyle);
                return;
            }

            // If 8 or fewer channels are selected, perfectly fit without scrollbar
            float ph = (visible <= 8) ? (r.height / visible) : Mathf.Max(60f * s, r.height / 8f);
            float totalH = ph * visible;

            // Scroll view
            Rect viewRect = new Rect(0, 0, r.width - (totalH > r.height ? 20 * s : 0), totalH);
            _plotScroll = GUI.BeginScrollView(r, _plotScroll, viewRect);

            int idx = 0;
            for (int c = 0; c < _numCh; c++)
            {
                if (!_rawChEnabled[c]) continue;
                Rect cr = new Rect(0, idx * ph, viewRect.width, ph - 2);

                // Row background
                GUI.color = new Color(0.05f, 0.07f, 0.11f);
                GUI.DrawTexture(cr, Texture2D.whiteTexture);

                // Channel label
                GUI.color = ChColor(c);
                GUI.Label(new Rect(cr.x + 8 * s, cr.y + 4 * s, 140 * s, 20 * s), $"Channel {c + 1}", _chActiveStyle);
                GUI.color = Color.white;

                idx++;
            }
            GUI.EndScrollView();

            // Render all waveforms with GL in top-left pixel coordinates (matching IMGUI directly)
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0);
            CreateLineMaterial().SetPass(0);
            GL.Begin(GL.LINES);

            idx = 0;
            for (int c = 0; c < _numCh; c++)
            {
                if (!_rawChEnabled[c]) continue;

                float screenX = r.x - _plotScroll.x;
                float screenY = r.y + idx * ph - _plotScroll.y;
                float screenW = viewRect.width;
                float screenH = ph - 2;
                idx++;

                // Skip if completely scrolled outside of plots area
                if (screenY + screenH < r.y || screenY > r.yMax) continue;

                // Center zero line
                float cy = screenY + screenH * 0.5f;
                if (cy >= r.y && cy <= r.yMax)
                {
                    GL.Color(new Color(0.18f, 0.24f, 0.34f, 0.6f));
                    GL.Vertex3(screenX, cy, 0);
                    GL.Vertex3(screenX + screenW, cy, 0);
                }

                // Row bottom border
                float by = screenY + screenH;
                if (by >= r.y && by <= r.yMax)
                {
                    GL.Color(new Color(0.14f, 0.18f, 0.28f, 0.8f));
                    GL.Vertex3(screenX, by, 0);
                    GL.Vertex3(screenX + screenW, by, 0);
                }

                // Waveform data
                int n = BciServer.RingBuffer?.ReadRecent(c, _sampleBuf, rawWindowSamples) ?? 0;
                if (n < 2) continue;

                float yRange = _autoscaleY ? ComputeRange(_sampleBuf, n) : _fixedYRange;
                if (yRange < 1f) yRange = 1f;
                float mean = _removeDc ? ComputeMean(_sampleBuf, n) : 0f;

                GL.Color(ChColor(c));
                float px0 = screenX + 2;
                float v0  = _sampleBuf[0] - mean;
                float py0 = cy - (v0 / yRange) * (screenH * 0.44f);
                py0 = Mathf.Clamp(py0, Mathf.Max(screenY + 1, r.y), Mathf.Min(screenY + screenH - 1, r.yMax));

                for (int i = 1; i < n; i++)
                {
                    float px = screenX + 2 + (float)i / (n - 1) * (screenW - 4);
                    float v  = _sampleBuf[i] - mean;
                    float py = cy - (v / yRange) * (screenH * 0.44f);
                    py = Mathf.Clamp(py, Mathf.Max(screenY + 1, r.y), Mathf.Min(screenY + screenH - 1, r.yMax));

                    GL.Vertex3(px0, py0, 0);
                    GL.Vertex3(px,  py,  0);
                    if (s >= 1.25f)
                    {
                        GL.Vertex3(px0, py0 + 1f, 0);
                        GL.Vertex3(px,  py  + 1f, 0);
                    }

                    px0 = px;
                    py0 = py;
                }
            }

            GL.End();
            GL.PopMatrix();
        }

        // ── FFT Spectrum Tab (Real-Time FFT matching bci_gui_v2.py) ──────────

        void DrawFftTab(Rect r, float s)
        {
            // Top control bar
            float ctrlH = Mathf.Round(32f * s);
            Rect ctrlR = new Rect(r.x + 6 * s, r.y + 4 * s, r.width - 12 * s, ctrlH);

            GUI.Label(new Rect(ctrlR.x, ctrlR.y + 3 * s, 85 * s, 24 * s), "X Max (Hz):", _labelStyle);
            string xMaxStr = GUI.TextField(new Rect(ctrlR.x + 90 * s, ctrlR.y + 2 * s, 55 * s, 24 * s), _fftXMax.ToString("F0"), _bodyStyle);
            if (float.TryParse(xMaxStr, out float parsedMax) && parsedMax > 5) _fftXMax = parsedMax;

            _fftAutoscaleY = GUI.Toggle(new Rect(ctrlR.x + 160 * s, ctrlR.y + 3 * s, 125 * s, 24 * s), _fftAutoscaleY, " Autoscale Y", _bodyStyle);

            if (!_fftAutoscaleY)
            {
                GUI.Label(new Rect(ctrlR.x + 295 * s, ctrlR.y + 3 * s, 65 * s, 24 * s), "Fixed Y:", _labelStyle);
                string fixYStr = GUI.TextField(new Rect(ctrlR.x + 365 * s, ctrlR.y + 2 * s, 65 * s, 24 * s), _fftFixedY.ToString("F0"), _bodyStyle);
                if (float.TryParse(fixYStr, out float parsedFix) && parsedFix > 0) _fftFixedY = parsedFix;
            }

            // Bottom channel selector
            float selH = Mathf.Round(88f * s);
            Rect selR = new Rect(r.x + 6 * s, r.yMax - selH, r.width - 12 * s, selH);
            DrawChannelSelector(selR, _fftChEnabled, "FFT Channels:", s);

            // Middle graph area
            Rect graphR = new Rect(r.x + 6 * s, r.y + ctrlH + 6 * s, r.width - 12 * s, r.height - ctrlH - selH - 12 * s);
            DrawFftOverlayGraph(graphR, s);
        }

        void DrawFftOverlayGraph(Rect graphR, float s)
        {
            // Margins for axes and titles
            float leftM   = Mathf.Round(68f * s);
            float rightM  = Mathf.Round(18f * s);
            float topM    = Mathf.Round(26f * s);
            float bottomM = Mathf.Round(42f * s);

            Rect plotBox = new Rect(graphR.x + leftM, graphR.y + topM, graphR.width - leftM - rightM, graphR.height - topM - bottomM);

            // Background
            GUI.color = new Color(0.04f, 0.06f, 0.10f);
            GUI.DrawTexture(plotBox, Texture2D.whiteTexture);
            GUI.color = Color.white;

            // Graph title
            GUI.Label(new Rect(plotBox.x, graphR.y + 2 * s, plotBox.width, 22 * s), "Real-Time FFT Power Spectrum", _headerStyle);

            // Y axis label
            GUI.color = new Color(0.70f, 0.78f, 0.90f);
            GUI.Label(new Rect(graphR.x + 2 * s, graphR.y + 2 * s, leftM - 4 * s, 20 * s), "Power (µV²)", _labelStyle);
            GUI.color = Color.white;

            // Real-time peak calculation across active channels matching bci_gui_v2.py
            float peak = 0f;
            int enabledCount = 0;
            for (int c = 0; c < _numCh; c++)
            {
                if (!_fftChEnabled[c] || _fftPower[c] == null || _fftFreq == null) continue;
                enabledCount++;
                float[] pwr = _fftPower[c];
                float[] frq = _fftFreq;
                for (int k = 0; k < pwr.Length && k < frq.Length; k++)
                {
                    if (frq[k] > 0.5f && frq[k] <= _fftXMax)
                    {
                        if (pwr[k] > peak) peak = pwr[k];
                    }
                }
            }

            if (_fftAutoscaleY && peak > 1e-9f)
            {
                // Matches bci_gui_v2.py: self.pw.setYRange(0, peak * 1.3, padding=0)
                _curFftYMax = peak * 1.3f;
            }
            else if (!_fftAutoscaleY)
            {
                _curFftYMax = Mathf.Max(1f, _fftFixedY);
            }

            // Draw grid & ticks via GL (using direct top-left pixel matrix matching IMGUI)
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0);
            CreateLineMaterial().SetPass(0);
            GL.Begin(GL.LINES);

            // Horizontal grid lines
            for (int i = 0; i <= 5; i++)
            {
                float frac = i / 5f;
                float gy = plotBox.yMax - frac * plotBox.height;

                GL.Color(new Color(0.18f, 0.24f, 0.35f, 0.65f));
                GL.Vertex3(plotBox.x, gy, 0);
                GL.Vertex3(plotBox.xMax, gy, 0);
            }

            // Vertical grid lines (from 0 Hz to _fftXMax)
            float freqSpan = Mathf.Max(1f, _fftXMax);
            float fStep = freqSpan <= 35f ? 5f : 10f;

            for (float f = 0f; f <= _fftXMax; f += fStep)
            {
                float frac = f / freqSpan;
                float gx = plotBox.x + frac * plotBox.width;

                GL.Color(new Color(0.18f, 0.24f, 0.35f, 0.65f));
                GL.Vertex3(gx, plotBox.y, 0);
                GL.Vertex3(gx, plotBox.yMax, 0);
            }

            // Plot border
            GL.Color(new Color(0.28f, 0.36f, 0.50f));
            GL.Vertex3(plotBox.x, plotBox.y, 0);
            GL.Vertex3(plotBox.xMax, plotBox.y, 0);
            GL.Vertex3(plotBox.xMax, plotBox.y, 0);
            GL.Vertex3(plotBox.xMax, plotBox.yMax, 0);
            GL.Vertex3(plotBox.xMax, plotBox.yMax, 0);
            GL.Vertex3(plotBox.x, plotBox.yMax, 0);
            GL.Vertex3(plotBox.x, plotBox.yMax, 0);
            GL.Vertex3(plotBox.x, plotBox.y, 0);

            // Spectrum curves (NON-INVERTED: baseline at plotBox.yMax, peaks shoot UP towards plotBox.y)
            for (int c = 0; c < _numCh; c++)
            {
                if (!_fftChEnabled[c] || _fftPower[c] == null || _fftFreq == null) continue;
                float[] pwr = _fftPower[c];
                float[] frq = _fftFreq;
                int len = Math.Min(pwr.Length, frq.Length);
                if (len < 2) continue;

                GL.Color(ChColor(c));
                float prevX = 0, prevY = 0;
                bool hasPrev = false;

                for (int k = 0; k < len; k++)
                {
                    float f = frq[k];
                    if (f > _fftXMax) break;

                    float xFrac = f / freqSpan;
                    float px = plotBox.x + xFrac * plotBox.width;

                    // yFrac = 0 at zero power (baseline at bottom), 1 at peak (shoots up towards top)
                    float yFrac = Mathf.Clamp01(pwr[k] / _curFftYMax);
                    float py = plotBox.yMax - yFrac * plotBox.height;
                    py = Mathf.Clamp(py, plotBox.y + 1, plotBox.yMax - 1);

                    if (hasPrev)
                    {
                        GL.Vertex3(prevX, prevY, 0);
                        GL.Vertex3(px,    py,    0);
                        if (s >= 1.25f)
                        {
                            GL.Vertex3(prevX, prevY + 1f, 0);
                            GL.Vertex3(px,    py    + 1f, 0);
                        }
                    }
                    prevX = px;
                    prevY = py;
                    hasPrev = true;
                }
            }

            GL.End();
            GL.PopMatrix();

            // Draw Y-axis tick labels
            for (int i = 0; i <= 5; i++)
            {
                float frac = i / 5f;
                float val = _curFftYMax * frac;
                float gy = plotBox.yMax - frac * plotBox.height;
                string valStr = val >= 100f ? val.ToString("F0") : (val >= 10f ? val.ToString("F1") : val.ToString("F2"));
                GUI.Label(new Rect(graphR.x, gy - 10 * s, leftM - 8 * s, 20 * s), valStr, _tickLabelStyle);
            }

            // Draw X-axis tick labels
            for (float f = 0f; f <= _fftXMax; f += fStep)
            {
                float frac = f / freqSpan;
                float gx = plotBox.x + frac * plotBox.width;
                GUI.Label(new Rect(gx - 25 * s, plotBox.yMax + 4 * s, 50 * s, 20 * s), f.ToString("F0"), _centerLabelStyle);
            }

            // X-axis Title
            GUI.color = new Color(0.70f, 0.78f, 0.90f);
            GUI.Label(new Rect(plotBox.x, plotBox.yMax + 20 * s, plotBox.width, 22 * s), "Frequency (Hz)", _centerLabelStyle);
            GUI.color = Color.white;

            // Overlay Legend (top-left inside plotBox)
            if (enabledCount > 0)
            {
                int cols = enabledCount > 16 ? 3 : (enabledCount > 8 ? 2 : 1);
                int rowsPerCol = Mathf.CeilToInt((float)enabledCount / cols);
                float colItemW = Mathf.Round(80f * s);
                float rowItemH = Mathf.Round(18f * s);
                float legW = cols * colItemW + 12f * s;
                float legH = rowsPerCol * rowItemH + 12f * s;
                Rect legR = new Rect(plotBox.x + 10 * s, plotBox.y + 10 * s, legW, legH);

                // Semi-transparent legend background
                GUI.color = new Color(0.06f, 0.08f, 0.13f, 0.85f);
                GUI.DrawTexture(legR, Texture2D.whiteTexture);
                GUI.color = Color.white;

                int itemIdx = 0;
                for (int c = 0; c < _numCh; c++)
                {
                    if (!_fftChEnabled[c]) continue;
                    int col = itemIdx / rowsPerCol;
                    int row = itemIdx % rowsPerCol;
                    float ix = legR.x + 8 * s + col * colItemW;
                    float iy = legR.y + 6 * s + row * rowItemH;

                    // Colored dash indicator
                    GUI.color = ChColor(c);
                    GUI.Label(new Rect(ix, iy - 2 * s, 22 * s, 18 * s), "-", _legendStyle);

                    // Channel label
                    GUI.color = new Color(0.90f, 0.94f, 0.98f);
                    GUI.Label(new Rect(ix + 20 * s, iy, 55 * s, 18 * s), $"CH{c + 1}", _chActiveStyle);
                    itemIdx++;
                }
                GUI.color = Color.white;
            }
            else
            {
                GUI.Label(new Rect(plotBox.center.x - 180 * s, plotBox.center.y - 14 * s, 360 * s, 28 * s),
                    "No channels selected. Please select channels below.", _centerLabelStyle);
            }
        }

        // ── Channel Selector (8 Columns with Colored Indicator Boxes) ─────────

        void DrawChannelSelector(Rect r, bool[] enabled, string title, float s)
        {
            // Panel background
            GUI.color = new Color(0.10f, 0.12f, 0.17f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;

            // Header row
            float hdrH = Mathf.Round(24f * s);
            GUI.Label(new Rect(r.x + 8 * s, r.y + 2 * s, 150 * s, hdrH), title, _labelStyle);

            // Preset buttons on the right of header
            float rx = r.xMax - 8 * s;
            float btnW_clear = Mathf.Round(60f * s);
            float btnW_all   = Mathf.Round(80f * s);
            float btnW_def   = Mathf.Round(140f * s);
            float btnH       = Mathf.Round(20f * s);

            rx -= btnW_clear;
            if (GUI.Button(new Rect(rx, r.y + 2 * s, btnW_clear - 4 * s, btnH), "Clear", _smallBtnStyle))
            {
                for (int i = 0; i < _numCh; i++) enabled[i] = false;
            }

            rx -= btnW_all;
            if (GUI.Button(new Rect(rx, r.y + 2 * s, btnW_all - 4 * s, btnH), "Select All", _smallBtnStyle))
            {
                for (int i = 0; i < _numCh; i++) enabled[i] = true;
            }

            rx -= btnW_def;
            if (GUI.Button(new Rect(rx, r.y + 2 * s, btnW_def - 4 * s, btnH), "CH 9-16 (Default)", _smallBtnStyle))
            {
                for (int i = 0; i < _numCh; i++) enabled[i] = (i >= 8 && i <= 15);
            }

            // Grid of channel toggles: 8 columns across
            int cols = 8;
            int rows = Mathf.Max(1, Mathf.CeilToInt((float)_numCh / cols));
            float gridY = r.y + hdrH + 2 * s;
            float colW = (r.width - 16 * s) / cols;
            float rowH = (r.height - hdrH - 6 * s) / rows;

            for (int c = 0; c < _numCh; c++)
            {
                int col = c % cols;
                int row = c / cols;
                float bx = r.x + 8 * s + col * colW;
                float by = gridY + row * rowH;
                Rect bRect = new Rect(bx, by, colW - 4 * s, rowH - 1 * s);

                // Indicator box
                float boxSz = Mathf.Round(14f * s);
                Rect boxRect = new Rect(bx + 2 * s, by + (rowH - boxSz) * 0.5f, boxSz, boxSz);
                Color chCol = ChColor(c);

                if (enabled[c])
                {
                    GUI.color = chCol;
                    GUI.DrawTexture(boxRect, Texture2D.whiteTexture);
                }
                else
                {
                    GUI.color = new Color(0.20f, 0.22f, 0.28f);
                    GUI.DrawTexture(boxRect, Texture2D.whiteTexture);
                }
                GUI.color = Color.white;

                // Click button over entire area
                if (GUI.Button(bRect, GUIContent.none, GUIStyle.none))
                {
                    enabled[c] = !enabled[c];
                }

                // Channel text label
                GUIStyle st = enabled[c] ? _chActiveStyle : _labelStyle;
                GUI.Label(new Rect(boxRect.xMax + 5 * s, by, colW - boxSz - 8 * s, rowH), $"CH{c + 1}", st);
            }
        }

        // ── Bottom bar (Proceed) ──────────────────────────────────────────────

        void DrawBottomBar(Rect r, float s)
        {
            GUI.color = new Color(0.09f, 0.11f, 0.16f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;

            float pad = Mathf.Round(16f * s);
            float bh  = Mathf.Round(40f * s);

            // Back button (left aligned)
            float backW = Mathf.Round(180f * s);
            Rect backBtn = new Rect(r.x + pad, r.y + (r.height - bh) * 0.5f, backW, bh);
            GUI.color = new Color(0.7f, 0.75f, 0.85f);
            if (GUI.Button(backBtn, "<  Back to Setup", _btnStyle))
            {
                BciServer.ExitCalibrationMode();
                OnBackPressed?.Invoke();
            }

            // Proceed button (center aligned)
            float bw = Mathf.Round(300f * s);
            Rect btn = new Rect(r.center.x - bw * 0.5f, r.y + (r.height - bh) * 0.5f, bw, bh);

            GUI.color = new Color(0.18f, 0.82f, 0.42f);
            if (GUI.Button(btn, "Proceed to Game  >", _btnActiveStyle))
            {
                BciServer.ExitCalibrationMode();
                OnProceedPressed?.Invoke();
            }
            GUI.color = Color.white;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        static float ComputeRange(float[] buf, int n)
        {
            float mn = buf[0], mx = buf[0];
            for (int i = 1; i < n; i++) { if (buf[i] < mn) mn = buf[i]; if (buf[i] > mx) mx = buf[i]; }
            return Mathf.Max(1f, (mx - mn) * 0.5f);
        }

        static float ComputeMean(float[] buf, int n)
        {
            double s = 0; for (int i = 0; i < n; i++) s += buf[i]; return (float)(s / n);
        }

        Color ChColor(int c) => ChColors[c % ChColors.Length];

        // Cached GL material for line drawing
        static string GetLocalIpAddress()
        {
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    var props = ni.GetIPProperties();
                    foreach (var addr in props.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                            !System.Net.IPAddress.IsLoopback(addr.Address))
                        {
                            return addr.Address.ToString();
                        }
                    }
                }
            }
            catch { }

            try
            {
                using (var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, 0))
                {
                    socket.Connect("8.8.8.8", 65530);
                    var endPoint = socket.LocalEndPoint as System.Net.IPEndPoint;
                    if (endPoint != null) return endPoint.Address.ToString();
                }
            }
            catch { }

            return "127.0.0.1";
        }

        static Material _lineMat;
        static Material CreateLineMaterial()
        {
            if (_lineMat) return _lineMat;
            var shader = Shader.Find("Hidden/Internal-Colored");
            _lineMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _lineMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _lineMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _lineMat.SetInt("_Cull",     (int)UnityEngine.Rendering.CullMode.Off);
            _lineMat.SetInt("_ZWrite",   0);
            return _lineMat;
        }

        void BuildStyles(float s)
        {
            if (Mathf.Abs(s - _lastBuiltScale) < 0.05f) return;
            _lastBuiltScale = s;

            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(16 * s), fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };
            _bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(13 * s), normal = { textColor = new Color(0.92f, 0.94f, 0.98f) }
            };
            _bodyBoldStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(13 * s), fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };
            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(12 * s), normal = { textColor = new Color(0.72f, 0.78f, 0.88f) }
            };
            _chActiveStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(12 * s), fontStyle = FontStyle.Bold, normal = { textColor = Color.white }
            };
            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(13 * s), fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.25f, 0.75f, 1f) }
            };
            _tickLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(11 * s), alignment = TextAnchor.MiddleRight,
                normal = { textColor = new Color(0.70f, 0.78f, 0.88f) }
            };
            _centerLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(11 * s), alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.70f, 0.78f, 0.88f) }
            };
            _legendStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(12 * s), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };
            _btnStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = Mathf.RoundToInt(13 * s),
                normal   = { textColor = new Color(0.8f, 0.88f, 0.96f), background = MakeTex(2, 2, new Color(0.14f, 0.18f, 0.28f)) },
                hover    = { textColor = Color.white,                   background = MakeTex(2, 2, new Color(0.20f, 0.28f, 0.44f)) },
            };
            _btnActiveStyle = new GUIStyle(_btnStyle)
            {
                fontStyle = FontStyle.Bold,
                normal    = { textColor = Color.white, background = MakeTex(2, 2, new Color(0.2f, 0.48f, 0.85f)) },
            };
            _smallBtnStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = Mathf.RoundToInt(11 * s),
                normal   = { textColor = new Color(0.8f, 0.88f, 0.96f), background = MakeTex(2, 2, new Color(0.16f, 0.20f, 0.30f)) },
                hover    = { textColor = Color.white,                   background = MakeTex(2, 2, new Color(0.24f, 0.32f, 0.48f)) },
            };
        }

        static Texture2D MakeTex(int w, int h, Color col)
        {
            var pixels = new Color[w * h];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = col;
            var tex = new Texture2D(w, h);
            tex.SetPixels(pixels); tex.Apply();
            return tex;
        }
    }
}
