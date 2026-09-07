# BciCore — In-Unity EEG TCP Server

**Version:** 1.0.0  
**Package ID:** `org.iisc.bcicore.server`  
**Author:** IISc BCI Lab

---

## Overview

`BciCore` ports the functionality of `eeg_tcp_server.py` into Unity itself. Instead of running a separate Python process on the PC, the Unity game now **hosts the TCP server**. The ESP32 board connects to Unity on port 5005, exactly as it previously connected to the Python script.

### What changed vs. the Python server

| Feature | Python `eeg_tcp_server.py` | `BciCore` (Unity) |
|---|---|---|
| Process | Separate Python process | In-process Unity package |
| Connection direction | PC (Python) ← ESP32 | PC (Unity) ← ESP32 |
| Port | 5005 (ESP32 in) | **5005 (same)** |
| NF fan-out | TCP port 5006 | **In-process event** (`BciServer.OnNfSample`) |
| `nf.txt` | Written at 10 Hz | **Removed** — values go in-process |
| Parity check | Yes (FIR filter replica) | **Removed** (validated) |
| Calibration GUI | Python PyQt5 GUI (`bci_gui_v2.py`) | **Unity IMGUI overlay** (Raw + Real-time FFT) |
| Logging | 4-file CSV | **Same 4-file CSV** (async) |
| UDP marker input (5007) | Yes | **Removed** (in-process via `BciServer.SendMarker`) |

---

## File Inventory

### BciCore Package (`Packages/org.iisc.bcicore.server/`)

```
org.iisc.bcicore.server/
├── package.json                         Unity package manifest
└── Runtime/
    ├── BciCore.asmdef                   Assembly definition (unsafe allowed)
    ├── Networking/
    │   ├── FrameType.cs                 Frame type byte constants (0x00–0x09)
    │   ├── FrameProtocol.cs             Stateless header parser + CMD builder
    │   ├── EspTcpListener.cs            TCP server core (recv/process threads)
    │   └── MainThreadDispatcher.cs      Background→Unity main thread marshaller
    ├── Processing/
    │   ├── ChannelRingBuffer.cs         Per-channel rolling sample buffer (thread-safe)
    │   └── FftAnalyzer.cs               Radix-2 FFT + real-time power spectrum (get_fft parity)
    ├── Logging/
    │   ├── LogQueue.cs                  Async log queue (background writer thread)
    │   └── CsvSessionLogger.cs          4-file CSV logger (same columns as Python)
    └── Api/
        ├── BciEvents.cs                 All event payload structs
        └── BciServer.cs                 PUBLIC API — the only file you call directly
```

### Game-Side Files (`Assets/LeafGame/`)

```
LeafGame/
├── Calibration/
│   └── CalibrationScreen.cs             Calibration overlay MonoBehaviour
└── Scripts/
    ├── BciCoreNfReader.cs               INfReader adapter (in-process NF)
    ├── BciCoreTriggerSender.cs          Marker sender (in-process via BciServer)
    ├── LeafGameCore.cs                  MODIFIED: NfSourceType.BciCore added
    └── LeafGameController.cs            MODIFIED: AppState.Calibration + BciCore wiring
```

---

## Architecture

### Threading Model

```
+---------------------------------------------------------------------+
| Network Thread (EspTcpListener._recvThread)                         |
|   - reads bytes from TcpClient.GetStream()                          |
|   - validates 0xAA 0x55 magic, extracts type/seq/payload            |
|   - puts (FrameType, seq, payload[]) onto BlockingCollection        |
|   Never calls Unity APIs.  Never processes.                         |
+-----------------------------+---------------------------------------+
                              | BlockingCollection<(type,seq,payload)>
+-----------------------------v---------------------------------------+
| Process Thread (EspTcpListener._processThread)                      |
|   - decodes payloads via FrameProtocol static methods               |
|   - tracks sequence gaps, per-second stats                          |
|   - calls MainThreadDispatcher.Enqueue(action) for every event      |
|   Never calls Unity APIs.                                           |
+-----------------------------+---------------------------------------+
                              | ConcurrentQueue<Action>
+-----------------------------v---------------------------------------+
| Unity Main Thread (MainThreadDispatcher.Update)                     |
|   - drains the action queue each frame                              |
|   - raises BciServer.OnNfSample, OnStats, OnRawFrame, etc.         |
|   --> CalibrationScreen.OnStats(), OnRawFrame(), etc.               |
|   --> BciCoreNfReader.OnSample()                                    |
+---------------------------------------------------------------------+
                              | LogQueue
+-----------------------------v---------------------------------------+
| Log Writer Thread (LogQueue background thread)                      |
|   - drains pre-formatted string lines                               |
|   - writes to StreamWriter (buffered, flushed when idle)            |
+---------------------------------------------------------------------+
```

