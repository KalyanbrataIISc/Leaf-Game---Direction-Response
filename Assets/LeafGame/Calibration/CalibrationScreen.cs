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

        // ── Scroll position for channel waveforms ────────────────────────────
        Vector2 _plotScroll;

        // ── Working buffers ───────────────────────────────────────────────────
        float[] _sampleBuf;

        // ── IMGUI styles ──────────────────────────────────────────────────────
        GUIStyle _titleStyle, _bodyStyle, _labelStyle, _headerStyle, _chActiveStyle;
        GUIStyle _btnStyle, _btnActiveStyle, _smallBtnStyle, _tickLabelStyle, _centerLabelStyle, _legendStyle;
        bool     _stylesBuilt;

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

        // ── IMGUI ─────────────────────────────────────────────────────────────

        void OnGUI()
        {
            BuildStyles();

            Rect full = new Rect(0, 0, Screen.width, Screen.height);

            // Background
            GUI.color = new Color(0.07f, 0.08f, 0.12f);
            GUI.DrawTexture(full, Texture2D.whiteTexture);
            GUI.color = Color.white;

            float lw = 220f;   // left panel width
            float bh = 46f;    // bottom bar height
            float th = 34f;    // title bar height

            Rect titleR  = new Rect(0, 0, Screen.width, th);
            Rect leftR   = new Rect(0, th, lw, Screen.height - th - bh);
            Rect plotR   = new Rect(lw, th, Screen.width - lw, Screen.height - th - bh);
            Rect bottomR = new Rect(0, Screen.height - bh, Screen.width, bh);

            DrawTitleBar(titleR);
            DrawLeftPanel(leftR);
            DrawPlotArea(plotR);
            DrawBottomBar(bottomR);
        }

        // ── Title bar ─────────────────────────────────────────────────────────

        void DrawTitleBar(Rect r)
        {
            GUI.color = new Color(0.11f, 0.13f, 0.19f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;

            GUI.Label(new Rect(r.x + 12, r.y + 6, 280, 24), "BCI Telemetry V2", _titleStyle);

            // Connection indicator
            bool connected = _connState == ConnectionState.Connected;
            GUI.color = connected ? new Color(0.2f, 1f, 0.2f) : new Color(1f, 0.6f, 0.1f);
            GUI.Label(new Rect(r.xMax - 200, r.y + 6, 190, 24),
                (connected ? "● Connected" : (_connState == ConnectionState.Listening ? "◌ Listening…" : "○ Disconnected")),
                _bodyStyle);
            GUI.color = Color.white;
        }

        // ── Left metrics panel ────────────────────────────────────────────────

        void DrawLeftPanel(Rect r)
        {
            GUI.color = new Color(0.09f, 0.10f, 0.15f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;

            float y  = r.y + 10;
            float lx = r.x + 10;
            float rw = r.width - 20;

            void SectionHeader(string text)
            {
                GUI.color = new Color(0.2f, 0.6f, 1.0f);
                GUI.Label(new Rect(lx, y, rw, 18), text, _headerStyle);
                GUI.color = Color.white;
                y += 22;
            }

            void Row(string label, string value)
            {
                GUI.color = new Color(0.55f, 0.60f, 0.72f);
                GUI.Label(new Rect(lx, y, 115, 16), label, _labelStyle);
                GUI.color = Color.white;
                GUI.Label(new Rect(lx + 118, y, rw - 118, 16), value, _bodyStyle);
                y += 18;
            }

            SectionHeader("NETWORK");
            Row("Frames / Sec:", _stats.Sps.ToString());
            Row("Worst |D| µV:", "0.00e+00");
            Row("Alpha rel|D|:", "0.00e+00");
            Row("SSVEP P rel|D|:", "0.00e+00");
            Row("Free Heap:", _stats.FreeHeap > 0 ? _stats.FreeHeap.ToString() : "—");
            Row("Curr Marker:", _stats.Marker.ToString());

            y += 10;
            SectionHeader("PACKET LOSS");
            Row("Board ring drops:", _stats.BoardDrops.ToString());
            Row("PC seq gaps:", (_stats.GapsRaw + _stats.GapsProc).ToString());
            Row("Board SPI bad:", _stats.BoardBad.ToString());
            Row("Board missed DRDY:", _stats.BoardMiss.ToString());
            Row("Server malformed:", _stats.BadFrames.ToString());
            Row("DSP peak (µs):", _stats.BoardDspMax.ToString());
        }

        // ── Main Plot Area & Tabs ─────────────────────────────────────────────

        void DrawPlotArea(Rect r)
        {
            // Tab bar
            float tabH = 30f;
            Rect tabBar = new Rect(r.x, r.y, r.width, tabH);
            GUI.color = new Color(0.12f, 0.14f, 0.20f);
            GUI.DrawTexture(tabBar, Texture2D.whiteTexture);
            GUI.color = Color.white;

            if (GUI.Button(new Rect(r.x + 6, r.y + 3, 160, tabH - 6), "Multi-Channel Raw",
                    _tab == Tab.Raw ? _btnActiveStyle : _btnStyle))
                _tab = Tab.Raw;

            if (GUI.Button(new Rect(r.x + 172, r.y + 3, 140, tabH - 6), "FFT Spectrum",
                    _tab == Tab.Fft ? _btnActiveStyle : _btnStyle))
                _tab = Tab.Fft;

            Rect content = new Rect(r.x, r.y + tabH, r.width, r.height - tabH);

            if (_tab == Tab.Raw) DrawRawTab(content);
            else                 DrawFftTab(content);
        }

        // ── Multi-Channel Raw Tab ─────────────────────────────────────────────

        void DrawRawTab(Rect r)
        {
            // Top options row
            float optH = 28f;
            Rect optR = new Rect(r.x + 6, r.y + 4, r.width - 12, optH);

            _removeDc   = GUI.Toggle(new Rect(optR.x, optR.y + 3, 160, 20), _removeDc, " Remove DC (Centred)", _bodyStyle);
            _autoscaleY = GUI.Toggle(new Rect(optR.x + 165, optR.y + 3, 110, 20), _autoscaleY, " Autoscale Y", _bodyStyle);

            if (!_autoscaleY)
            {
                GUI.Label(new Rect(optR.x + 280, optR.y + 3, 85, 20), "±Range µV:", _labelStyle);
                string rangeStr = GUI.TextField(new Rect(optR.x + 368, optR.y + 2, 60, 20), _fixedYRange.ToString("F0"), _bodyStyle);
                if (float.TryParse(rangeStr, out float parsed) && parsed > 0) _fixedYRange = parsed;
            }

            // Bottom channel selector
            float selH = 76f;
            Rect selR = new Rect(r.x + 6, r.yMax - selH, r.width - 12, selH);
            DrawChannelSelector(selR, _rawChEnabled, "RAW Channels:");

            // Waveform plots area
            Rect plotsR = new Rect(r.x + 6, r.y + optH + 8, r.width - 12, r.height - optH - selH - 14);
            DrawWaveformGrid(plotsR);
        }

        void DrawWaveformGrid(Rect r)
        {
            int visible = 0;
            for (int c = 0; c < _numCh; c++) if (_rawChEnabled[c]) visible++;

            if (visible == 0)
            {
                GUI.color = new Color(0.08f, 0.10f, 0.15f);
                GUI.DrawTexture(r, Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(new Rect(r.x + 20, r.y + 20, 400, 24), "No channels selected. Please select channels below.", _bodyStyle);
                return;
            }

            // If 8 or fewer channels are selected, perfectly fit without scrollbar
            float ph = (visible <= 8) ? (r.height / visible) : Mathf.Max(55f, r.height / 8f);
            float totalH = ph * visible;

            // Scroll view
            Rect viewRect = new Rect(0, 0, r.width - (totalH > r.height ? 18 : 0), totalH);
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
                GUI.Label(new Rect(cr.x + 6, cr.y + 3, 120, 16), $"Channel {c + 1}", _labelStyle);
                GUI.color = Color.white;

                idx++;
            }
            GUI.EndScrollView();

            // Render all waveforms in one GL batch with exact screen coordinates & clipping
            GL.PushMatrix();
            GL.LoadPixelMatrix();
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
                    GL.Color(new Color(0.18f, 0.22f, 0.30f, 0.5f));
                    GL.Vertex3(screenX, Screen.height - cy, 0);
                    GL.Vertex3(screenX + screenW, Screen.height - cy, 0);
                }

                // Row bottom border
                float by = screenY + screenH;
                if (by >= r.y && by <= r.yMax)
                {
                    GL.Color(new Color(0.14f, 0.18f, 0.26f, 0.7f));
                    GL.Vertex3(screenX, Screen.height - by, 0);
                    GL.Vertex3(screenX + screenW, Screen.height - by, 0);
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

                    GL.Vertex3(px0, Screen.height - py0, 0);
                    GL.Vertex3(px,  Screen.height - py,  0);

                    px0 = px;
                    py0 = py;
                }
            }

            GL.End();
            GL.PopMatrix();
        }

        // ── FFT Spectrum Tab (Real-Time FFT matching bci_gui_v2.py) ──────────

        void DrawFftTab(Rect r)
        {
            // Top control bar
            float ctrlH = 30f;
            Rect ctrlR = new Rect(r.x + 6, r.y + 4, r.width - 12, ctrlH);

            GUI.Label(new Rect(ctrlR.x, ctrlR.y + 5, 75, 20), "X Max (Hz):", _labelStyle);
            string xMaxStr = GUI.TextField(new Rect(ctrlR.x + 76, ctrlR.y + 4, 45, 22), _fftXMax.ToString("F0"), _bodyStyle);
            if (float.TryParse(xMaxStr, out float parsedMax) && parsedMax > 5) _fftXMax = parsedMax;

            _fftAutoscaleY = GUI.Toggle(new Rect(ctrlR.x + 135, ctrlR.y + 5, 105, 20), _fftAutoscaleY, " Autoscale Y", _bodyStyle);

            if (!_fftAutoscaleY)
            {
                GUI.Label(new Rect(ctrlR.x + 250, ctrlR.y + 5, 55, 20), "Fixed Y:", _labelStyle);
                string fixYStr = GUI.TextField(new Rect(ctrlR.x + 305, ctrlR.y + 4, 55, 22), _fftFixedY.ToString("F0"), _bodyStyle);
                if (float.TryParse(fixYStr, out float parsedFix) && parsedFix > 0) _fftFixedY = parsedFix;
            }

            // Bottom channel selector
            float selH = 76f;
            Rect selR = new Rect(r.x + 6, r.yMax - selH, r.width - 12, selH);
            DrawChannelSelector(selR, _fftChEnabled, "FFT Channels:");

            // Middle graph area
            Rect graphR = new Rect(r.x + 6, r.y + ctrlH + 8, r.width - 12, r.height - ctrlH - selH - 14);
            DrawFftOverlayGraph(graphR);
        }

        void DrawFftOverlayGraph(Rect graphR)
        {
            // Margins for axes and titles
            float leftM   = 62f;
            float rightM  = 16f;
            float topM    = 24f;
            float bottomM = 36f;

            Rect plotBox = new Rect(graphR.x + leftM, graphR.y + topM, graphR.width - leftM - rightM, graphR.height - topM - bottomM);

            // Background
            GUI.color = new Color(0.04f, 0.06f, 0.10f);
            GUI.DrawTexture(plotBox, Texture2D.whiteTexture);
            GUI.color = Color.white;

            // Graph title
            GUI.Label(new Rect(graphR.x, graphR.y + 2, graphR.width, 20), "FFT Power Spectrum", _headerStyle);

            // Y axis label
            GUI.color = new Color(0.6f, 0.7f, 0.85f);
            GUI.Label(new Rect(graphR.x + 4, graphR.y + 4, 85, 16), "Power (µV²)", _labelStyle);
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

            // Draw grid & ticks via GL
            GL.PushMatrix();
            GL.LoadPixelMatrix();
            CreateLineMaterial().SetPass(0);
            GL.Begin(GL.LINES);

            // Horizontal grid lines
            for (int i = 0; i <= 5; i++)
            {
                float frac = i / 5f;
                float gy = plotBox.yMax - frac * plotBox.height;

                GL.Color(new Color(0.16f, 0.22f, 0.32f, 0.65f));
                GL.Vertex3(plotBox.x, Screen.height - gy, 0);
                GL.Vertex3(plotBox.xMax, Screen.height - gy, 0);
            }

            // Vertical grid lines (from 0 Hz to _fftXMax)
            float freqSpan = Mathf.Max(1f, _fftXMax);
            float fStep = freqSpan <= 35f ? 5f : 10f;

            for (float f = 0f; f <= _fftXMax; f += fStep)
            {
                float frac = f / freqSpan;
                float gx = plotBox.x + frac * plotBox.width;

                GL.Color(new Color(0.16f, 0.22f, 0.32f, 0.65f));
                GL.Vertex3(gx, Screen.height - plotBox.y, 0);
                GL.Vertex3(gx, Screen.height - plotBox.yMax, 0);
            }

            // Plot border
            GL.Color(new Color(0.24f, 0.32f, 0.44f));
            GL.Vertex3(plotBox.x, Screen.height - plotBox.y, 0);
            GL.Vertex3(plotBox.xMax, Screen.height - plotBox.y, 0);
            GL.Vertex3(plotBox.xMax, Screen.height - plotBox.y, 0);
            GL.Vertex3(plotBox.xMax, Screen.height - plotBox.yMax, 0);
            GL.Vertex3(plotBox.xMax, Screen.height - plotBox.yMax, 0);
            GL.Vertex3(plotBox.x, Screen.height - plotBox.yMax, 0);
            GL.Vertex3(plotBox.x, Screen.height - plotBox.yMax, 0);
            GL.Vertex3(plotBox.x, Screen.height - plotBox.y, 0);

            // Spectrum curves (overlay!)
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

                    float yFrac = Mathf.Clamp01(pwr[k] / _curFftYMax);
                    float py = plotBox.yMax - yFrac * plotBox.height;
                    py = Mathf.Clamp(py, plotBox.y + 1, plotBox.yMax - 1);

                    if (hasPrev)
                    {
                        GL.Vertex3(prevX, Screen.height - prevY, 0);
                        GL.Vertex3(px,    Screen.height - py,   0);
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
                GUI.Label(new Rect(graphR.x, gy - 8, leftM - 6, 16), valStr, _tickLabelStyle);
            }

            // Draw X-axis tick labels
            for (float f = 0f; f <= _fftXMax; f += fStep)
            {
                float frac = f / freqSpan;
                float gx = plotBox.x + frac * plotBox.width;
                GUI.Label(new Rect(gx - 20, plotBox.yMax + 3, 40, 16), f.ToString("F0"), _centerLabelStyle);
            }

            // X-axis Title
            GUI.color = new Color(0.6f, 0.7f, 0.85f);
            GUI.Label(new Rect(plotBox.x, plotBox.yMax + 18, plotBox.width, 18), "Frequency (Hz)", _centerLabelStyle);
            GUI.color = Color.white;

            // Overlay Legend (top-left inside plotBox)
            if (enabledCount > 0)
            {
                int cols = enabledCount > 16 ? 3 : (enabledCount > 8 ? 2 : 1);
                int rowsPerCol = Mathf.CeilToInt((float)enabledCount / cols);
                float legW = cols * 74f + 10f;
                float legH = rowsPerCol * 16f + 10f;
                Rect legR = new Rect(plotBox.x + 10, plotBox.y + 8, legW, legH);

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
                    float ix = legR.x + 8 + col * 74f;
                    float iy = legR.y + 5 + row * 16f;

                    // Colored dash indicator
                    GUI.color = ChColor(c);
                    GUI.Label(new Rect(ix, iy - 2, 20, 16), "—", _legendStyle);

                    // Channel label
                    GUI.color = new Color(0.85f, 0.90f, 0.95f);
                    GUI.Label(new Rect(ix + 18, iy, 52, 16), $"CH{c + 1}", _labelStyle);
                    itemIdx++;
                }
                GUI.color = Color.white;
            }
            else
            {
                GUI.Label(new Rect(plotBox.center.x - 150, plotBox.center.y - 12, 300, 24),
                    "No channels selected. Please select channels below.", _centerLabelStyle);
            }
        }

        // ── Channel Selector (8 Columns with Colored Indicator Boxes) ─────────

        void DrawChannelSelector(Rect r, bool[] enabled, string title)
        {
            // Panel background
            GUI.color = new Color(0.10f, 0.12f, 0.17f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;

            // Header row
            float hdrH = 20f;
            GUI.Label(new Rect(r.x + 8, r.y + 2, 120, hdrH), title, _labelStyle);

            // Preset buttons on the right of header
            float rx = r.xMax - 8;

            rx -= 55f;
            if (GUI.Button(new Rect(rx, r.y + 2, 50, 17), "Clear", _smallBtnStyle))
            {
                for (int i = 0; i < _numCh; i++) enabled[i] = false;
            }

            rx -= 75f;
            if (GUI.Button(new Rect(rx, r.y + 2, 70, 17), "Select All", _smallBtnStyle))
            {
                for (int i = 0; i < _numCh; i++) enabled[i] = true;
            }

            rx -= 125f;
            if (GUI.Button(new Rect(rx, r.y + 2, 120, 17), "CH 9–16 (Default)", _smallBtnStyle))
            {
                for (int i = 0; i < _numCh; i++) enabled[i] = (i >= 8 && i <= 15);
            }

            // Grid of channel toggles: 8 columns across
            int cols = 8;
            int rows = Mathf.Max(1, Mathf.CeilToInt((float)_numCh / cols));
            float gridY = r.y + hdrH + 2;
            float colW = (r.width - 16) / cols;
            float rowH = (r.height - hdrH - 6) / rows;

            for (int c = 0; c < _numCh; c++)
            {
                int col = c % cols;
                int row = c / cols;
                float bx = r.x + 8 + col * colW;
                float by = gridY + row * rowH;
                Rect bRect = new Rect(bx, by, colW - 4, rowH - 1);

                // Indicator box
                float boxSz = 11f;
                Rect boxRect = new Rect(bx + 2, by + (rowH - boxSz) * 0.5f, boxSz, boxSz);
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
                GUI.Label(new Rect(boxRect.xMax + 4, by, colW - boxSz - 8, rowH), $"CH{c + 1}", st);
            }
        }

        // ── Bottom bar (Proceed) ──────────────────────────────────────────────

        void DrawBottomBar(Rect r)
        {
            GUI.color = new Color(0.09f, 0.11f, 0.16f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;

            float bw = 240f, bh = 34f;
            Rect btn = new Rect(r.center.x - bw * 0.5f, r.y + (r.height - bh) * 0.5f, bw, bh);

            GUI.color = new Color(0.2f, 0.8f, 0.4f);
            if (GUI.Button(btn, "Proceed to Game  ▶", _btnActiveStyle))
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

        void BuildStyles()
        {
            if (_stylesBuilt) return;
            _stylesBuilt = true;

            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14, fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };
            _bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11, normal = { textColor = new Color(0.9f, 0.9f, 0.9f) }
            };
            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 10, normal = { textColor = new Color(0.55f, 0.62f, 0.74f) }
            };
            _chActiveStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 10, fontStyle = FontStyle.Bold, normal = { textColor = Color.white }
            };
            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11, fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.3f, 0.7f, 1f) }
            };
            _tickLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 9, alignment = TextAnchor.MiddleRight,
                normal = { textColor = new Color(0.55f, 0.62f, 0.72f) }
            };
            _centerLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 10, alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.6f, 0.7f, 0.85f) }
            };
            _legendStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };
            _btnStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 11,
                normal   = { textColor = new Color(0.7f, 0.8f, 0.9f), background = MakeTex(2, 2, new Color(0.14f, 0.18f, 0.28f)) },
                hover    = { textColor = Color.white,                   background = MakeTex(2, 2, new Color(0.18f, 0.25f, 0.40f)) },
            };
            _btnActiveStyle = new GUIStyle(_btnStyle)
            {
                fontStyle = FontStyle.Bold,
                normal    = { textColor = Color.white, background = MakeTex(2, 2, new Color(0.2f, 0.45f, 0.8f)) },
            };
            _smallBtnStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 9,
                normal   = { textColor = new Color(0.7f, 0.8f, 0.9f), background = MakeTex(2, 2, new Color(0.16f, 0.20f, 0.30f)) },
                hover    = { textColor = Color.white,                   background = MakeTex(2, 2, new Color(0.22f, 0.30f, 0.45f)) },
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
