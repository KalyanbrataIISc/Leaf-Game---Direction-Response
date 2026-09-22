using System;
using System.Globalization;
using System.IO;
using System.Text;
using BciCore;
using LeafGame;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

namespace PaddleGame
{
    /// <summary>
    /// Unity implementation of gameBreakoutv7p1_val.m. The two ends of the
    /// paddle are 17/19 Hz contrast-modulated gratings and the fresh BCI pair
    /// drives horizontal velocity using (right - left), a deadzone and a cap.
    /// </summary>
    public sealed class PaddleGameController : MonoBehaviour
    {
        enum GameState { WaitingForLaunch, Calibration, Instructions, Preview, Trial, Feedback, Summary, Fatal }

        [Header("Experiment Design")]
        [Tooltip("Must be even so the session contains exactly the same number of left and right cues.")]
        [SerializeField, Min(2)] int trialsPerSession = 24;
        [Tooltip("Maximum allowed run of identical cue directions in the pseudorandom schedule (1-3).")]
        [SerializeField, Range(1, 3)] int maxConsecutiveDirectionTrials = 3;
        [SerializeField] bool useFixedRandomSeed;
        [SerializeField] int fixedRandomSeed = 918273;

        [Header("Timing (seconds)")]
        [SerializeField, Min(0)] float preSpawnDelaySec = 2f;
        [SerializeField, Min(0.1f)] float maxTrialDurationSec = 10f;
        [SerializeField, Min(0)] float feedbackDurationSec = 2f;
        [SerializeField, Min(0.01f)] float traceLogIntervalSec = 0.1f;

        [Header("Paddle (pixels at 1920 x 1080)")]
        [SerializeField, Min(0)] float fieldMarginPx;
        [SerializeField, Min(1)] float paddleWidthPx = 900f;
        [SerializeField, Min(1)] float paddleHeightPx = 200f;
        [SerializeField, Min(0)] float paddleBottomMarginPx = 50f;
        [SerializeField, Min(0)] float paddleBorderPx = 3f;
        [SerializeField, Min(1)] float paddleGratingWidthPx = 300f;
        [SerializeField, Min(1)] float gratingBarWidthPx = 14f;
        [SerializeField, Min(0)] float edgeInsetPx = 20f;
        [SerializeField, Min(1)] float edgeLineThicknessPx = 4f;
        [SerializeField, Min(0)] float hudHeightPx = 80f;

        [Header("Neurofeedback")]
        [SerializeField, Min(0)] float paddleNfGainPxPerSec = 600f;
        [SerializeField, Min(0)] float paddleMaxSpeedPxPerSec = 600f;
        [SerializeField, Min(0)] float paddleNfDeadzone = 0.02f;
        [SerializeField] bool allowKeyboardPaddle = true;
        [SerializeField, Min(0)] float keyboardPaddleSpeedPxPerSec = 600f;
        [SerializeField] string dataFolderName = "data";

        [Header("SSVEP")]
        [SerializeField, Min(0.01f)] float gratingLeftFreqHz = 17f;
        [SerializeField, Min(0.01f)] float gratingRightFreqHz = 19f;
        [SerializeField, Min(1)] int targetFrameRate = 120;
        [SerializeField, Range(1, 4)] int verticalSyncCount = 1;

        [Header("Trigger Codes")]
        [SerializeField, Range(0, 255)] int resetTrigger;
        [Tooltip("Trial-start marker sent when the direction cue points left.")]
        [FormerlySerializedAs("trialStartTrigger")]
        [SerializeField, Range(0, 255)] int trialStartLeftTrigger = 20;
        [Tooltip("Trial-start marker sent when the direction cue points right.")]
        [SerializeField, Range(0, 255)] int trialStartRightTrigger = 21;
        [SerializeField, Range(0, 255)] int trialStopTrigger = 30;
        [SerializeField, Range(1, 65535)] int udpTriggerPort = 5007;
        [SerializeField] string udpTriggerHost = "10.36.17.144";

