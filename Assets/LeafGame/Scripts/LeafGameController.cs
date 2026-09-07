using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Serialization;
using BciCore;

#pragma warning disable 0414 // Serialized fields accessed via reflection in InitializeRuntimeSettings

namespace LeafGame
{
    public sealed partial class LeafGameController : MonoBehaviour
    {
        // AppState.Calibration is inserted between Setup and Instructions when
        // nfSourceType == NfSourceType.BciCore.  All other paths skip it.
        enum AppState { Setup, Calibration, Instructions, Trial, Iti, Summary, Fatal }

        sealed class RuntimeSetting
        {
            public FieldInfo field;
            public string header;
            public string label;
            public string[] values;
            public bool multiline;
        }

        [Header("Experiment Design")]
        [SerializeField, Min(1)] int trialsPerBlock=24;
        [SerializeField, Range(0f,1f)] float cueC1Probability=0.5f;
        [SerializeField] bool useFixedRandomSeed=false;
        [SerializeField] int fixedRandomSeed=918273;
        [SerializeField] bool mixParticipantAndBlockIntoSeed=true;

        [Header("Trial Timing (seconds)")]
        [SerializeField, Min(0)] double PreCueConstantSec=1;
        [SerializeField, Min(0.0001f)] double PreCueExpMeanSec=3;
        [SerializeField, Min(0)] double PreCueExpMaxSec=5;
        [SerializeField, Min(0.0001f)] double NfRevealTimeoutSec=10;
        [SerializeField, Min(0.0001f)] double ResponseTimeoutSec=4;
        [SerializeField, Min(0)] double FeedbackSec=1;
        [SerializeField, Min(0)] double ItiSec=1;

        [Header("Neurofeedback")]
        [SerializeField] NfSourceType nfSourceType=NfSourceType.Tcp;
        [FormerlySerializedAs("nfFileName")]
        [SerializeField] string nfFilePath="nf.txt";
        [SerializeField, Min(32)] int nfPathMaximumCharacters=1024;
        [SerializeField] string nfTcpHost="10.36.17.144";
        [SerializeField, Range(1,65535)] int nfTcpPort=5006;
        [SerializeField, Min(0.1f)] double nfTcpTimeoutSec=10;
        [SerializeField, Min(0)] double nfStreamStallWarnSec=2;
        [SerializeField] string dataFolderName="data";
        [SerializeField, Min(0.001f)] double NfTraceIntervalSec=0.1;
        [SerializeField, Min(0)] double NfGreenHoldSec=1;
        [SerializeField, Min(0)] double NfRatePerUnitPerSec=3;
        [SerializeField, Min(0.0001f)] double NfClip=1;
        [SerializeField, Range(0f,1f)] double NfWhiteTopRelaxation=0.10;

        [Header("SSVEP (precomputed before every trial)")]
        [SerializeField, Min(0.01f)] double frequencyC1Hz=19;
        [SerializeField, Min(0.01f)] double frequencyC2Hz=23;
        [SerializeField, Min(0.1f)] double precomputedPhaseHorizonSec=300;
        [SerializeField, Min(0)] double phaseHorizonSafetyMarginSec=2;
        [SerializeField, Range(0f,1f)] double ssvepMeanLuminance=0.5;
        [SerializeField, Range(0f,1f)] double ssvepModulationDepth=1;
        [SerializeField] double phaseC1Degrees=0;
        [SerializeField] double phaseC2Degrees=0;
        [SerializeField, Min(0)] float requestedDisplayRefreshRateHz=120;
        [SerializeField, Min(0)] int targetFrameRateOverride=0;
        [SerializeField, Range(1,4)] int verticalSyncCount=1;
        [SerializeField] bool requestClosestSupportedRefreshRate=true;
        [SerializeField] bool abortTrialOnRefreshRateChange=true;
        [SerializeField, Min(0.01f)] float refreshRateChangeToleranceHz=0.5f;
        [SerializeField, Min(1)] float minimumValidRefreshHz=30;
        [SerializeField, Min(1)] float fallbackRefreshHz=60;
        [SerializeField, Min(0)] float presentationLookAheadFrames=1;
        [SerializeField] bool runInBackground=true;
        [SerializeField] bool preventScreenSleep=true;

        [Header("Trigger UDP (uses NF TCP Host)")]
        [SerializeField, Range(1,65535)] int triggerPort=5007;

        [Header("Trigger Codes (MATLAB defaults)")]
        [SerializeField, Range(0,255)] int resetTrigger=0;
        [SerializeField, Range(0,255)] int trialStartTrigger=20;
        [SerializeField, Range(0,255)] int cueOnsetTrigger=45;
        [SerializeField, Range(0,255)] int successTrigger=50;
        [SerializeField, Range(0,255)] int responseTrigger=40;
        [SerializeField, Range(0,255)] int trialStopTrigger=30;

        [Header("Responsive Screen Layout")]
        [SerializeField] bool useResponsiveSizing=true;
        [SerializeField, Min(1)] float referenceShortSidePx=1080;
        [SerializeField, Min(0.01f)] float minimumScreenScale=0.05f;
        [SerializeField, Min(0.01f)] float maximumScreenScale=4f;
        [SerializeField, Range(0.05f,1f)] float autoFitMinimumFraction=0.25f;
        [SerializeField, Range(0.01f,0.25f)] float autoFitStepFraction=0.05f;
        [SerializeField, Range(0.25f,1f)] float stimulusWidthFraction=1f;
        [SerializeField, Range(0.25f,1f)] float stimulusHeightFraction=1f;
        [SerializeField, Min(0)] float safeAreaPaddingReferencePx=0;

        [Header("Leaves (pixels at reference short side)")]
        [SerializeField, Min(1)] float leafSizePx=100;
        [SerializeField, Min(0.01f)] float leafWidthMultiplier=0.42f;
        [SerializeField, Min(0)] float leafBorderMultiplier=0.20f;
        [SerializeField, Min(0)] float leafSpeedPxPerSec=200;
        [SerializeField, Range(1,100)] int leavesPerFlock=10;
        [SerializeField, Min(0)] float laneClearanceMarginPx=6;
        [SerializeField, Min(0)] int maxFieldShrinkPx=400;
        [SerializeField, Min(1)] int minRepeatsPerAxis=2;
        [SerializeField, Range(3,64)] int leafArcPoints=10;
        [SerializeField] bool verifyLeafPathsAtSetup=true;
        [SerializeField, Min(1)] int verificationMaxFrames=40000;