### Frame Routing Gate

`BciServer.CalibrationMode` gates the expensive calibration events:

| Event | When active |
|---|---|
| `OnRawFrame` / `OnRawCounts` | CalibrationMode = true only |
| `OnProcFrame` | CalibrationMode = true only |
| `OnAlpha` / `OnSsvep` | CalibrationMode = true only |
| `OnNfSample` | **Always** |
| `OnHealth` | **Always** |
| `OnMarker` | **Always** |
| `OnMlResult` | **Always** |
| `OnStats` (1 Hz) | **Always** |

---

## Supported Frame Types

All 10 frame types from the ESP32 firmware are handled:

| Type byte | Name | Payload |
|---|---|---|
| `0x00` | `HELLO` | `fs:u16, nch:u8, nchips:u8, uv_per_count:f32, fir_len:u16, dcR:f32` + optional montage |
| `0x01` | `RAW` | `numCh × int32` counts + optional `uint16` marker |
| `0x02` | `PROC` | `numCh × float32` µV + optional `uint16` marker |
| `0x03` | `ANALYSIS` | `analysisCh × float32` alpha power |
| `0x04` | `SSVEP` | `powerA[8], snrA[8], powerB[8], snrB[8]` (all `float32`) |
| `0x05` | `NF` | shaped smi values — supports 12, 14, and 22-byte layouts |
| `0x06` | `HEALTH` | 14-byte legacy or 26-byte extended |
| `0x07` | `MARKER` | `uint16` marker code latched by board |
| `0x08` | `CMD` | PC → ESP32 marker command |
| `0x09` | `ML` | `pred_class:u8, confidence:f32, latency_ms:f32` |

---

## Public API (`BciServer.cs`)

```csharp
// ── Lifecycle
BciServer.StartServer(port = 5005, logDir = null, enableLogging = true);
BciServer.StopServer();
BciServer.EnterCalibrationMode();   // routes RAW/PROC/ALPHA/SSVEP events
BciServer.ExitCalibrationMode();    // stops routing heavy events

// ── Commands
bool sent = BciServer.SendMarker(int code);   // sends CMD frame 0x08 to ESP32

// ── Properties
BciConfig cfg          = BciServer.Config;          // fs, numCh, uvPerCount (from HELLO)
ConnectionState s      = BciServer.State;           // Disconnected / Listening / Connected
bool inCalib           = BciServer.CalibrationMode;
ChannelRingBuffer ring = BciServer.RingBuffer;      // public rolling sample buffer (2500 samples/ch)

// ── Events (main thread)
BciServer.OnHello               += (BciConfig cfg) => { };
BciServer.OnRawFrame            += (float[] uv, ushort marker, uint seq) => { };
BciServer.OnRawCounts           += (int[] counts, ushort marker, uint seq) => { };
BciServer.OnProcFrame           += (float[] uv, ushort marker, uint seq) => { };
BciServer.OnAlpha               += (AlphaFrame af) => { };
BciServer.OnSsvep               += (SsvepFrame sf) => { };
BciServer.OnNfSample            += (NfSample nf) => { };
BciServer.OnHealth              += (HealthFrame h) => { };
BciServer.OnMarker              += (ushort code, uint seq) => { };
BciServer.OnMlResult            += (MlResult ml) => { };
BciServer.OnStats               += (StatsSnapshot snap) => { };
BciServer.OnConnectionStateChanged += (ConnectionState s) => { };
```