        [Header("Colors (sRGB)")]
        [SerializeField] Color backgroundColor = new Color32(10, 12, 20, 255);
        [SerializeField] Color gratingMidColor = new Color32(128, 128, 128, 255);
        [SerializeField] Color leftBarHigh = new Color32(0, 230, 230, 255);
        [SerializeField] Color leftBarLow = new Color32(0, 80, 80, 255);
        [SerializeField] Color rightBarHigh = new Color32(255, 220, 0, 255);
        [SerializeField] Color rightBarLow = new Color32(100, 80, 0, 255);
        [SerializeField] Color paddleBodyColor = new Color32(30, 30, 60, 255);
        [SerializeField] Color paddleBorderColor = new Color32(0, 180, 220, 255);
        [SerializeField] Color edgeLineColor = new Color32(200, 200, 200, 255);
        [SerializeField] Color hudColor = new Color32(220, 220, 255, 255);
        [SerializeField] Color readyColor = new Color32(0, 255, 200, 255);
        [SerializeField] Color successColor = new Color32(0, 255, 0, 255);
        [SerializeField] Color failureColor = new Color32(255, 0, 0, 255);

        [Header("Text")]
        [SerializeField] string instructionsText = "INSTRUCTIONS\n\nMove the paddle toward the edge shown by the arrow. Focus on the left or right flickering end of the paddle to steer it.\n\nReach the cued edge before time runs out. Reaching the opposite edge ends the trial as a failure.";
        [SerializeField] string beginButtonLabel = "Begin session";

        [NonSerialized] bool sharedLaunchConfigured;
        [NonSerialized] bool sharedEnableBciLogging = true;
        [NonSerialized] Action sharedReturnToLauncher;
        [NonSerialized] string participant = "000";
        [NonSerialized] string session = "000";

        GameState state = GameState.WaitingForLaunch;
        CalibrationScreen calibration;
        BciCoreNfReader nfReader;
        BciCoreTriggerSender triggers;
        PaddleCsvLogger logger;
        System.Random rng;
        GUIStyle titleStyle, bodyStyle, buttonStyle, hudStyle, arrowStyle;
        bool runtimeStarted;
        int trialIndex, successCount, targetSide, droppedFrames;
        int[] trialSides;
        float paddleCenterX, lastVelocity, nfLeft, nfRight;
        double experimentT0, stateStartedAt, trialStartedAt, phaseT0, lastTraceAt, lastFrameAt;
        string feedbackText = "";
        string fatalMessage = "";

        public void ConfigureSharedLaunch(string participantNumber, string sessionNumber, bool enableBciLogging = true, Action returnToLauncher = null)
        {
            participant = string.IsNullOrWhiteSpace(participantNumber) ? "000" : participantNumber.Trim();
            session = string.IsNullOrWhiteSpace(sessionNumber) ? "000" : sessionNumber.Trim();
            sharedEnableBciLogging = enableBciLogging;
            sharedReturnToLauncher = returnToLauncher;
            sharedLaunchConfigured = true;
        }

        public bool ValidateSettings(out string error)
        {
            if (trialsPerSession < 2)
            {
                error = "Paddle Trials Per Session must be at least 2.";
                return false;
            }
            if ((trialsPerSession & 1) != 0)
            {
                error = "Paddle Trials Per Session must be even so left and right cues can be exactly balanced.";
                return false;
            }
            if (maxConsecutiveDirectionTrials < 1 || maxConsecutiveDirectionTrials > 3)
            {
                error = "Paddle Max Consecutive Direction Trials must be between 1 and 3.";
                return false;
            }
            if (trialStartLeftTrigger == trialStartRightTrigger)
            {
                error = "Paddle left and right trial-start triggers must be different.";
                return false;
            }
            error = "";
            return true;
        }