        [Header("Cue (pixels at reference short side)")]
        [SerializeField, Min(1)] float cueWidthPx=220;
        [SerializeField, Min(1)] float cueHeightPx=90;
        [SerializeField, Min(0)] float cueCornerRadiusPx=20;
        [SerializeField, Range(1,32)] int cueCornerSegments=8;
        [SerializeField, Min(1)] int cueFontSize=64;
        [SerializeField, Min(0.01f)] float cueCharacterSize=0.5f;
        [SerializeField, Min(0)] float cueTextOutlinePx=2f;

        [Header("Input")]
        [SerializeField] bool enableKeyboardArrows=true;
        [SerializeField] bool enableSwipeResponses=true;
        [SerializeField] bool escapeEndsBlock=true;
        [SerializeField, Min(1)] float swipeThresholdPx=40;

        [Header("Colors (sRGB)")]
        [SerializeField] Color c1Srgb=new Color32(0,191,255,255);
        [SerializeField] Color c2Srgb=new Color32(255,140,0,255);
        [SerializeField] Color cuePreSrgb=new Color32(64,64,64,255);
        [SerializeField] Color successSrgb=new Color32(0,255,0,255);
        [SerializeField] Color failureSrgb=new Color32(255,0,0,255);
        [SerializeField] Color greenZoneSrgb=new Color32(179,255,179,255);
        [SerializeField] Color backgroundSrgb=Color.black;
        [SerializeField] Color preCueLeafFillSrgb=Color.black;
        [SerializeField] Color cueTextSrgb=Color.black;
        [SerializeField] Color cueTextOutlineSrgb=Color.white;
        [SerializeField] Color instructionBorderSrgb=Color.white;

        [Header("UI (pixels at 1080px short side)")]
        [SerializeField, Min(1)] float uiReferenceShortSidePx=1080;
        [SerializeField, Min(0.01f)] float minimumUiScale=0.15f;
        [SerializeField, Min(0.01f)] float maximumUiScale=4f;
        [SerializeField, Min(1)] int titleFontSize=35;
        [SerializeField, Min(1)] int bodyFontSize=25;
        [SerializeField, Min(1)] int centerFontSize=22;
        [SerializeField, Min(1)] int buttonFontSize=26;
        [SerializeField] string applicationTitle="Leaf Game - Direction Response";
        [SerializeField] string participantLabel="Participant number";
        [SerializeField] string blockLabel="Block number";
        [SerializeField] string continueButtonLabel="Continue";
        [SerializeField] string beginBlockButtonLabel="Begin block";
        [SerializeField] string cueC1Word="Pointing";
        [SerializeField] string cueC2Word="Moving";
        [SerializeField] string correctFeedbackText="Correct";
        [SerializeField] string incorrectFeedbackText="Incorrect";
        [SerializeField] string missedResponseText="missed";
        [SerializeField] string completedTitle="TESTING COMPLETED!";
        [SerializeField] string instructionC1Label="This color -> use its POINTING direction";
        [SerializeField] string instructionC2Label="This color -> use its MOVING direction";
        [SerializeField] string nfPathLabel="NF file path";
        [SerializeField] string resolvedNfPathLabel="Resolved NF path: ";
        [SerializeField, TextArea(5,12)] string instructionsText="INSTRUCTIONS\n\nTwo groups of leaves move around the screen. Each group points in one direction and moves in another (up / down / left / right), independently.\n\nAt first only the flickering outlines are visible. The centre box turns a color and shows Pointing or Moving. Concentrating on that group brightens the leaves. Hold them green to reveal their true colors.\n\nOnly then answer with an arrow key or a swipe. Earlier responses are ignored.";


        [Header("Session Defaults")]
        [SerializeField] string participant="000";
        [SerializeField] string block="000";

        AppState state=AppState.Setup;
        LeafVisualRenderer visuals;
        ITriggerSender triggers;
        INfReader nfReader;
        CsvLogger logger;
        System.Random rng;
        // BciCore calibration screen — instantiated/destroyed around AppState.Calibration
        CalibrationScreen _calibScreen;
        List<TrialDefinition> trials;
        TrialDefinition trial;
        List<LeafState> leaves;
        FieldChoice field;
        List<Vector2> inner1,outer1,inner2,outer2;
        SsvepPhaseSchedule phaseSchedule;

        string fatalMessage="";
        string nfConnectionStatus="";
        string participantResponse="missed",feedbackText="";
        bool accuracy,revealTimeout,responseTimeout,colorsRevealed,inGreen,inFeedback,colorOnsetCommitted,finishQueued,sessionStarting,nfStallWarned;
        int trialIndex,trialFrame,preCueFrames,cueOnsetFrame,revealDeadlineFrame,responseDeadlineFrame;
        int feedbackFrames,feedbackRemaining,itiRemaining,nfTraceEvery,nfGreenHoldFrames,greenHoldFrames;
        int previousPhaseSlot=-1,droppedCount;
        double refreshHz,ifi,trialRefreshHz,experimentT0,trialPhaseT0,trialStartTime,cueOnsetTime,firstGreenTime,colorOnsetTime,colorOnsetClock,reactionTime,nfLastFreshClock;
        float activePixelScale=1;
        double nfCurrent,nfLevel;
        readonly List<NfTraceRow> traceRows=new List<NfTraceRow>(256);
        readonly List<DroppedFrameRow> droppedRows=new List<DroppedFrameRow>(32);
        readonly List<bool> accuracyResults=new List<bool>(24);
        Vector2 touchStart;
        bool touchTracking;
        readonly List<RuntimeSetting> runtimeSettings=new List<RuntimeSetting>(128);
        Vector2 settingsScroll;
        string settingsValidationError="";
        bool runtimeInitialized;
        GUIStyle titleStyle,bodyStyle,centerStyle,cueStyle,buttonStyle,errorStyle;
        GUIStyle settingsHeaderStyle,settingsLabelStyle,settingsInputStyle,settingsTextAreaStyle,settingsHelpStyle;
        float stylesScale=-1;