---

## Calibration Screen (`CalibrationScreen.cs`)

### How it is activated

When `nfSourceType == NfSourceType.BciCore`, `LeafGameController.ApplySettingsAndContinue()`:

1. Calls `BciServer.StartServer()` — begins listening on port 5005.
2. Sets `state = AppState.Calibration`.
3. Adds `CalibrationScreen` as a component to the game object.
4. When the operator clicks **Proceed to Game**, the screen:
   - Calls `BciServer.ExitCalibrationMode()`.
   - Fires `OnProceedPressed` → `BeginSession()` → `AppState.Instructions`.
   - Removes itself from the game object.

### Left-Panel Metrics Mapping

| GUI label | Source |
|---|---|
| Frames / Sec | `StatsSnapshot.Sps` (true ADC sampling rate) |
| Worst \|D\| µV | Displayed as `0.00e+00` (parity not ported) |
| Alpha rel\|D\| | Displayed as `0.00e+00` |
| SSVEP P rel\|D\| | Displayed as `0.00e+00` |
| Free Heap | `HealthFrame.FreeHeap` (bytes) |
| Curr Marker | `HealthFrame.Marker` |
| Board ring drops | `StatsSnapshot.BoardDrops` |
| PC seq gaps | `StatsSnapshot.GapsRaw + GapsProc` |
| Board SPI bad | `StatsSnapshot.BoardBad` |
| Board missed DRDY | `StatsSnapshot.BoardMiss` |
| Server malformed | `StatsSnapshot.BadFrames` |
| DSP peak (µs) | `StatsSnapshot.BoardDspMax` |

### Channel Selection

- Both tabs (Raw and FFT) feature an **8-column channel toggle grid** (Row 1: CH1–8, Row 2: CH9–16, Row 3: CH17–24, Row 4: CH25–32) with colored indicator boxes.
- **Channels 9 to 16 are selected by default** on startup (8 primary analysis/SSVEP channels).
- The operator can toggle any other channels on/off individually.
- Convenient preset buttons are provided in the selector header:
  - **`[CH 9–16 (Default)]`** — Quickly restores the 8 primary experiment channels.
  - **`[Select All]`** — Enables all 32 channels.
  - **`[Clear]`** — Clears selection.

### Multi-Channel Raw Tab

- Stacks active channels in individual waveform rows. When 8 channels (CH9–16) are active, they neatly fill the vertical view without scrolling.
- Waveform GL lines are strictly bounded and clamped to channel row limits and plot bounds.
- **Remove DC (Centred)** — subtracts per-channel mean before drawing.
- **Autoscale Y** — auto-ranges each channel to its data peak.
- **±Range µV** — fixed symmetric Y axis (editable text field, active when Autoscale is off).

### FFT Spectrum Tab (Real-Time Power Spectrum)

Directly ported from `FFTTab` in `bci_gui_v2.py`:
- **Real-Time Live Processing:** Recomputed at **~12.5 Hz** (every 80 ms, matching PyQt) using `FftAnalyzer.ComputeRealtimePowerSpectrum` over the last 500 samples (`FFT_NPTS = 500`, 2 seconds at 250 SPS).
- **Processing Pipeline:** Per-channel mean subtraction, linear detrend (`seg - polyval(p, t)`), single-sided power spectrum $P_1 = 2 \cdot (|Y| / N)^2$, and DC bin zeroing (`pwr[0] = 0`).
- **Overlaid Graph:** All selected channels are plotted simultaneously on a single unified XY graph in their distinct channel colors.
- **Top Controls:**
  - **X Max (Hz):** Configurable frequency span (default 60 Hz).
  - **Autoscale Y:** Dynamically scales to `peak * 1.3f` across all enabled channels for frequencies > 0.5 Hz (matching `bci_gui_v2.py:663`).
  - **Fixed Y:** Manual Y max value when Autoscale is unchecked (default 100).
- **Overlay Legend:** Semi-transparent panel in the upper-left of the plot displaying channel color swatches and labels (`— CH9`, `— CH10`, etc.).
- **No Accumulator Lag:** Instantaneous real-time spectral response to head movement, eyes closing, or SSVEP flicker.