        void Start()
        {
            if (!sharedLaunchConfigured)
            {
                Fatal("This game must be started from the Feature Attention Games launcher.");
                return;
            }

            try
            {
                QualitySettings.vSyncCount = Mathf.Max(1, verticalSyncCount);
                Application.targetFrameRate = Mathf.Max(1, targetFrameRate);
                Application.runInBackground = true;
                Screen.sleepTimeout = SleepTimeout.NeverSleep;
                int seed = useFixedRandomSeed ? fixedRandomSeed : unchecked(Environment.TickCount * 397 ^ participant.GetHashCode() ^ session.GetHashCode());
                rng = new System.Random(seed);
                experimentT0 = Time.realtimeSinceStartupAsDouble;
                string dataPath = Path.IsPathRooted(dataFolderName) ? dataFolderName : Path.Combine(Application.persistentDataPath, dataFolderName);
                logger = new PaddleCsvLogger(dataPath, participant, session);
                BciServer.StartServer(enableLogging: sharedEnableBciLogging);
                triggers = new BciCoreTriggerSender(udpTriggerHost.Trim(), udpTriggerPort);
                triggers.Send("reset", resetTrigger);
                runtimeStarted = true;
                state = GameState.Calibration;
                calibration = gameObject.AddComponent<CalibrationScreen>();
                calibration.OnProceedPressed = ProceedFromCalibration;
                calibration.OnBackPressed = ReturnToSharedLauncher;
            }
            catch (Exception e)
            {
                Fatal(e.Message);
                Debug.LogException(e);
            }
        }

        void ProceedFromCalibration()
        {
            if (calibration != null) Destroy(calibration);
            calibration = null;
            BciServer.ExitCalibrationMode();
            nfReader = new BciCoreNfReader();
            state = GameState.Instructions;
        }

        void Update()
        {
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame &&
                state != GameState.Calibration && state != GameState.Summary && state != GameState.Fatal)
            {
                EndSession();
                return;
            }

            double now = Time.realtimeSinceStartupAsDouble;
            if (state == GameState.Preview)
            {
                if (now - stateStartedAt >= preSpawnDelaySec) BeginTrialMotion(now);
            }
            else if (state == GameState.Trial)
            {
                UpdateTrial(now);
            }
            else if (state == GameState.Feedback && now - stateStartedAt >= feedbackDurationSec)
            {
                trialIndex++;
                if (trialIndex >= trialsPerSession) EndSession();
                else StartTrialPreview(now);
            }
        }

        void BeginSession()
        {
            if (!ValidateSettings(out string settingsError))
            {
                Fatal(settingsError);
                return;
            }
            trialIndex = 0;
            successCount = 0;
            trialSides = BuildBalancedTrialSides(trialsPerSession);
            StartTrialPreview(Time.realtimeSinceStartupAsDouble);
        }

        void StartTrialPreview(double now)
        {
            if (trialSides == null || trialIndex < 0 || trialIndex >= trialSides.Length)
            {
                Fatal("The Paddle direction schedule is unavailable for this trial.");
                return;
            }
            targetSide = trialSides[trialIndex];
            paddleCenterX = Screen.width * 0.5f;
            nfLeft = nfRight = lastVelocity = 0f;
            droppedFrames = 0;
            lastTraceAt = double.NegativeInfinity;
            lastFrameAt = now;
            phaseT0 = now;
            stateStartedAt = now;
            state = GameState.Preview;
        }

        int[] BuildBalancedTrialSides(int count)
        {
            var sides = new int[count];
            int half = count / 2;
            for (int i = 0; i < count; i++) sides[i] = i < half ? -1 : 1;

            const int MaxShuffleAttempts = 1024;
            for (int attempt = 0; attempt < MaxShuffleAttempts; attempt++)
            {
                for (int i = sides.Length - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    int swap = sides[i];
                    sides[i] = sides[j];
                    sides[j] = swap;
                }
                if (HasAcceptableDirectionRuns(sides)) return sides;
            }

            bool startLeft = rng.Next(0, 2) == 0;
            for (int i = 0; i < count; i++) sides[i] = ((i & 1) == 0) == startLeft ? -1 : 1;
            return sides;
        }