        float LeafLength=>leafSizePx*activePixelScale;
        float LeafWidth=>leafSizePx*leafWidthMultiplier*activePixelScale;
        float Border=>leafSizePx*leafBorderMultiplier*activePixelScale;
        float OuterLength=>LeafLength+2*Border;
        float OuterWidth=>LeafWidth+2*Border;
        float LeafSpeed=>leafSpeedPxPerSec*activePixelScale;
        float LaneMargin=>laneClearanceMarginPx*activePixelScale;
        float MaxRadius=>0.5f*Mathf.Sqrt(OuterLength*OuterLength+OuterWidth*OuterWidth);
        double Elapsed=>MonotonicClock.Now-experimentT0;
        string NfPath
        {
            get
            {
                string chosen=string.IsNullOrWhiteSpace(nfFilePath)?"nf.txt":nfFilePath.Trim();
                return Path.IsPathRooted(chosen)?chosen:Path.Combine(Application.persistentDataPath,chosen);
            }
        }
        string NfDescription=>nfSourceType==NfSourceType.Tcp?$"tcp://{nfTcpHost.Trim()}:{nfTcpPort}":NfPath;
        string DataPath=>Path.IsPathRooted(dataFolderName)?dataFolderName:Path.Combine(Application.persistentDataPath,dataFolderName);

        void Start()
        {
            state=AppState.Setup;
            InitializeRuntimeSettings();
            InitializeAestheticPresentation();
        }

        void InitializeGameplayFromSettings()
        {
            ConfigurePresentation();
            activePixelScale=RequestedScreenScale();
            ResolveResponsiveField();
            visuals?.Dispose();
            visuals=new LeafVisualRenderer(transform,cueWidthPx*activePixelScale,cueHeightPx*activePixelScale,cueCornerRadiusPx*activePixelScale,
                cueCornerSegments,cueFontSize,cueCharacterSize*activePixelScale,Render(cueTextOutlineSrgb),cueTextOutlinePx*activePixelScale,Render(backgroundSrgb),leavesPerFlock,leafArcPoints+1);
            InitializeAestheticGameplayBackground();
            triggers?.Dispose();
            if(nfSourceType==NfSourceType.BciCore)
            {
                // BciCore: start the in-process TCP server and wire BciCoreTriggerSender (TCP + UDP fallback).
                BciServer.StartServer();
                triggers=new BciCoreTriggerSender(nfTcpHost.Trim(),triggerPort);
                triggers.Send("reset",resetTrigger);
            }
            else
            {
                triggers=new TriggerSender(nfTcpHost.Trim(),triggerPort);
                triggers.Send("reset",resetTrigger);
            }
            visuals.ClearLeaves();visuals.SetCueVisible(false,Color.clear);
            runtimeInitialized=true;
        }

        void ConfigurePresentation()
        {
            QualitySettings.vSyncCount=Mathf.Max(1,verticalSyncCount);
            OnDemandRendering.renderFrameInterval=1;
            Application.runInBackground=runInBackground;
            Screen.sleepTimeout=preventScreenSleep?SleepTimeout.NeverSleep:SleepTimeout.SystemSetting;
#if UNITY_ANDROID && !UNITY_EDITOR
            RequestAndroidDisplayRefreshRate();
#endif
            RefreshPresentationTiming();
        }

        void RequestAndroidDisplayRefreshRate()
        {
            if(requestedDisplayRefreshRateHz<=0)return;
            double requested=requestedDisplayRefreshRateHz;
            RefreshRate selected=new RefreshRate{numerator=(uint)Math.Max(1,Math.Round(requested*1000.0)),denominator=1000};
            if(requestClosestSupportedRefreshRate)
            {
                Resolution[] modes=Screen.resolutions;
                double bestError=double.PositiveInfinity;
                for(int i=0;i<modes.Length;i++)
                {
                    double hz=modes[i].refreshRateRatio.value;
                    if(hz<=0||double.IsNaN(hz)||double.IsInfinity(hz))continue;
                    double error=Math.Abs(hz-requested);
                    if(error<bestError){bestError=error;selected=modes[i].refreshRateRatio;}
                }
            }
            Screen.SetResolution(Screen.width,Screen.height,FullScreenMode.FullScreenWindow,selected);
        }

        void RefreshPresentationTiming()
        {
            double observed=Screen.currentResolution.refreshRateRatio.value;
            refreshHz=ValidRefresh(observed)?observed:fallbackRefreshHz;
            ifi=1.0/refreshHz;
            int target=targetFrameRateOverride>0?targetFrameRateOverride:Mathf.RoundToInt((float)refreshHz);
            Application.targetFrameRate=Math.Max(1,target);
        }

        bool ValidRefresh(double hz)=>hz>=minimumValidRefreshHz&&!double.IsNaN(hz)&&!double.IsInfinity(hz);

        bool TrialRefreshRateChanged()
        {
            if(!abortTrialOnRefreshRateChange)return false;
            double observed=Screen.currentResolution.refreshRateRatio.value;
            return ValidRefresh(observed)&&Math.Abs(observed-trialRefreshHz)>refreshRateChangeToleranceHz;
        }

        void AbortForRefreshRateChange()
        {
            finishQueued=true;
            triggers.Send("trialstop",trialStopTrigger);
            Fatal($"Display refresh rate changed during the trial (expected {trialRefreshHz:F3} Hz, observed {Screen.currentResolution.refreshRateRatio.value:F3} Hz). Trial aborted to protect SSVEP phase integrity.");
        }

        void OnDestroy()
        {
            if(state==AppState.Trial)triggers?.Send("trialstop",trialStopTrigger);
            if(_calibScreen!=null){Destroy(_calibScreen);_calibScreen=null;}
            if(nfSourceType==NfSourceType.BciCore)BciServer.StopServer();
            visuals?.Dispose();nfReader?.Dispose();triggers?.Dispose();DisposeAestheticPresentation();
        }

        void Update()
        {
            UpdateAestheticInput();
            if(escapeEndsBlock&&state!=AppState.Setup&&EscapePressed()){if(state==AppState.Trial){triggers?.Send("trialstop",trialStopTrigger);finishQueued=true;}state=AppState.Summary;visuals?.ClearLeaves();visuals?.SetCueVisible(false,Color.clear);return;}
            if(state==AppState.Trial&&!finishQueued)UpdateTrial();
            else if(state==AppState.Iti)UpdateIti();
        }