---

## Game Integration — `AppState` Flow

```
AppState.Setup
    |  (operator fills settings, clicks Apply & Continue)
    v  [BciCore only]
AppState.Calibration   <-- CalibrationScreen renders here
    |  (operator validates signal, clicks Proceed to Game)
    v
AppState.Instructions  <-- existing screen, unchanged
    |
AppState.Trial         <-- BciCoreNfReader feeds smi values
    |
AppState.Iti --> Summary
```

For `File` and `Tcp` modes, `AppState.Calibration` is never entered — flow goes directly `Setup → Instructions` as before.

---

## CSV Logging

For each connection run, a dedicated timestamped folder is created inside the logs directory:
`eeg_<yyyyMMdd_HHmmss>/`

Inside this folder, all four CSV files for that specific run are saved:

| File | Contents |
|---|---|
| `eeg_<ts>_raw.csv` | `seq, raw1…rawN, marker` (one row per RAW frame) |
| `eeg_<ts>_signals.csv` | `seq, filt1…filtN, marker` (PROC frame, board-filtered µV) |
| `eeg_<ts>_features.csv` | `seq, marker, smi_14gt18, smi_18gt14, smi_14gt18_shaped, smi_18gt14_shaped, alphaNF, ssvepNF, alphaLeft, alphaRight, ssvepRight14, ssvepLeft18, alpha1…alpha8` |
| `eeg_<ts>_health.csv` | `wall_clock, board_ms, seq, board_drops, pc_gaps, board_bad, board_miss, board_dspmax_us, srv_bad, free_heap, marker, event` |

Column names and ordering **exactly match** `eeg_tcp_server.py` output.

---

### Where Logs Are Saved

#### 1. On Android Tablet
All logs are written to Android's `Application.persistentDataPath`, which is the app's dedicated internal storage sandbox and **does not require special root or runtime storage permissions**:

```
/storage/emulated/0/Android/data/com.featureattention.leafdirectionresponse/files/BciCoreLogs/
```

Each run is stored in its own subfolder:
```
/storage/emulated/0/Android/data/com.featureattention.leafdirectionresponse/files/BciCoreLogs/eeg_<yyyyMMdd_HHmmss>/
    ├── eeg_<ts>_raw.csv
    ├── eeg_<ts>_signals.csv
    ├── eeg_<ts>_features.csv
    └── eeg_<ts>_health.csv
```

#### 2. On PC / Unity Editor
```
<Project-Root>/BciCoreLogs/eeg_<yyyyMMdd_HHmmss>/
```
*(Or in `%USERPROFILE%/AppData/LocalLow/DefaultCompany/LeafGame/BciCoreLogs/...` if running standalone PC build).*

To specify a custom base log directory programmatically:
```csharp
BciServer.StartServer(port: 5005, logDir: @"D:\MyLogs", enableLogging: true);
// Active run directory path can be checked anytime via:
string currentRunFolder = BciServer.CurrentSessionDir;
```

---

### How to Access & Extract Logs from the Android Tablet

There are **3 convenient methods** to retrieve the logs:

#### Method 1: Via USB Cable to PC (Windows File Explorer / MTP) — Recommended for everyday use
1. Connect the tablet to your PC using a USB-C cable.
2. On the tablet, swipe down the notification shade, tap **Charging this device via USB**, and change it to **File Transfer / MTP**.
3. On your PC, open **Windows File Explorer** and navigate to:
   ```
   This PC \ [Your Tablet Name] \ Internal shared storage \ Android \ data \ com.featureattention.leafdirectionresponse \ files \ BciCoreLogs \
   ```
4. Copy the `eeg_<timestamp>` folder directly to your PC for analysis.
> [!NOTE]
> While Android 11+ restricts some on-device third-party file manager apps from browsing `Android/data/`, connecting via **USB MTP to a PC bypasses this restriction completely** without requiring root or special permissions.