        bool HasAcceptableDirectionRuns(int[] sides)
        {
            int runLength = 1;
            for (int i = 1; i < sides.Length; i++)
            {
                runLength = sides[i] == sides[i - 1] ? runLength + 1 : 1;
                if (runLength > maxConsecutiveDirectionTrials) return false;
            }
            return true;
        }

        void BeginTrialMotion(double now)
        {
            trialStartedAt = now;
            phaseT0 = now;
            stateStartedAt = now;
            lastFrameAt = now;
            bool isLeftCue = targetSide < 0;
            triggers.Send(isLeftCue ? "trialstart_left" : "trialstart_right",
                isLeftCue ? trialStartLeftTrigger : trialStartRightTrigger);
            state = GameState.Trial;
        }

        void UpdateTrial(double now)
        {
            float dt = Mathf.Max(0f, Time.unscaledDeltaTime);
            float nominal = 1f / Mathf.Max(1, targetFrameRate);
            if (dt > nominal * 1.5f)
            {
                int skipped = Mathf.Max(1, Mathf.RoundToInt(dt / nominal) - 1);
                droppedFrames += skipped;
                logger.AddDropped(trialIndex + 1, Time.frameCount, now - experimentT0, dt - nominal);
            }

            double freshLeft = 0, freshRight = 0;
            bool readOk = nfReader != null && nfReader.TryReadPair(out freshLeft, out freshRight);
            if (readOk)
            {
                nfLeft = (float)freshLeft;
                nfRight = (float)freshRight;
            }

            float signed = nfRight - nfLeft;
            if (Mathf.Abs(signed) < paddleNfDeadzone) signed = 0f;
            float velocity = Mathf.Clamp(paddleNfGainPxPerSec * signed, -paddleMaxSpeedPxPerSec, paddleMaxSpeedPxPerSec);
            if (allowKeyboardPaddle && Keyboard.current != null)
            {
                if (Keyboard.current.leftArrowKey.isPressed) velocity = -keyboardPaddleSpeedPxPerSec;
                if (Keyboard.current.rightArrowKey.isPressed) velocity = keyboardPaddleSpeedPxPerSec;
            }
            lastVelocity = velocity;

            GetGeometry(out float fieldLeft, out _, out float fieldRight, out _, out float paddleWidth, out _, out _, out _, out _);
            float border = Mathf.Max(1f, paddleBorderPx * (Screen.width / 1920f));
            float minCenter = fieldLeft + paddleWidth * 0.5f + border;
            float maxCenter = fieldRight - paddleWidth * 0.5f - border;
            paddleCenterX = Mathf.Clamp(paddleCenterX + velocity * dt * (Screen.width / 1920f), minCenter, maxCenter);

            float left = paddleCenterX - paddleWidth * 0.5f;
            float right = paddleCenterX + paddleWidth * 0.5f;
            float leftEdge = fieldLeft + edgeInsetPx * (Screen.width / 1920f);
            float rightEdge = fieldRight - edgeInsetPx * (Screen.width / 1920f);
            bool correctEdge = targetSide < 0 ? left <= leftEdge : right >= rightEdge;
            bool wrongEdge = targetSide < 0 ? right >= rightEdge : left <= leftEdge;

            if (now - lastTraceAt >= traceLogIntervalSec)
            {
                lastTraceAt = now;
                logger.AddTrace(trialIndex + 1, Time.frameCount, now - experimentT0, nfLeft, nfRight, signed, readOk, paddleCenterX, velocity, targetSide < 0 ? leftEdge : rightEdge);
            }

            if (correctEdge) FinishTrial(true, false, "SUCCESS!", now);
            else if (wrongEdge) FinishTrial(false, false, "WRONG EDGE", now);
            else if (now - trialStartedAt >= maxTrialDurationSec) FinishTrial(false, true, "TIMEOUT!", now);
            lastFrameAt = now;
        }