        public void ApplySettingsAndContinue()
        {
            if(sessionStarting)return;
            settingsValidationError="";
            if(!TryApplyRuntimeSettings(out settingsValidationError))return;
            try
            {
                stylesScale=-1;
                InitializeGameplayFromSettings();
                if(nfSourceType==NfSourceType.BciCore)
                {
                    // Enter calibration before proceeding to BeginSession.
                    // CalibrationScreen calls BeginSession() when the operator clicks Proceed.
                    state=AppState.Calibration;
                    if(_calibScreen==null)
                    {
                        _calibScreen=gameObject.AddComponent<CalibrationScreen>();
                        _calibScreen.OnProceedPressed=()=>{
                            if(_calibScreen!=null){Destroy(_calibScreen);_calibScreen=null;}
                            BeginSession();
                        };
                        _calibScreen.OnBackPressed=()=>{
                            if(_calibScreen!=null){Destroy(_calibScreen);_calibScreen=null;}
                            BciServer.ExitCalibrationMode();
                            state=AppState.Setup;
                        };
                    }
                }
                else
                {
                    BeginSession();
                }
            }
            catch(Exception e)
            {
                runtimeInitialized=false;
                settingsValidationError=e.Message;
                Debug.LogError(e);
            }
        }

        public void BeginSession()
        {
            if(sessionStarting)return;
            if(!runtimeInitialized){settingsValidationError="Apply the settings before continuing.";return;}
            try
            {
                experimentT0=MonotonicClock.Now;
                int seed=useFixedRandomSeed?fixedRandomSeed:unchecked(Environment.TickCount*397);
                if(mixParticipantAndBlockIntoSeed)seed=unchecked(seed^participant.GetHashCode()^block.GetHashCode());
                rng=new System.Random(seed);
                nfReader?.Dispose();nfReader=null;
                nfConnectionStatus="";nfStallWarned=false;
                if(nfSourceType==NfSourceType.Tcp)
                {
                    var tcpReader=new NfTcpReader(nfTcpHost,nfTcpPort);nfReader=tcpReader;sessionStarting=true;
                    nfConnectionStatus="Connecting to "+tcpReader.Description+" ...";
                    StartCoroutine(WaitForTcpAndCompleteSession(tcpReader));
                }
                else if(nfSourceType==NfSourceType.BciCore)
                {
                    // BciCoreNfReader reads smi_14gt18_shaped / smi_18gt14_shaped
                    // directly from BciServer.OnNfSample — no TCP connection needed here.
                    nfReader=new BciCoreNfReader();
                    nfConnectionStatus="Receiving neurofeedback in-process (BciCore)";
                    // Initialise the stall clock so the warning doesn't fire immediately
                    // (mirrors what WaitForTcpAndCompleteSession does for Tcp mode).
                    nfLastFreshClock=MonotonicClock.Now;
                    CompleteSessionStart();
                }
                else
                {
                    nfReader=new NfBinaryReader(NfPath);
                    CompleteSessionStart();
                }
            }
            catch(Exception e){Fatal(e.Message);}
        }

        IEnumerator WaitForTcpAndCompleteSession(NfTcpReader tcpReader)
        {
            double deadline=MonotonicClock.Now+Math.Max(0.1,nfTcpTimeoutSec);
            while(state==AppState.Setup&&!tcpReader.IsReady&&string.IsNullOrEmpty(tcpReader.Error)&&MonotonicClock.Now<deadline)yield return null;
            sessionStarting=false;
            if(state!=AppState.Setup)yield break;
            if(!tcpReader.IsReady)
            {
                string reason=string.IsNullOrEmpty(tcpReader.Error)?$"timed out after {nfTcpTimeoutSec:F1} seconds":tcpReader.Error;
                tcpReader.Dispose();if(ReferenceEquals(nfReader,tcpReader))nfReader=null;
                Fatal($"Could not receive neurofeedback from {tcpReader.Description}: {reason}");yield break;
            }
            nfConnectionStatus="Receiving neurofeedback from "+tcpReader.Description;
            nfLastFreshClock=MonotonicClock.Now;
            try{CompleteSessionStart();}catch(Exception e){Fatal(e.Message);}
        }

        void CompleteSessionStart()
        {
            logger=new CsvLogger(DataPath,participant,block);
            trials=TrialPlanner.Build(trialsPerBlock,cueC1Probability,rng);accuracyResults.Clear();
            if(accuracyResults.Capacity<trialsPerBlock)accuracyResults.Capacity=trialsPerBlock;
            ResolveResponsiveField();ValidateLayouts();
            state=AppState.Instructions;
            visuals.RenderInstructionDemo(Render(c1Srgb),Render(c2Srgb),Render(instructionBorderSrgb),StimulusViewport(),LeafLength,LeafWidth,OuterLength,OuterWidth,leafArcPoints);
        }

        float RequestedScreenScale()
        {
            if(!useResponsiveSizing)return 1f;
            float shortSide=Mathf.Max(1,Mathf.Min(Screen.width,Screen.height));
            return Mathf.Clamp(shortSide/Mathf.Max(1,referenceShortSidePx),minimumScreenScale,maximumScreenScale);
        }

        Rect SafeAreaTopLeft()
        {
            Rect s=Screen.safeArea;
            return new Rect(s.x,Screen.height-s.yMax,s.width,s.height);
        }

        Rect StimulusViewport()
        {
            Rect safe=SafeAreaTopLeft();
            float pad=safeAreaPaddingReferencePx*activePixelScale;
            float width=Mathf.Max(1,(safe.width-2*pad)*stimulusWidthFraction);
            float height=Mathf.Max(1,(safe.height-2*pad)*stimulusHeightFraction);
            return new Rect(safe.center.x-width*0.5f,safe.center.y-height*0.5f,width,height);
        }