#### Method 2: Via ADB Command Line — Fastest for developers
If you have Android Platform Tools / ADB installed on your PC:
1. Connect the tablet via USB with **USB Debugging** enabled.
2. Open PowerShell or Command Prompt on your PC and run:
   ```powershell
   # Pull all session logs into a local "TabletLogs" folder:
   adb pull /storage/emulated/0/Android/data/com.featureattention.leafdirectionresponse/files/BciCoreLogs/ ./TabletLogs/
   ```
3. To view the list of runs on the tablet directly from terminal:
   ```powershell
   adb shell ls -la /storage/emulated/0/Android/data/com.featureattention.leafdirectionresponse/files/BciCoreLogs/
   ```

#### Method 3: Directly On the Tablet
1. Open a capable file manager app (e.g., **Files by Google**, **Total Commander**, or **Solid Explorer**).
2. Browse to `Internal Storage` → `Android` → `data` → `com.featureattention.leafdirectionresponse` → `files` → `BciCoreLogs`.
3. Select the session folder and share it via Google Drive, Bluetooth, or email.

---

## Testing on Android Tablet (with ESP32 SoftAP)

When deploying to a standalone tablet, the ESP32 acts as a **SoftAP (Wi-Fi Access Point)**, creating its own local Wi-Fi network that the tablet connects to directly. No external router or internet connection is required.

### 1. Network Topology (SoftAP Mode)

```
+------------------------------------+          +-----------------------------------------+
| ESP32 Board (SoftAP Host)          |          | Android Tablet (Client & TCP Server)    |
|   - SoftAP SSID: e.g. "EMG32_AP"   |  Wi-Fi   |   - Connects to "EMG32_AP"              |
|   - SoftAP IP (Gateway):192.168.4.1|<-------->|   - Tablet Assigned IP: 192.168.4.2     |
|   - Runs TCP Client                |          |   - Runs Unity Game (BciServer on 5005) |
|   - Connects to 192.168.4.2:5005   |          |   - Logs saved to app internal storage  |
+------------------------------------+          +-----------------------------------------+
```

1. **ESP32 SoftAP IP:** `192.168.4.1` (default ESP-IDF SoftAP gateway).
2. **ESP32 DHCP Server:** Automatically assigns `192.168.4.2` to the first connected device (the tablet).
3. **ESP32 Firmware Target IP:** In the ESP32 firmware (`Config.h` / `NetLink.cpp`), ensure the target server IP is set to `192.168.4.2` and port to `5005`.

---

### 2. Android Wi-Fi Setup — Crucial Step!

> [!IMPORTANT]
> **Handle Android's "No Internet Access" Check:**
> Because the ESP32 SoftAP has no internet/cellular uplink, Android will detect that the Wi-Fi has no internet access.
> - A prompt will appear: *"This network has no internet access. Stay connected?"*
> - You **MUST** select **"Always connect"** or **"Yes / Stay connected"**.
> - If you ignore this prompt or if "Switch to Mobile Data" is enabled on the tablet, Android will disconnect from the ESP32 or route socket packets to cellular data, causing the connection to fail.
> - **Recommendation:** When testing in SoftAP mode, turn off Mobile Data / Cellular on the tablet to prevent Android from bypassing the SoftAP Wi-Fi interface.

---

### 3. Step-by-Step Testing Procedure

#### Step A: Build & Install APK
1. In Unity Editor, open **File → Build Settings**.
2. Select **Android** and ensure your main game scenes are included (`SetupScene`, etc.).
3. Verify in **Project Settings → Player → Other Settings**:
   - Package Name: `com.featureattention.leafdirectionresponse`
   - Internet Access: **Require** (already configured, ensures `android.permission.INTERNET`).
4. Connect the tablet to your PC via USB and click **Build and Run** (or build the `.apk` file and install via `adb install -r leaf_game.apk`).

#### Step B: Connect Tablet to ESP32 Wi-Fi
1. Power on the ESP32 board in SoftAP mode.
2. On the tablet, open **Settings → Wi-Fi**.
3. Select your ESP32 Wi-Fi network (e.g. `EMG32_AP`). Enter the password if configured.
4. When Android warns that the network has no internet access, tap **Keep Connected**.