        void FinishTrial(bool success, bool timeout, string message, double now)
        {
            triggers.Send("trialstop", trialStopTrigger);
            if (success) successCount++;
            float targetDistance = Screen.width * 0.5f - edgeInsetPx * (Screen.width / 1920f);
            logger.AddTrial(trialIndex + 1, trialStartedAt - experimentT0, now - experimentT0,
                targetDistance, targetSide, success, timeout, droppedFrames);
            feedbackText = $"Trial {trialIndex + 1}: {message}";
            stateStartedAt = now;
            state = GameState.Feedback;
        }

        void EndSession()
        {
            if (state == GameState.Trial) triggers?.Send("trialstop", trialStopTrigger);
            logger?.Flush();
            state = GameState.Summary;
        }

        void Fatal(string message)
        {
            fatalMessage = message;
            state = GameState.Fatal;
        }

        void OnDestroy()
        {
            if (!runtimeStarted) return;
            if (state == GameState.Trial) triggers?.Send("trialstop", trialStopTrigger);
            logger?.Flush();
            nfReader?.Dispose();
            triggers?.Dispose();
            BciServer.StopServer();
        }

        void OnGUI()
        {
            if (state == GameState.Calibration || state == GameState.WaitingForLaunch) return;
            BuildStyles();
            Fill(new Rect(0, 0, Screen.width, Screen.height), Render(backgroundColor));

            if (state == GameState.Instructions)
            {
                float w = Mathf.Min(Screen.width * 0.82f, 1000f * UiScale());
                Rect card = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.18f, w, Screen.height * 0.56f);
                Fill(card, new Color(0.06f, 0.10f, 0.17f, 0.96f));
                GUI.Label(new Rect(card.x + 30, card.y + 24, card.width - 60, card.height - 120), instructionsText, bodyStyle);
                if (GUI.Button(new Rect(card.center.x - 170 * UiScale(), card.yMax - 76 * UiScale(), 340 * UiScale(), 54 * UiScale()), beginButtonLabel, buttonStyle)) BeginSession();
                return;
            }

            if (state == GameState.Summary)
            {
                GUI.Label(new Rect(0, Screen.height * 0.25f, Screen.width, Screen.height * 0.5f), $"SESSION COMPLETE\n\nSUCCESS RATE  {(trialsPerSession == 0 ? 0 : 100f * successCount / trialsPerSession):F1}%\n\n{successCount} / {trialsPerSession} targets reached", titleStyle);
                if (GUI.Button(new Rect(Screen.width * 0.5f - 170 * UiScale(), Screen.height * 0.78f, 340 * UiScale(), 54 * UiScale()), "Back to games", buttonStyle)) ReturnToSharedLauncher();
                return;
            }

            if (state == GameState.Fatal)
            {
                GUI.Label(new Rect(Screen.width * 0.1f, Screen.height * 0.25f, Screen.width * 0.8f, Screen.height * 0.5f), fatalMessage, titleStyle);
                return;
            }

            if (state == GameState.Feedback)
            {
                Color color = feedbackText.Contains("SUCCESS") ? Render(successColor) : Render(failureColor);
                Color old = GUI.color; GUI.color = color;
                GUI.Label(new Rect(0, Screen.height * 0.3f, Screen.width, Screen.height * 0.4f), feedbackText, titleStyle);
                GUI.color = old;
                return;
            }