        void ResolveResponsiveField()
        {
            float requested=RequestedScreenScale();
            float minimum=Mathf.Max(minimumScreenScale,requested*autoFitMinimumFraction);
            string lastMessage="No responsive field candidate was evaluated.";
            for(float scale=requested;scale>=minimum-0.0001f;scale-=Mathf.Max(0.01f,requested*autoFitStepFraction))
            {
                activePixelScale=Mathf.Max(minimum,scale);
                Rect viewport=StimulusViewport();
                int required=Mathf.CeilToInt(2*(OuterLength+OuterWidth+2*LaneMargin))+1;
                int shrink=Mathf.RoundToInt(maxFieldShrinkPx*activePixelScale);
                FieldChoice candidate=LeafLaneLayout.ChooseField(Mathf.FloorToInt(viewport.width),Mathf.FloorToInt(viewport.height),
                    required,shrink,minRepeatsPerAxis);
                if(!candidate.feasible){lastMessage=candidate.message;continue;}
                candidate.rect.position+=viewport.position;
                field=candidate;
                if(!AllLayoutsFit(out lastMessage))continue;
                return;
            }
            throw new InvalidOperationException("Display auto-fit failed: "+lastMessage);
        }

        bool AllLayoutsFit(out string message)
        {
            var checkRng=new System.Random(918273);
            var combos=TrialPlanner.Build(24,0.5,checkRng);
            float speed=(float)(LeafSpeed*ifi);
            foreach(var t in combos)
            {
                var layout=LeafLaneLayout.Create(leavesPerFlock,field.rect,OuterLength,OuterWidth,t.c1Point,t.c1Move,t.c2Point,t.c2Move,
                    speed,LaneMargin,checkRng);
                if(!layout.feasible){message=layout.message;return false;}
            }
            message="ok";return true;
        }

        void ValidateLayouts()
        {
            if(!verifyLeafPathsAtSetup)return;
            var combos=TrialPlanner.Build(24,0.5,new System.Random(918273));
            float speed=(float)(LeafSpeed*ifi),worst=float.PositiveInfinity;
            foreach(var t in combos)
            {
                var layout=LeafLaneLayout.Create(leavesPerFlock,field.rect,OuterLength,OuterWidth,t.c1Point,t.c1Move,t.c2Point,t.c2Move,speed,LaneMargin,rng);
                if(!layout.feasible)throw new InvalidOperationException(layout.message);
                Vector2 e1=LeafLaneLayout.Extents(t.c1Point,OuterLength,OuterWidth),e2=LeafLaneLayout.Extents(t.c2Point,OuterLength,OuterWidth);
                if(!LeafLaneLayout.Verify(layout.leaves,field.rect,e1,e2,verificationMaxFrames,out float clearance))
                    throw new InvalidOperationException("Fixed leaf paths overlap; minimum clearance "+clearance.ToString("F1")+"px.");
                worst=Math.Min(worst,clearance);
            }
            Debug.Log($"Leaf paths verified for all 24 direction combinations; worst clearance {worst:F1}px. Field: {field.message}");
        }

        public void BeginBlock()
        {
            if(state!=AppState.Instructions)return;
            trialIndex=0;StartTrial();
        }

        void StartTrial()
        {
            if(trialIndex>=trials.Count){ShowSummary();return;}
            RefreshPresentationTiming();
            trialRefreshHz=refreshHz;
            trial=trials[trialIndex];trialFrame=0;finishQueued=false;colorsRevealed=false;colorOnsetCommitted=false;
            inGreen=false;inFeedback=false;accuracy=false;revealTimeout=false;responseTimeout=false;
            participantResponse=missedResponseText;feedbackText="";nfCurrent=0;nfLevel=0;greenHoldFrames=0;previousPhaseSlot=-1;droppedCount=0;
            cueOnsetTime=firstGreenTime=colorOnsetTime=reactionTime=double.NaN;
            traceRows.Clear();droppedRows.Clear();
            preCueFrames=Math.Max(1,Mathf.RoundToInt((float)((PreCueConstantSec+TrialPlanner.TruncatedExponential(PreCueExpMeanSec,PreCueExpMaxSec,rng))/ifi)));
            cueOnsetFrame=preCueFrames+1;revealDeadlineFrame=preCueFrames+Math.Max(1,Mathf.RoundToInt((float)(NfRevealTimeoutSec/ifi)));
            responseDeadlineFrame=int.MaxValue;feedbackFrames=Math.Max(1,Mathf.RoundToInt((float)(FeedbackSec/ifi)));
            nfTraceEvery=Math.Max(1,Mathf.RoundToInt((float)(NfTraceIntervalSec/ifi)));
            nfGreenHoldFrames=Math.Max(1,Mathf.RoundToInt((float)(NfGreenHoldSec/ifi)));
            var layout=LeafLaneLayout.Create(leavesPerFlock,field.rect,OuterLength,OuterWidth,trial.c1Point,trial.c1Move,trial.c2Point,trial.c2Move,
                (float)(LeafSpeed*ifi),LaneMargin,rng);
            if(!layout.feasible){Fatal(layout.message);return;}leaves=layout.leaves;
            inner1=LeafVisualRenderer.CreateLeafShape(trial.c1Point,LeafLength,LeafWidth,leafArcPoints);outer1=LeafVisualRenderer.CreateLeafShape(trial.c1Point,OuterLength,OuterWidth,leafArcPoints);
            inner2=LeafVisualRenderer.CreateLeafShape(trial.c2Point,LeafLength,LeafWidth,leafArcPoints);outer2=LeafVisualRenderer.CreateLeafShape(trial.c2Point,OuterLength,OuterWidth,leafArcPoints);
            double worstCaseHorizon=PreCueConstantSec+PreCueExpMaxSec+NfRevealTimeoutSec+ResponseTimeoutSec+FeedbackSec+phaseHorizonSafetyMarginSec;
            double actualHorizon=Math.Max(precomputedPhaseHorizonSec,worstCaseHorizon);
            phaseSchedule=new SsvepPhaseSchedule(refreshHz,actualHorizon,frequencyC1Hz,frequencyC2Hz,
                ssvepMeanLuminance,ssvepModulationDepth,phaseC1Degrees,phaseC2Degrees);
            int traceCapacity=Math.Max(4,Mathf.CeilToInt((float)(actualHorizon/Math.Max(0.001,NfTraceIntervalSec)))+4);
            if(traceRows.Capacity<traceCapacity)traceRows.Capacity=traceCapacity;
            if(droppedRows.Capacity<phaseSchedule.Count)droppedRows.Capacity=phaseSchedule.Count;
            trialPhaseT0=MonotonicClock.Now;trialStartTime=Elapsed;state=AppState.Trial;
            triggers.Send("trialstart",trialStartTrigger);
        }