#### Step C: Launch the Game & Check Calibration
1. Launch **Leaf Game** on the tablet.
2. In the setup screen:
   - Set **NF Source Type** to **`BciCore`**.
   - Fill in subject ID and block parameters.
   - Tap **Apply & Continue**.
3. Unity enters the **Calibration Screen** and starts `BciServer` listening on port `5005`.
4. **Check the Title Bar:**
   - The top-right shows: `◌ Listening on <Tablet_IP>:5005…` (e.g., `192.168.4.2:5005`).
   - Confirm that `<Tablet_IP>` matches what the ESP32 is attempting to connect to.
5. If the ESP32 was already powered on, it will connect immediately. The indicator switches to:
   - `● Connected (192.168.4.1:...)` in bright green.
6. Check telemetry and plots:
   - **Frames / Sec:** should read ~250 SPS.
   - **Drops / Gaps:** should remain 0.
   - **Multi-Channel Raw Tab:** Channels 9 to 16 are visible by default. Waveforms sweep smoothly.
   - **FFT Spectrum Tab:** Real-time power spectrum updates continuously (~12.5 Hz).

#### Step D: Run the Experiment
1. When satisfied with signal quality and electrode contacts, tap **Proceed to Game**.
2. Calibration UI closes, background raw event processing is disabled to save CPU/battery, and the game enters the Instruction / Trial loop.
3. Neurofeedback is fed in-process directly to the leaf animation.

#### Step E: Collect Logs
1. After the session finishes, connect the tablet to your PC via USB.
2. Pull or copy the folder from:
   `/storage/emulated/0/Android/data/com.featureattention.leafdirectionresponse/files/BciCoreLogs/eeg_<timestamp>/`

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Status stays "Listening on 192.168.4.2:5005…" | ESP32 not connecting | 1. Confirm ESP32 firmware target IP is `192.168.4.2`.<br>2. Confirm tablet Wi-Fi is connected to ESP32 SoftAP.<br>3. Check if Android switched to cellular data (turn off Mobile Data). |
| Android drops ESP32 Wi-Fi after a few seconds | Android "no internet" watchdog | Tap the Wi-Fi notification on the tablet and choose **"Stay connected"** / **"Don't ask again"**. |
| Tablet IP is different (e.g., `192.168.4.3`) | Another device connected to AP first | Look at the IP displayed on the Calibration screen title bar. If it's `192.168.4.3`, reconnect the tablet first or update ESP32 target IP. |
| Cannot see `Android/data` folder on tablet | Android 11+ scoped storage | Connect tablet to PC via USB and view in Windows File Explorer (MTP mode has no restriction), or use `adb pull`. |
| `SocketException (10048)` | Port 5005 in use | Kill background instance or check other listeners. |
| Waveforms absent | CalibrationMode not entered | Confirm `NF Source Type = BciCore` in setup screen. |
| NF bar does not move | NF frames not arriving | Confirm firmware sends type `0x05`; check WireFps. |
| `BciCore` not found | Package not registered | Check `Packages/manifest.json` has `file:org.iisc.bcicore.server`. |
| `unsafe` compile error | asmdef mismatch | Confirm `BciCore.asmdef` has `"allowUnsafeCode": true`. |
| CSV files empty | Log directory not writable | Logs write to `persistentDataPath` automatically on Android. |

---

## What Was Intentionally Not Ported

| Python feature | Decision |
|---|---|
| FIR filter parity check (61-tap replica, per-channel DC blocker) | **Omitted** — firmware validated |
| Goertzel recompute (server-side alpha/SSVEP) | **Omitted** — same reason |
| AMI/SMI server-side recompute | **Omitted** |
| UDP 5007 marker listener (`_marker_udp_loop`) | **Omitted** — in-process via `BciServer.SendMarker` |
| NF fan-out TCP port 5006 / `nf.txt` | **Replaced** by `BciServer.OnNfSample` C# event |

The `File` and `Tcp` NF source modes remain **fully functional** and unchanged.

---

*Generated: 2026-09-04 — BciCore v1.0.0*