            DrawPlayfield(Time.realtimeSinceStartupAsDouble);
        }

        void DrawPlayfield(double now)
        {
            GetGeometry(out float fieldLeft, out float fieldTop, out float fieldRight, out float fieldBottom,
                out float paddleWidth, out float paddleHeight, out float paddleTop, out float paddleBottom, out float sx);
            float leftEdge = fieldLeft + edgeInsetPx * sx;
            float rightEdge = fieldRight - edgeInsetPx * sx;
            Fill(new Rect(leftEdge, fieldTop, Mathf.Max(1f, edgeLineThicknessPx * sx), fieldBottom - fieldTop), Render(edgeLineColor));
            Fill(new Rect(rightEdge, fieldTop, Mathf.Max(1f, edgeLineThicknessPx * sx), fieldBottom - fieldTop), Render(edgeLineColor));

            string arrow = targetSide < 0 ? "<" : ">";
            GUI.Label(new Rect(Screen.width * 0.5f - 190 * sx, Screen.height * 0.38f, 380 * sx, 180 * (Screen.height / 1080f)), arrow, arrowStyle);

            Rect paddle = new Rect(paddleCenterX - paddleWidth * 0.5f, paddleTop, paddleWidth, paddleHeight);
            float border = Mathf.Max(1f, paddleBorderPx * sx);
            Fill(new Rect(paddle.x - border, paddle.y - border, paddle.width + 2 * border, paddle.height + 2 * border), Render(paddleBorderColor));
            Fill(paddle, Render(paddleBodyColor));

            float gratingWidth = paddleGratingWidthPx * sx;
            float barWidth = Mathf.Max(1f, gratingBarWidthPx * sx);
            float t = (float)Math.Max(0, now - phaseT0);
            DrawGrating(new Rect(paddle.x, paddle.y, gratingWidth, paddle.height), barWidth, t, gratingLeftFreqHz, leftBarHigh, leftBarLow);
            DrawGrating(new Rect(paddle.xMax - gratingWidth, paddle.y, gratingWidth, paddle.height), barWidth, t, gratingRightFreqHz, rightBarHigh, rightBarLow);

            string phase = state == GameState.Preview ? "GET READY" : $"TIME: {Math.Max(0, maxTrialDurationSec - (now - trialStartedAt)):F1}s";
            GUI.Label(new Rect(28 * sx, 8, Screen.width * 0.35f, fieldTop - 8), $"TRIAL {trialIndex + 1}/{trialsPerSession}", hudStyle);
            GUI.Label(new Rect(Screen.width * 0.62f, 8, Screen.width * 0.35f, fieldTop - 8), phase, hudStyle);
        }

        void DrawGrating(Rect rect, float barWidth, float t, float frequency, Color high, Color low)
        {
            float envelope = 0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * frequency * t);
            Color a = Color.Lerp(Render(gratingMidColor), Render(high), envelope);
            Color b = Color.Lerp(Render(gratingMidColor), Render(low), envelope);
            int bars = Mathf.CeilToInt(rect.width / barWidth);
            for (int i = 0; i < bars; i++)
            {
                float x = rect.x + i * barWidth;
                Fill(new Rect(x, rect.y, Mathf.Min(barWidth, rect.xMax - x), rect.height), (i & 1) == 0 ? a : b);
            }
        }

        void GetGeometry(out float fieldLeft, out float fieldTop, out float fieldRight, out float fieldBottom,
            out float paddleWidth, out float paddleHeight, out float paddleTop, out float paddleBottom, out float sx)
        {
            sx = Screen.width / 1920f;
            float sy = Screen.height / 1080f;
            fieldLeft = fieldMarginPx * sx;
            fieldTop = (fieldMarginPx + hudHeightPx) * sy;
            fieldRight = Screen.width - fieldMarginPx * sx;
            fieldBottom = Screen.height - fieldMarginPx * sy;
            paddleWidth = paddleWidthPx * sx;
            paddleHeight = paddleHeightPx * sy;
            paddleBottom = fieldBottom - paddleBottomMarginPx * sy;
            paddleTop = paddleBottom - paddleHeight;
        }

        float UiScale() => Mathf.Clamp(Mathf.Min(Screen.width / 1920f, Screen.height / 1080f), 0.55f, 2.5f);

        void BuildStyles()
        {
            float u = UiScale();
            if (titleStyle != null && titleStyle.fontSize == Mathf.RoundToInt(44 * u)) return;
            titleStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(44 * u), alignment = TextAnchor.MiddleCenter, wordWrap = true, fontStyle = FontStyle.Bold };
            titleStyle.normal.textColor = Color.white;
            bodyStyle = new GUIStyle(titleStyle) { fontSize = Mathf.RoundToInt(28 * u), fontStyle = FontStyle.Normal };
            buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = Mathf.RoundToInt(28 * u), fontStyle = FontStyle.Bold };
            hudStyle = new GUIStyle(titleStyle) { fontSize = Mathf.RoundToInt(28 * u) };
            arrowStyle = new GUIStyle(titleStyle) { fontSize = Mathf.RoundToInt(150 * u) };
            arrowStyle.normal.textColor = Render(readyColor);
        }

        static void Fill(Rect rect, Color color)
        {
            Color old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = old;
        }

        static Color Render(Color srgb) => QualitySettings.activeColorSpace == ColorSpace.Linear ? srgb.linear : srgb;

        void ReturnToSharedLauncher()
        {
            if (calibration != null) Destroy(calibration);
            calibration = null;
            Action callback = sharedReturnToLauncher;
            callback?.Invoke();
            Destroy(gameObject);
        }

        sealed class PaddleCsvLogger
        {
            const string TrialHeader = "TrialNumber,TrialStart,TrialEnd,TrialDuration,TargetDistancePx,TargetSide,Success,Timeout,DroppedFrameCount";
            const string TraceHeader = "TrialNumber,FrameNumber,SampleTime,NF17,NF19,NFSigned,NFReadOk,PaddleCenterX,PaddleVxPxPerSec,TargetEdgeX";
            const string DropHeader = "TrialNumber,FrameNumber,VBLTime,MissedBySec";
            static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
            readonly string trialPath, tracePath, dropPath;
            readonly StringBuilder trials = new StringBuilder(4096), traces = new StringBuilder(32768), drops = new StringBuilder(4096);
            bool flushed;

            public PaddleCsvLogger(string root, string participant, string session)
            {
                Directory.CreateDirectory(root);
                string tag = "p" + participant + "_b" + session;
                trialPath = Ensure(root, tag + "_breakoutval_trialdata.csv", TrialHeader);
                tracePath = Ensure(root, tag + "_breakoutval_trace.csv", TraceHeader);
                dropPath = Ensure(root, tag + "_breakoutval_droppedframes.csv", DropHeader);
            }

            static string Ensure(string root, string name, string header)
            {
                string path = Path.Combine(root, name);
                if (!File.Exists(path)) File.WriteAllText(path, header + Environment.NewLine);
                return path;
            }

            static string F(double value) => value.ToString("F6", Inv);
            static int B(bool value) => value ? 1 : 0;

            public void AddTrial(int number, double start, double end, float distance, int side, bool success, bool timeout, int dropped)
            {
                trials.Append(number).Append(',').Append(F(start)).Append(',').Append(F(end)).Append(',').Append(F(end - start)).Append(',')
                    .Append(F(distance)).Append(',').Append(side).Append(',').Append(B(success)).Append(',').Append(B(timeout)).Append(',').Append(dropped).AppendLine();
            }

            public void AddTrace(int trial, int frame, double sampleTime, float left, float right, float signed, bool readOk, float center, float velocity, float target)
            {
                traces.Append(trial).Append(',').Append(frame).Append(',').Append(F(sampleTime)).Append(',').Append(F(left)).Append(',')
                    .Append(F(right)).Append(',').Append(F(signed)).Append(',').Append(B(readOk)).Append(',').Append(F(center)).Append(',')
                    .Append(F(velocity)).Append(',').Append(F(target)).AppendLine();
            }

            public void AddDropped(int trial, int frame, double time, double missedBy)
            {
                drops.Append(trial).Append(',').Append(frame).Append(',').Append(F(time)).Append(',').Append(F(missedBy)).AppendLine();
            }

            public void Flush()
            {
                if (flushed) return;
                flushed = true;
                if (trials.Length > 0) File.AppendAllText(trialPath, trials.ToString());
                if (traces.Length > 0) File.AppendAllText(tracePath, traces.ToString());
                if (drops.Length > 0) File.AppendAllText(dropPath, drops.ToString());
            }
        }
    }
}