        void UpdateTrial()
        {
            if(TrialRefreshRateChanged()){AbortForRefreshRateChange();return;}
            trialFrame++;bool preCue=trialFrame<cueOnsetFrame;
            bool readOk=nfReader.TryRead(trial.NfIndex,out double fresh);
            if(readOk){nfCurrent=fresh;nfLastFreshClock=MonotonicClock.Now;}
            else if((nfSourceType==NfSourceType.Tcp||nfSourceType==NfSourceType.BciCore)&&!nfStallWarned&&nfStreamStallWarnSec>0&&MonotonicClock.Now-nfLastFreshClock>=nfStreamStallWarnSec)
            {
                nfStallWarned=true;
                string extra=nfSourceType==NfSourceType.BciCore?" Board may not be sending NF frames (check firmware/DSP pipeline).":"";
                Debug.LogWarning($"No fresh NF sample for {MonotonicClock.Now-nfLastFreshClock:F1} s during trial {trialIndex+1}. Source: {NfDescription}.{extra} The trial will continue using the last value.");
            }
            double clipped=Math.Max(-NfClip,Math.Min(NfClip,nfCurrent));
            if(!preCue)nfLevel=Math.Max(0,Math.Min(1,nfLevel+NfRatePerUnitPerSec*ifi*clipped));
            inGreen=!preCue&&nfLevel>=1-NfWhiteTopRelaxation;
            bool firstGreenNow=inGreen&&double.IsNaN(firstGreenTime);
            bool revealNow=false;
            if(!colorsRevealed)
            {
                if(inGreen){greenHoldFrames++;if(greenHoldFrames>=nfGreenHoldFrames){colorsRevealed=true;revealNow=true;responseDeadlineFrame=trialFrame+Math.Max(1,Mathf.RoundToInt((float)(ResponseTimeoutSec/ifi)));}}
                else greenHoldFrames=0;
            }
            LeafLaneLayout.Advance(leaves,field.rect);
            double predicted=(MonotonicClock.Now-trialPhaseT0)+presentationLookAheadFrames*ifi;
            if(!phaseSchedule.TrySampleForPresentation(predicted,out int slot,out float lum1,out float lum2)){Fatal("Precomputed SSVEP phase horizon exhausted.");return;}
            if(previousPhaseSlot>=0&&slot>previousPhaseSlot+1)
            {
                int skipped=slot-previousPhaseSlot-1;droppedCount+=skipped;
                droppedRows.Add(new DroppedFrameRow{frame=trialFrame,vblTime=Elapsed,missedBy=skipped*ifi});
            }
            previousPhaseSlot=slot;
            Color fill1,fill2;
            if(preCue){fill1=fill2=Render(preCueLeafFillSrgb);}
            else if(colorsRevealed){fill1=Render(c1Srgb);fill2=Render(c2Srgb);}
            else if(inGreen){fill1=fill2=Render(greenZoneSrgb);}
            else{fill1=fill2=RenderGray((float)nfLevel);}
            visuals.RenderLeaves(leaves,field.rect,MaxRadius,inner1,outer1,inner2,outer2,fill1,fill2,RenderGray(lum1),RenderGray(lum2));
            visuals.SetCueVisible(true,preCue?Render(cuePreSrgb):Render(trial.cue==CueKind.C1?c1Srgb:c2Srgb));
            visuals.SetCueText(preCue?"":(inFeedback?feedbackText:(trial.cue==CueKind.C1?cueC1Word:cueC2Word)),inFeedback?(accuracy?Render(successSrgb):Render(failureSrgb)):Render(cueTextSrgb));

            if(trialFrame==cueOnsetFrame)StartCoroutine(CommitPresented("cue"));
            if(firstGreenNow)StartCoroutine(CommitPresented("green"));
            if(revealNow)StartCoroutine(CommitPresented("reveal"));
            if((trialFrame-1)%nfTraceEvery==0)traceRows.Add(new NfTraceRow{frame=trialFrame,sampleTime=Elapsed,raw=nfCurrent,clipped=clipped,
                level=nfLevel,green=inGreen,greenHold=greenHoldFrames*ifi,revealed=colorsRevealed,postCue=!preCue,readOk=readOk});

            if(inFeedback)
            {
                feedbackRemaining--;if(feedbackRemaining<=0)QueueFinish();
            }
            else if(colorsRevealed&&colorOnsetCommitted)
            {
                if(TryGetDirection(out Direction4 response))
                {
                    participantResponse=DirectionUtil.Text(response);reactionTime=MonotonicClock.Now-colorOnsetClock;accuracy=response==trial.CorrectResponse;
                    triggers.Send("response",responseTrigger);feedbackText=accuracy?correctFeedbackText:incorrectFeedbackText;inFeedback=true;feedbackRemaining=feedbackFrames;
                }
                else if(trialFrame>=responseDeadlineFrame){responseTimeout=true;QueueFinish();}
            }
            else if(!preCue&&!colorsRevealed&&trialFrame>=revealDeadlineFrame){revealTimeout=true;QueueFinish();}
        }

        IEnumerator CommitPresented(string which)
        {
            int index=trialIndex;yield return new WaitForEndOfFrame();
            if(state!=AppState.Trial||trialIndex!=index)yield break;
            if(which=="cue"){cueOnsetTime=Elapsed;triggers.Send("cueonset",cueOnsetTrigger);}
            else if(which=="green"){if(double.IsNaN(firstGreenTime))firstGreenTime=Elapsed;}
            else{colorOnsetClock=MonotonicClock.Now;colorOnsetTime=Elapsed;colorOnsetCommitted=true;triggers.Send("success",successTrigger);}
        }

        void QueueFinish(){if(finishQueued)return;finishQueued=true;StartCoroutine(FinishAfterPresentation());}
        IEnumerator FinishAfterPresentation()
        {
            yield return new WaitForEndOfFrame();triggers.Send("trialstop",trialStopTrigger);double end=Elapsed;
            logger.WriteTrial(trialIndex+1,trialStartTime,trial,cueOnsetTime,firstGreenTime,colorOnsetTime,participantResponse,accuracy,reactionTime,revealTimeout,responseTimeout,end,droppedCount);
            logger.WriteTrace(trialIndex+1,trial.NfIndex+1,traceRows);logger.WriteDropped(trialIndex+1,droppedRows);accuracyResults.Add(accuracy);
            visuals.ClearLeaves();visuals.SetCueVisible(true,Render(cuePreSrgb));state=AppState.Iti;
            itiRemaining=Math.Max(1,Mathf.RoundToInt((float)(ItiSec/ifi)));trialIndex++;
        }

