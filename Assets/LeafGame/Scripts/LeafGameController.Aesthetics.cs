using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LeafGame
{
    public sealed partial class LeafGameController
    {
        [Header("Aesthetic Presentation")]
        [SerializeField] string backgroundTextureResource="LeafGame/BackgroundTexture";
        [SerializeField, Min(0)] float cueBottomMarginPx=24;
        [SerializeField] Color panelSrgb=new Color32(10,30,42,224);
        [SerializeField] Color softPanelSrgb=new Color32(238,243,236,232);
        [SerializeField] Color accentSrgb=new Color32(54,158,96,255);
        [SerializeField] Color accentHoverSrgb=new Color32(67,181,112,255);
        [SerializeField] Color secondaryButtonSrgb=new Color32(52,76,89,245);
        [SerializeField] string welcomeSubtitle="Follow the leaves. Find your focus. Trust your direction.";
        [SerializeField] string playButtonLabel="Play";
        [SerializeField] string settingsButtonLabel="Settings";
        [SerializeField] string backButtonLabel="Back";
        [SerializeField] string timeBoardLabel="TIME";
        [SerializeField] string scoreBoardLabel="SCORE";

        bool settingsVisible;
        Texture2D backgroundTexture;
        Rect settingsScrollViewport;
        float settingsScrollExtent;
        bool settingsTouchDragging;
        Vector2 settingsLastTouch;
        GameObject aestheticBackgroundObject;
        Sprite aestheticBackgroundSprite;
        GUIStyle aestheticPanelStyle,softPanelStyle,primaryButtonStyle,secondaryButtonStyle,aestheticInputStyle,hudLabelStyle,hudValueStyle;
        Texture2D panelTexture,softPanelTexture,primaryTexture,primaryHoverTexture,secondaryTexture,inputTexture;
        float aestheticStyleScale=-1;
        int hudCachedSecond=-1,hudCachedResultCount=-1;
        string hudTimeValue="00:00",hudScoreValue="0 / 0";

        void InitializeAestheticPresentation()
        {
            settingsVisible=false;
            backgroundTexture=Resources.Load<Texture2D>(backgroundTextureResource);
        }

        void InitializeAestheticGameplayBackground()
        {
            DisposeGameplayBackground();
            if(backgroundTexture==null)backgroundTexture=Resources.Load<Texture2D>(backgroundTextureResource);
            if(backgroundTexture!=null)
            {
                aestheticBackgroundObject=new GameObject("AestheticBackground");
                aestheticBackgroundObject.transform.SetParent(transform,false);
                aestheticBackgroundSprite=Sprite.Create(backgroundTexture,new Rect(0,0,backgroundTexture.width,backgroundTexture.height),new Vector2(0.5f,0.5f),1f,0,SpriteMeshType.FullRect);
                aestheticBackgroundSprite.name="LeafGameBackgroundSprite";
                var renderer=aestheticBackgroundObject.AddComponent<SpriteRenderer>();
                renderer.sprite=aestheticBackgroundSprite;
                renderer.sortingOrder=-100;
                aestheticBackgroundObject.transform.localPosition=new Vector3(0,0,80);
                UpdateAestheticGeometry();
            }
            PositionCueAtBottom();
        }

        void UpdateAestheticInput()
        {
            UpdateAestheticGeometry();
            if(state!=AppState.Setup||!settingsVisible)return;
            var touch=Touchscreen.current?.primaryTouch;
            if(touch==null)return;
            Vector2 point=touch.position.ReadValue();
            point.y=Screen.height-point.y;
            if(touch.press.wasPressedThisFrame&&settingsScrollViewport.Contains(point))
            {
                settingsTouchDragging=true;
                settingsLastTouch=point;
            }
            if(settingsTouchDragging&&touch.press.isPressed)
            {
                Vector2 delta=point-settingsLastTouch;
                if(Mathf.Abs(delta.y)>0.1f)
                {
                    float maximum=Mathf.Max(0,settingsScrollExtent-settingsScrollViewport.height);
                    settingsScroll.y=Mathf.Clamp(settingsScroll.y-delta.y,0,maximum);
                    settingsLastTouch=point;
                }
            }
            if(touch.press.wasReleasedThisFrame)settingsTouchDragging=false;
        }

        void UpdateAestheticGeometry()
        {
            if(aestheticBackgroundObject!=null&&backgroundTexture!=null)
                aestheticBackgroundObject.transform.localScale=new Vector3((float)Screen.width/backgroundTexture.width,(float)Screen.height/backgroundTexture.height,1);
            PositionCueAtBottom();
        }

        void PositionCueAtBottom()
        {
            if(!runtimeInitialized)return;
            float scaledHeight=cueHeightPx*activePixelScale;
            float bottom=Screen.safeArea.y+cueBottomMarginPx*activePixelScale;
            Transform cueBox=transform.Find("StimulusRenderer/CueBox");
            if(cueBox!=null)cueBox.localPosition=new Vector3(0,bottom+scaledHeight*0.5f-Screen.height*0.5f,0);
            Transform cueTextTransform=transform.Find("StimulusRenderer/CueCanvas/CueText");
            var cueRect=cueTextTransform as RectTransform;
            if(cueRect!=null)
            {
                cueRect.anchorMin=cueRect.anchorMax=new Vector2(0.5f,0);
                cueRect.pivot=new Vector2(0.5f,0.5f);
                cueRect.anchoredPosition=new Vector2(0,bottom+scaledHeight*0.5f);
            }
        }

        void DisposeGameplayBackground()
        {
            if(aestheticBackgroundObject!=null)Destroy(aestheticBackgroundObject);
            if(aestheticBackgroundSprite!=null)Destroy(aestheticBackgroundSprite);
            aestheticBackgroundObject=null;
            aestheticBackgroundSprite=null;
        }

        void DisposeAestheticPresentation()
        {
            DisposeGameplayBackground();
            DestroyTexture(ref panelTexture);DestroyTexture(ref softPanelTexture);DestroyTexture(ref primaryTexture);
            DestroyTexture(ref primaryHoverTexture);DestroyTexture(ref secondaryTexture);DestroyTexture(ref inputTexture);
        }

        static void DestroyTexture(ref Texture2D texture)
        {
            if(texture!=null)Destroy(texture);
            texture=null;
        }

        void EnsureAestheticStyles()
        {
            float u=UiScale();
            if(aestheticPanelStyle!=null&&Mathf.Abs(aestheticStyleScale-u)<0.001f)return;
            aestheticStyleScale=u;
            if(panelTexture==null)
            {
                panelTexture=CreateRoundedTexture(64,15,Render(panelSrgb));
                softPanelTexture=CreateRoundedTexture(64,15,Render(softPanelSrgb));
                primaryTexture=CreateRoundedTexture(64,15,Render(accentSrgb));
                primaryHoverTexture=CreateRoundedTexture(64,15,Render(accentHoverSrgb));
                secondaryTexture=CreateRoundedTexture(64,15,Render(secondaryButtonSrgb));
                inputTexture=CreateRoundedTexture(64,12,new Color(0.96f,0.98f,0.96f,0.98f));
            }
            aestheticPanelStyle=new GUIStyle(GUI.skin.box){normal={background=panelTexture},border=new RectOffset(18,18,18,18),padding=new RectOffset(22,22,18,18)};
            softPanelStyle=new GUIStyle(aestheticPanelStyle){normal={background=softPanelTexture}};
            primaryButtonStyle=new GUIStyle(buttonStyle){normal={background=primaryTexture,textColor=Color.white},hover={background=primaryHoverTexture,textColor=Color.white},active={background=primaryHoverTexture,textColor=Color.white},border=new RectOffset(18,18,18,18),fontStyle=FontStyle.Bold};
            secondaryButtonStyle=new GUIStyle(primaryButtonStyle){normal={background=secondaryTexture,textColor=Color.white},hover={background=secondaryTexture,textColor=Color.white},active={background=secondaryTexture,textColor=Color.white}};
            aestheticInputStyle=new GUIStyle(settingsInputStyle){normal={background=inputTexture,textColor=new Color(0.05f,0.12f,0.14f)},focused={background=inputTexture,textColor=new Color(0.05f,0.12f,0.14f)},border=new RectOffset(15,15,15,15),padding=new RectOffset(16,16,8,8)};
            hudLabelStyle=new GUIStyle(centerStyle){fontSize=Mathf.RoundToInt(Mathf.Max(14,centerFontSize-5)*u),fontStyle=FontStyle.Bold,normal={textColor=new Color(0.70f,0.84f,0.80f)}};
            hudValueStyle=new GUIStyle(titleStyle){fontSize=Mathf.RoundToInt(Mathf.Max(24,titleFontSize-4)*u),fontStyle=FontStyle.Bold,normal={textColor=Color.white}};
        }

        static Texture2D CreateRoundedTexture(int size,float radius,Color color)
        {
            var texture=new Texture2D(size,size,TextureFormat.RGBA32,false){name="LeafGameRoundedUI",wrapMode=TextureWrapMode.Clamp,filterMode=FilterMode.Bilinear};
            var pixels=new Color[size*size];
            float half=(size-1)*0.5f;
            float inset=half-radius;
            for(int y=0;y<size;y++)for(int x=0;x<size;x++)
            {
                float dx=Mathf.Max(Mathf.Abs(x-half)-inset,0);
                float dy=Mathf.Max(Mathf.Abs(y-half)-inset,0);
                float alpha=Mathf.Clamp01(radius+0.5f-Mathf.Sqrt(dx*dx+dy*dy));
                Color pixel=color;pixel.a*=alpha;pixels[y*size+x]=pixel;
            }
            texture.SetPixels(pixels);texture.Apply(false,true);return texture;
        }

        void DrawAestheticGui()
        {
            InitStyles();EnsureAestheticStyles();
            if(state==AppState.Trial||state==AppState.Iti){DrawGameplayHud();return;}
            Rect safe=SafeAreaTopLeft();
            float u=UiScale();
            if(state==AppState.Setup)
            {
                DrawSetupBackground();
                if(settingsVisible)DrawSettingsScreen(safe,u);else DrawWelcomeScreen(safe,u);
                return;
            }
            float mx=Mathf.Max(14*u,safe.width*0.035f);
            if(state==AppState.Instructions)
            {
                Rect card=new Rect(safe.x+mx,safe.y+18*u,safe.width-2*mx,Mathf.Min(safe.height*0.48f,500*u));
                GUI.Box(card,GUIContent.none,aestheticPanelStyle);
                GUI.Label(new Rect(card.x+24*u,card.y+18*u,card.width-48*u,card.height-36*u),instructionsText,bodyStyle);
                GUI.Label(new Rect(safe.x+safe.width*0.07f,safe.y+safe.height*0.67f,safe.width*0.38f,80*u),instructionC1Label,centerStyle);
                GUI.Label(new Rect(safe.x+safe.width*0.55f,safe.y+safe.height*0.67f,safe.width*0.38f,80*u),instructionC2Label,centerStyle);
                float buttonW=Mathf.Min(330*u,safe.width*0.52f);
                if(GUI.Button(new Rect(safe.center.x-buttonW*0.5f,safe.yMax-74*u,buttonW,58*u),beginBlockButtonLabel,primaryButtonStyle))BeginBlock();
            }
            else if(state==AppState.Summary)
            {
                Rect card=new Rect(safe.center.x-Mathf.Min(380*u,safe.width*0.43f),safe.center.y-170*u,Mathf.Min(760*u,safe.width*0.86f),340*u);
                GUI.Box(card,GUIContent.none,aestheticPanelStyle);
                int completed=Math.Min(trialIndex,trials==null?0:trials.Count);
                double mean=accuracyResults.Count==0?0:(double)accuracyResults.FindAll(a=>a).Count/accuracyResults.Count;
                GUI.Label(new Rect(card.x+24*u,card.y+28*u,card.width-48*u,card.height-56*u),$"{completedTitle}\n\nAccuracy  {100*mean:F0}%\n\nTrials completed  {completed}",titleStyle);
            }
            else if(state==AppState.Fatal)
            {
                Rect card=new Rect(safe.x+mx,safe.y+safe.height*0.22f,safe.width-2*mx,safe.height*0.50f);
                GUI.Box(card,GUIContent.none,aestheticPanelStyle);
                GUI.Label(new Rect(card.x+24*u,card.y+24*u,card.width-48*u,card.height-48*u),fatalMessage,errorStyle);
            }
        }

        void DrawSetupBackground()
        {
            if(backgroundTexture!=null)GUI.DrawTexture(new Rect(0,0,Screen.width,Screen.height),backgroundTexture,ScaleMode.ScaleAndCrop,true);
            else
            {
                Color previous=GUI.color;GUI.color=Render(new Color32(7,34,50,255));
                GUI.DrawTexture(new Rect(0,0,Screen.width,Screen.height),Texture2D.whiteTexture);GUI.color=previous;
            }
        }

        void DrawWelcomeScreen(Rect safe,float u)
        {
            float cardW=Mathf.Min(720*u,safe.width*0.88f),cardH=Mathf.Min(590*u,safe.height*0.82f);
            Rect card=new Rect(safe.center.x-cardW*0.5f,safe.center.y-cardH*0.5f,cardW,cardH);
            GUI.Box(card,GUIContent.none,aestheticPanelStyle);
            GUI.Label(new Rect(card.x+30*u,card.y+34*u,card.width-60*u,70*u),applicationTitle,titleStyle);
            GUI.Label(new Rect(card.x+46*u,card.y+106*u,card.width-92*u,74*u),welcomeSubtitle,bodyStyle);
            float labelW=Mathf.Min(225*u,card.width*0.36f),inputX=card.x+labelW+38*u,inputW=card.width-labelW-78*u;
            float rowY=card.y+205*u,rowH=54*u;
            GUI.Label(new Rect(card.x+34*u,rowY,labelW,rowH),participantLabel,settingsLabelStyle);
            participant=GUI.TextField(new Rect(inputX,rowY,inputW,rowH),participant,aestheticInputStyle);
            rowY+=78*u;
            GUI.Label(new Rect(card.x+34*u,rowY,labelW,rowH),blockLabel,settingsLabelStyle);
            block=GUI.TextField(new Rect(inputX,rowY,inputW,rowH),block,aestheticInputStyle);
            SyncWelcomeValues();
            float gap=18*u,buttonW=(card.width-78*u-gap)*0.5f,buttonY=card.yMax-96*u;
            bool enabled=GUI.enabled;GUI.enabled=enabled&&!sessionStarting;
            if(GUI.Button(new Rect(card.x+30*u,buttonY,buttonW,58*u),settingsButtonLabel,secondaryButtonStyle))settingsVisible=true;
            if(GUI.Button(new Rect(card.x+48*u+buttonW,buttonY,buttonW,58*u),sessionStarting?"Connecting...":playButtonLabel,primaryButtonStyle))ApplySettingsAndContinue();
            GUI.enabled=enabled;
            if(!string.IsNullOrEmpty(settingsValidationError))GUI.Label(new Rect(card.x+34*u,buttonY-62*u,card.width-68*u,54*u),settingsValidationError,errorStyle);
        }

        void SyncWelcomeValues()
        {
            foreach(RuntimeSetting setting in runtimeSettings)
            {
                if(setting.field.Name==nameof(participant))setting.values[0]=participant;
                else if(setting.field.Name==nameof(block))setting.values[0]=block;
            }
        }

        void DrawSettingsScreen(Rect safe,float u)
        {
            if(runtimeSettings.Count==0)InitializeRuntimeSettings();
            float margin=Mathf.Max(12*u,safe.width*0.025f);
            Rect shell=new Rect(safe.x+margin,safe.y+margin,safe.width-2*margin,safe.height-2*margin);
            GUI.Box(shell,GUIContent.none,aestheticPanelStyle);
            float headerH=62*u;
            GUI.Label(new Rect(shell.x+26*u,shell.y+10*u,shell.width-180*u,headerH),"Game settings",settingsHeaderStyle);
            if(GUI.Button(new Rect(shell.xMax-150*u,shell.y+12*u,120*u,48*u),backButtonLabel,secondaryButtonStyle))
            {
                if(TryApplyRuntimeSettings(out settingsValidationError))settingsVisible=false;
            }
            float helpY=shell.y+headerH;
            GUI.Label(new Rect(shell.x+24*u,helpY,shell.width-48*u,34*u),"Drag anywhere in the list to scroll. Changes apply when you start the game.",settingsHelpStyle);
            float buttonH=56*u,errorH=string.IsNullOrEmpty(settingsValidationError)?0:50*u;
            float footerH=buttonH+errorH+24*u;
            settingsScrollViewport=new Rect(shell.x+22*u,helpY+40*u,shell.width-44*u,shell.yMax-(helpY+40*u)-footerH-18*u);
            float contentW=Mathf.Max(320*u,settingsScrollViewport.width-28*u);
            settingsScrollExtent=SettingsContentHeight(u);
            Rect contentRect=new Rect(0,0,contentW,settingsScrollExtent);
            bool enabled=GUI.enabled;GUI.enabled=enabled&&!sessionStarting;
            settingsScroll=GUI.BeginScrollView(settingsScrollViewport,settingsScroll,contentRect,false,true);
            float y=12*u;
            foreach(RuntimeSetting setting in runtimeSettings)
            {
                if(!string.IsNullOrEmpty(setting.header)){GUI.Label(new Rect(8*u,y,contentW-16*u,38*u),setting.header,settingsHeaderStyle);y+=44*u;}
                DrawRuntimeSetting(setting,8*u,y,contentW-16*u,u);y+=(setting.multiline?150:54)*u;
            }
            GUI.EndScrollView();
            float buttonW=Mathf.Min(390*u,shell.width*0.58f),buttonY=shell.yMax-footerH;
            if(GUI.Button(new Rect(shell.center.x-buttonW*0.5f,buttonY,buttonW,buttonH),sessionStarting?"Connecting...":"Apply & Play",primaryButtonStyle))ApplySettingsAndContinue();
            GUI.enabled=enabled;
            if(!string.IsNullOrEmpty(settingsValidationError))GUI.Label(new Rect(shell.x+24*u,buttonY+buttonH+3*u,shell.width-48*u,errorH),settingsValidationError,errorStyle);
        }

        void DrawGameplayHud()
        {
            Rect safe=SafeAreaTopLeft();float u=UiScale();
            float tileW=Mathf.Clamp(166*u,132*u,safe.width*0.23f),tileH=72*u,gap=10*u;
            float x=safe.xMax-2*tileW-gap-14*u,y=safe.y+12*u;
            if(x<safe.x+8*u){tileW=Mathf.Max(118*u,(safe.width-gap-28*u)*0.5f);x=safe.xMax-2*tileW-gap-10*u;}
            int seconds=Mathf.Max(0,Mathf.FloorToInt((float)Elapsed));
            if(seconds!=hudCachedSecond){hudCachedSecond=seconds;hudTimeValue=$"{seconds/60:00}:{seconds%60:00}";}
            if(accuracyResults.Count!=hudCachedResultCount)
            {
                hudCachedResultCount=accuracyResults.Count;
                int correct=0;for(int i=0;i<accuracyResults.Count;i++)if(accuracyResults[i])correct++;
                hudScoreValue=$"{correct} / {accuracyResults.Count}";
            }
            DrawHudTile(new Rect(x,y,tileW,tileH),timeBoardLabel,hudTimeValue);
            DrawHudTile(new Rect(x+tileW+gap,y,tileW,tileH),scoreBoardLabel,hudScoreValue);
        }

        void DrawHudTile(Rect rect,string label,string value)
        {
            GUI.Box(rect,GUIContent.none,aestheticPanelStyle);
            GUI.Label(new Rect(rect.x+8,rect.y+4,rect.width-16,22*UiScale()),label,hudLabelStyle);
            GUI.Label(new Rect(rect.x+8,rect.y+23*UiScale(),rect.width-16,rect.height-25*UiScale()),value,hudValueStyle);
        }
    }
}