        void UpdateIti(){itiRemaining--;if(itiRemaining<=0)StartTrial();}
        void ShowSummary(){state=AppState.Summary;visuals.ClearLeaves();visuals.SetCueVisible(false,Color.clear);}
        void Fatal(string message){fatalMessage=message;state=AppState.Fatal;visuals?.ClearLeaves();visuals?.SetCueVisible(false,Color.clear);Debug.LogError(message);}

        bool TryGetDirection(out Direction4 d)
        {
            var k=Keyboard.current;
            if(enableKeyboardArrows&&k!=null)
            {
                if(k.upArrowKey.wasPressedThisFrame){d=Direction4.Up;return true;}
                if(k.downArrowKey.wasPressedThisFrame){d=Direction4.Down;return true;}
                if(k.leftArrowKey.wasPressedThisFrame){d=Direction4.Left;return true;}
                if(k.rightArrowKey.wasPressedThisFrame){d=Direction4.Right;return true;}
            }
            var touch=Touchscreen.current?.primaryTouch;
            if(enableSwipeResponses&&touch!=null)
            {
                if(touch.press.wasPressedThisFrame){touchStart=touch.position.ReadValue();touchTracking=true;}
                if(touchTracking&&touch.press.wasReleasedThisFrame)
                {
                    touchTracking=false;Vector2 delta=touch.position.ReadValue()-touchStart;
                    if(delta.magnitude>=swipeThresholdPx*activePixelScale){if(Math.Abs(delta.x)>Math.Abs(delta.y))d=delta.x<0?Direction4.Left:Direction4.Right;else d=delta.y<0?Direction4.Down:Direction4.Up;return true;}
                }
            }
            d=default;return false;
        }
        bool EscapePressed()=>Keyboard.current!=null&&Keyboard.current.escapeKey.wasPressedThisFrame;
        Color Render(Color srgb)=>QualitySettings.activeColorSpace==ColorSpace.Linear?srgb.linear:srgb;
        Color RenderGray(float v){var c=new Color(v,v,v,1);return QualitySettings.activeColorSpace==ColorSpace.Linear?c.linear:c;}

        float UiScale()
        {
            return Mathf.Clamp(Mathf.Min(Screen.width,Screen.height)/Mathf.Max(1,uiReferenceShortSidePx),minimumUiScale,maximumUiScale);
        }

        void InitStyles()
        {
            float u=UiScale();
            if(titleStyle!=null&&Mathf.Abs(stylesScale-u)<0.001f)return;
            stylesScale=u;
            titleStyle=new GUIStyle(GUI.skin.label){fontSize=Mathf.RoundToInt(titleFontSize*u),alignment=TextAnchor.MiddleCenter,normal={textColor=Color.white}};
            bodyStyle=new GUIStyle(GUI.skin.label){fontSize=Mathf.RoundToInt(bodyFontSize*u),alignment=TextAnchor.MiddleCenter,wordWrap=true,normal={textColor=Color.white}};
            centerStyle=new GUIStyle(GUI.skin.label){fontSize=Mathf.RoundToInt(centerFontSize*u),alignment=TextAnchor.MiddleCenter,wordWrap=true,normal={textColor=Color.white}};
            cueStyle=new GUIStyle(GUI.skin.label){fontSize=Mathf.RoundToInt(cueFontSize*u),alignment=TextAnchor.MiddleCenter,normal={textColor=Color.black}};
            buttonStyle=new GUIStyle(GUI.skin.button){fontSize=Mathf.RoundToInt(buttonFontSize*u)};
            errorStyle=new GUIStyle(bodyStyle);errorStyle.normal.textColor=Render(failureSrgb);
            settingsHeaderStyle=new GUIStyle(titleStyle){fontSize=Mathf.RoundToInt(Mathf.Max(bodyFontSize+4,buttonFontSize)*u),alignment=TextAnchor.MiddleLeft};
            settingsLabelStyle=new GUIStyle(bodyStyle){alignment=TextAnchor.MiddleLeft};
            int inputFont=Mathf.RoundToInt(Mathf.Max(30,bodyFontSize+6)*u);
            settingsInputStyle=new GUIStyle(GUI.skin.textField){fontSize=inputFont,alignment=TextAnchor.MiddleLeft};
            settingsTextAreaStyle=new GUIStyle(settingsInputStyle){wordWrap=true,alignment=TextAnchor.UpperLeft};
            settingsHelpStyle=new GUIStyle(centerStyle){fontSize=Mathf.RoundToInt(Mathf.Max(18,centerFontSize)*u)};
        }

        void InitializeRuntimeSettings()
        {
            runtimeSettings.Clear();
            FieldInfo[] fields=GetType().GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
            Array.Sort(fields,(a,b)=>a.MetadataToken.CompareTo(b.MetadataToken));
            foreach(FieldInfo fieldInfo in fields)
            {
                if(fieldInfo.IsStatic||fieldInfo.IsNotSerialized||Attribute.IsDefined(fieldInfo,typeof(HideInInspector)))continue;
                bool serialized=fieldInfo.IsPublic||Attribute.IsDefined(fieldInfo,typeof(SerializeField));
                if(!serialized||!SupportsRuntimeSetting(fieldInfo.FieldType))continue;
                var header=fieldInfo.GetCustomAttribute<HeaderAttribute>();
                object value=fieldInfo.GetValue(this);
                runtimeSettings.Add(new RuntimeSetting{
                    field=fieldInfo,
                    header=header==null?"":header.header,
                    label=Nicify(fieldInfo.Name),
                    values=FormatRuntimeValue(fieldInfo.FieldType,value),
                    multiline=Attribute.IsDefined(fieldInfo,typeof(TextAreaAttribute))
                });
            }
        }

        static bool SupportsRuntimeSetting(Type type)
        {
            return type==typeof(string)||type==typeof(bool)||type==typeof(int)||type==typeof(float)||type==typeof(double)||type==typeof(Color)||type.IsEnum;
        }

        static string[] FormatRuntimeValue(Type type,object value)
        {
            if(type==typeof(Color))
            {
                Color c=(Color)value;
                return new[]{Invariant(c.r),Invariant(c.g),Invariant(c.b),Invariant(c.a)};
            }
            if(type==typeof(float))return new[]{Invariant((float)value)};
            if(type==typeof(double))return new[]{Invariant((double)value)};
            if(type==typeof(int))return new[]{((int)value).ToString(CultureInfo.InvariantCulture)};
            if(type==typeof(bool))return new[]{((bool)value)?"True":"False"};
            return new[]{value==null?"":value.ToString()};
        }

        static string Invariant(float value)=>value.ToString("R",CultureInfo.InvariantCulture);
        static string Invariant(double value)=>value.ToString("R",CultureInfo.InvariantCulture);

        static string Nicify(string value)
        {
            if(string.IsNullOrEmpty(value))return "";
            var result=new StringBuilder(value.Length+12);
            for(int i=0;i<value.Length;i++)
            {
                char c=value[i];
                if(c=='_'){result.Append(' ');continue;}
                if(i>0&&char.IsUpper(c)&&(char.IsLower(value[i-1])||char.IsDigit(value[i-1])))result.Append(' ');
                result.Append(i==0?char.ToUpperInvariant(c):c);
            }
            return result.ToString();
        }

        bool TryApplyRuntimeSettings(out string error)
        {
            var parsed=new List<object>(runtimeSettings.Count);
            foreach(RuntimeSetting setting in runtimeSettings)
            {
                if(!TryParseRuntimeValue(setting,out object value,out error))return false;
                parsed.Add(value);
            }
            for(int i=0;i<runtimeSettings.Count;i++)runtimeSettings[i].field.SetValue(this,parsed[i]);
            error="";
            return true;
        }

        bool TryParseRuntimeValue(RuntimeSetting setting,out object value,out string error)
        {
            Type type=setting.field.FieldType;
            string raw=setting.values.Length==0?"":setting.values[0];
            error="";
            value=null;
            if(type==typeof(string)){value=raw??"";return true;}
            if(type==typeof(bool))
            {
                if(bool.TryParse(raw,out bool parsedBool)){value=parsedBool;return true;}
                error=setting.label+" must be On or Off.";return false;
            }
            if(type.IsEnum)
            {
                try{value=Enum.Parse(type,raw,true);return true;}
                catch{error=setting.label+" has an invalid selection.";return false;}
            }
            if(type==typeof(Color))
            {
                if(setting.values.Length!=4){error=setting.label+" must contain R, G, B and A values.";return false;}
                var components=new float[4];
                for(int i=0;i<4;i++)
                {
                    if(!float.TryParse(setting.values[i],NumberStyles.Float,CultureInfo.InvariantCulture,out components[i])||float.IsNaN(components[i])||float.IsInfinity(components[i]))
                    {error=$"{setting.label} {"RGBA"[i]} must be a number.";return false;}
                    components[i]=Mathf.Clamp01(components[i]);
                }
                value=new Color(components[0],components[1],components[2],components[3]);return true;
            }
            if(!double.TryParse(raw,NumberStyles.Float,CultureInfo.InvariantCulture,out double number)||double.IsNaN(number)||double.IsInfinity(number))
            {error=setting.label+" must be a number.";return false;}
            var min=setting.field.GetCustomAttribute<MinAttribute>();
            var range=setting.field.GetCustomAttribute<RangeAttribute>();
            if(min!=null)number=Math.Max(min.min,number);
            if(range!=null)number=Math.Max(range.min,Math.Min(range.max,number));
            if(type==typeof(int))
            {
                if(number<int.MinValue||number>int.MaxValue){error=setting.label+" is outside the valid integer range.";return false;}
                value=(int)Math.Round(number);return true;
            }
            if(type==typeof(float)){value=(float)number;return true;}
            value=number;return true;
        }

        float SettingsContentHeight(float u)
        {
            float height=12*u;
            foreach(RuntimeSetting setting in runtimeSettings)
            {
                if(!string.IsNullOrEmpty(setting.header))height+=44*u;
                height+=(setting.multiline?150:54)*u;
            }
            return height+12*u;
        }

        void DrawRuntimeSetting(RuntimeSetting setting,float x,float y,float width,float u)
        {
            float rowH=(setting.multiline?142:46)*u;
            float gap=12*u;
            float labelW=Mathf.Clamp(width*0.42f,220*u,520*u);
            float inputX=x+labelW+gap;
            float inputW=Mathf.Max(100*u,width-labelW-gap);
            GUI.Label(new Rect(x,y,labelW,rowH),setting.label,settingsLabelStyle);
            Type type=setting.field.FieldType;
            if(type==typeof(bool))
            {
                bool current=string.Equals(setting.values[0],"True",StringComparison.OrdinalIgnoreCase);
                bool next=GUI.Toggle(new Rect(inputX,y,inputW,rowH),current,current?"On":"Off",buttonStyle);
                setting.values[0]=next?"True":"False";
            }
            else if(type.IsEnum)
            {
                if(GUI.Button(new Rect(inputX,y,inputW,rowH),setting.values[0],buttonStyle))
                {
                    string[] names=Enum.GetNames(type);
                    int index=Array.IndexOf(names,setting.values[0]);
                    setting.values[0]=names[(Math.Max(0,index)+1)%names.Length];
                }
            }
            else if(type==typeof(Color))
            {
                float componentGap=5*u;
                float componentW=(inputW-3*componentGap)/4f;
                for(int i=0;i<4;i++)setting.values[i]=GUI.TextField(new Rect(inputX+i*(componentW+componentGap),y,componentW,rowH),setting.values[i],settingsInputStyle);
            }
            else if(setting.multiline)
                setting.values[0]=GUI.TextArea(new Rect(inputX,y,inputW,rowH),setting.values[0],settingsTextAreaStyle);
            else
                setting.values[0]=GUI.TextField(new Rect(inputX,y,inputW,rowH),setting.values[0],settingsInputStyle);
        }

        void OnGUI(){DrawAestheticGui();}

    }
}



