using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace LeafGame
{
    public sealed class LeafVisualRenderer : IDisposable
    {
        sealed class Batch
        {
            public readonly Mesh mesh;
            public readonly Material material;
            public readonly List<Vector3> vertices;
            public readonly List<int> triangles;
            public Batch(Transform parent,string name,Shader shader,int order,int vertexCapacity,int triangleCapacity)
            {
                vertices=new List<Vector3>(Math.Max(4,vertexCapacity));
                triangles=new List<int>(Math.Max(6,triangleCapacity));
                var go=new GameObject(name);go.transform.SetParent(parent,false);
                mesh=new Mesh{name=name+"Mesh"};
                if(vertexCapacity>65535)mesh.indexFormat=IndexFormat.UInt32;
                mesh.MarkDynamic();
                var mf=go.AddComponent<MeshFilter>();mf.sharedMesh=mesh;
                var mr=go.AddComponent<MeshRenderer>();material=new Material(shader){name=name+"Material"};
                mr.sharedMaterial=material;mr.sortingOrder=order;
            }
            public void Begin(){vertices.Clear();triangles.Clear();}
            public void Add(IList<Vector2> polygon,Vector2 pixelPosition,float z,int screenW,int screenH)
            {
                if(polygon==null||polygon.Count<3)return;
                int center=vertices.Count;
                vertices.Add(new Vector3(pixelPosition.x-screenW*0.5f,screenH*0.5f-pixelPosition.y,z));
                int perimeter=center+1;
                for(int i=0;i<polygon.Count;i++)
                {
                    Vector2 p=pixelPosition+polygon[i];
                    vertices.Add(new Vector3(p.x-screenW*0.5f,screenH*0.5f-p.y,z));
                }
                for(int i=0;i<polygon.Count;i++)
                {
                    triangles.Add(center);
                    triangles.Add(perimeter+i);
                    triangles.Add(perimeter+(i+1)%polygon.Count);
                }
            }
            public void End(Color color)
            {
                mesh.Clear(false);mesh.SetVertices(vertices);mesh.SetTriangles(triangles,0,true);
                material.color=color;
            }
            public void Clear(){mesh.Clear(false);}
            public void Dispose(){UnityEngine.Object.Destroy(mesh);UnityEngine.Object.Destroy(material);}
        }

        readonly GameObject root;
        readonly Batch outer1,outer2,inner1,inner2;
        readonly Mesh cueMesh;
        readonly Material cueMaterial;
        readonly MeshRenderer cueRenderer;
        readonly Canvas cueCanvas;
        readonly Text cueText;
        string cueTextValue="";
        Color cueTextColor=Color.clear;
        readonly Camera camera;
        int screenW,screenH;

        public LeafVisualRenderer(Transform parent,float cueWidth,float cueHeight,float cueRadius,int cueSegments,
            int cueFontSize,float cueCharacterSize,Color cueTextOutlineColor,float cueTextOutlineDistance,Color backgroundColor,int leavesPerFlock,int leafVertexCount)
        {
            root=new GameObject("StimulusRenderer");root.transform.SetParent(parent,false);
            Shader shader=Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            if(shader==null) throw new InvalidOperationException("No unlit color shader found.");
            int maxCopies=Math.Max(1,leavesPerFlock)*4;
            int vertexCapacity=maxCopies*Math.Max(4,leafVertexCount);
            int triangleCapacity=maxCopies*Math.Max(2,leafVertexCount-2)*3;
            outer1=new Batch(root.transform,"Flock1Border",shader,1,vertexCapacity,triangleCapacity);
            outer2=new Batch(root.transform,"Flock2Border",shader,1,vertexCapacity,triangleCapacity);
            inner1=new Batch(root.transform,"Flock1Fill",shader,2,vertexCapacity,triangleCapacity);
            inner2=new Batch(root.transform,"Flock2Fill",shader,2,vertexCapacity,triangleCapacity);
            var cueGo=new GameObject("CueBox");cueGo.transform.SetParent(root.transform,false);
            cueMesh=BuildRoundedRect(cueWidth,cueHeight,Mathf.Min(cueRadius,Mathf.Min(cueWidth,cueHeight)*0.5f),cueSegments);cueGo.AddComponent<MeshFilter>().sharedMesh=cueMesh;
            cueRenderer=cueGo.AddComponent<MeshRenderer>();cueMaterial=new Material(shader){name="CueMaterial"};cueRenderer.sharedMaterial=cueMaterial;cueRenderer.sortingOrder=5;
            var canvasGo=new GameObject("CueCanvas",typeof(RectTransform),typeof(Canvas),typeof(CanvasScaler));canvasGo.transform.SetParent(root.transform,false);
            cueCanvas=canvasGo.GetComponent<Canvas>();cueCanvas.renderMode=RenderMode.ScreenSpaceOverlay;cueCanvas.sortingOrder=100;
            var scaler=canvasGo.GetComponent<CanvasScaler>();scaler.uiScaleMode=CanvasScaler.ScaleMode.ConstantPixelSize;scaler.scaleFactor=1;
            var textGo=new GameObject("CueText",typeof(RectTransform),typeof(CanvasRenderer),typeof(Text));textGo.transform.SetParent(canvasGo.transform,false);
            var textRect=textGo.GetComponent<RectTransform>();textRect.anchorMin=textRect.anchorMax=textRect.pivot=new Vector2(0.5f,0.5f);textRect.anchoredPosition=Vector2.zero;textRect.sizeDelta=new Vector2(cueWidth,cueHeight);
            cueText=textGo.GetComponent<Text>();cueText.alignment=TextAnchor.MiddleCenter;cueText.raycastTarget=false;cueText.supportRichText=false;
            int maximumFontSize=Mathf.Max(1,Mathf.RoundToInt(cueFontSize*Mathf.Max(0.01f,cueCharacterSize)));cueText.fontSize=maximumFontSize;cueText.resizeTextForBestFit=true;cueText.resizeTextMinSize=Mathf.Max(1,maximumFontSize/2);cueText.resizeTextMaxSize=maximumFontSize;
            cueText.horizontalOverflow=HorizontalWrapMode.Wrap;cueText.verticalOverflow=VerticalWrapMode.Truncate;cueText.text="";cueText.color=Color.black;
            var font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");if(font!=null)cueText.font=font;
            var outline=textGo.AddComponent<UnityEngine.UI.Outline>();outline.effectColor=cueTextOutlineColor;outline.effectDistance=new Vector2(cueTextOutlineDistance,-cueTextOutlineDistance);outline.useGraphicAlpha=true;
            camera=Camera.main;
            if(camera==null)
            {
                var cgo=new GameObject("Main Camera");cgo.tag="MainCamera";camera=cgo.AddComponent<Camera>();
            }
            camera.orthographic=true;camera.transform.position=new Vector3(0,0,-100);camera.transform.rotation=Quaternion.identity;
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=backgroundColor;camera.nearClipPlane=0.1f;camera.farClipPlane=500;
            SyncScreen();
        }

        public void SyncScreen()
        {
            screenW=Screen.width;screenH=Screen.height;
            camera.orthographicSize=Math.Max(1,screenH*0.5f);camera.aspect=(float)screenW/Math.Max(1,screenH);
        }

        public static List<Vector2> CreateLeafShape(Direction4 pointing,float length,float width,int arcPoints)
        {
            int sidePoints=Mathf.Clamp(arcPoints,5,64);
            var result=new List<Vector2>(sidePoints*2+4);
            float stemRear=-length*0.5f,bodyRear=-length*0.30f,tip=length*0.5f;
            float halfWidth=width*0.5f,stemHalf=Mathf.Min(halfWidth*0.32f,Mathf.Max(0.75f,width*0.055f));
            float degrees=pointing==Direction4.Right?0:pointing==Direction4.Down?90:pointing==Direction4.Left?180:-90;
            float radians=degrees*Mathf.Deg2Rad,cos=Mathf.Cos(radians),sin=Mathf.Sin(radians);

            result.Add(new Vector2(cos*stemRear-sin*(-stemHalf),sin*stemRear+cos*(-stemHalf)));
            result.Add(new Vector2(cos*bodyRear-sin*(-stemHalf),sin*bodyRear+cos*(-stemHalf)));
            for(int i=1;i<=sidePoints;i++)
            {
                float t=i/(float)sidePoints;
                float x=Mathf.Lerp(bodyRear,tip,t);
                float envelope=Mathf.Pow(Mathf.Max(0f,Mathf.Sin(Mathf.PI*t)),0.72f);
                float organic=Mathf.Min(1f,0.90f+0.06f*Mathf.Sin(Mathf.PI*t)+0.04f*Mathf.Sin(2*Mathf.PI*t));
                float y=-(stemHalf*(1-t)+(halfWidth-stemHalf)*envelope*organic);
                result.Add(new Vector2(cos*x-sin*y,sin*x+cos*y));
            }
            for(int i=sidePoints-1;i>=1;i--)
            {
                float t=i/(float)sidePoints;
                float x=Mathf.Lerp(bodyRear,tip,t);
                float envelope=Mathf.Pow(Mathf.Max(0f,Mathf.Sin(Mathf.PI*t)),0.72f);
                float organic=Mathf.Min(1f,0.94f+0.08f*Mathf.Sin(Mathf.PI*t)-0.05f*Mathf.Sin(2*Mathf.PI*t));
                float y=stemHalf*(1-t)+(halfWidth-stemHalf)*envelope*organic;
                result.Add(new Vector2(cos*x-sin*y,sin*x+cos*y));
            }
            result.Add(new Vector2(cos*bodyRear-sin*stemHalf,sin*bodyRear+cos*stemHalf));
            result.Add(new Vector2(cos*stemRear-sin*stemHalf,sin*stemRear+cos*stemHalf));
            return result;
        }

        public void RenderLeaves(IList<LeafState> leaves,Rect field,float maxRadius,
            IList<Vector2> in1,IList<Vector2> out1,IList<Vector2> in2,IList<Vector2> out2,
            Color fill1,Color fill2,Color border1,Color border2)
        {
            if(screenW!=Screen.width||screenH!=Screen.height)SyncScreen();
            outer1.Begin();outer2.Begin();inner1.Begin();inner2.Begin();
            foreach(var leaf in leaves)
            {
                var ob=leaf.flock==1?outer1:outer2;var ib=leaf.flock==1?inner1:inner2;
                var os=leaf.flock==1?out1:out2;var ins=leaf.flock==1?in1:in2;
                float dx=leaf.position.x-maxRadius<field.xMin?field.width:(leaf.position.x+maxRadius>field.xMax?-field.width:0f);
                float dy=leaf.position.y-maxRadius<field.yMin?field.height:(leaf.position.y+maxRadius>field.yMax?-field.height:0f);
                AddCopy(ob,ib,os,ins,leaf.position);
                if(dx!=0)AddCopy(ob,ib,os,ins,leaf.position+new Vector2(dx,0));
                if(dy!=0)AddCopy(ob,ib,os,ins,leaf.position+new Vector2(0,dy));
                if(dx!=0&&dy!=0)AddCopy(ob,ib,os,ins,leaf.position+new Vector2(dx,dy));
            }
            outer1.End(border1);outer2.End(border2);inner1.End(fill1);inner2.End(fill2);
        }

        public void RenderInstructionDemo(Color c1,Color c2,Color border,Rect demoArea,float length,float width,float outerLength,float outerWidth,int arcPoints)
        {
            var demo=new List<LeafState>{
                new LeafState{flock=1,position=new Vector2(demoArea.x+demoArea.width*0.28f,demoArea.y+demoArea.height*0.65f)},
                new LeafState{flock=2,position=new Vector2(demoArea.x+demoArea.width*0.72f,demoArea.y+demoArea.height*0.65f)}};
            var i=CreateLeafShape(Direction4.Right,length,width,arcPoints);var o=CreateLeafShape(Direction4.Right,outerLength,outerWidth,arcPoints);
            RenderLeaves(demo,demoArea,Mathf.Sqrt(outerLength*outerLength+outerWidth*outerWidth)/2f,i,o,i,o,c1,c2,border,border);
            SetCueVisible(false,Color.clear);
        }

        void AddCopy(Batch ob,Batch ib,IList<Vector2> os,IList<Vector2> ins,Vector2 p)
        {
            ob.Add(os,p,0,screenW,screenH);ib.Add(ins,p,-0.1f,screenW,screenH);
        }

        public void SetCueVisible(bool visible,Color color)
        {
            cueMaterial.color=color;cueRenderer.enabled=visible;cueCanvas.enabled=visible;if(!visible)SetCueText("",Color.clear);
        }
        public void SetCueText(string value,Color color)
        {
            value=value??"";cueText.enabled=value.Length>0;if(cueTextValue==value&&cueTextColor==color)return;
            cueTextValue=value;cueTextColor=color;cueText.text=value;cueText.color=color;
        }
        public void ClearLeaves(){outer1.Clear();outer2.Clear();inner1.Clear();inner2.Clear();}

        static float[] Offsets(float p,float min,float max,float size,float radius)
        {
            if(p-radius<min)return new[]{0f,size};
            if(p+radius>max)return new[]{0f,-size};
            return Zero;
        }
        static readonly float[] Zero={0f};

        static Mesh BuildRoundedRect(float width,float height,float radius,int segments)
        {
            segments=Mathf.Clamp(segments,1,32);
            var verts=new List<Vector3>{new Vector3(0,0,-0.2f)};
            float hx=width/2f,hy=height/2f;
            Vector2[] centres={new Vector2(hx-radius,hy-radius),new Vector2(-hx+radius,hy-radius),new Vector2(-hx+radius,-hy+radius),new Vector2(hx-radius,-hy+radius)};
            float[] starts={0,90,180,270};
            for(int c=0;c<4;c++)for(int i=0;i<=segments;i++)
            {
                float a=(starts[c]+i*90f/segments)*Mathf.Deg2Rad;
                Vector2 p=centres[c]+new Vector2(Mathf.Cos(a),Mathf.Sin(a))*radius;
                verts.Add(new Vector3(p.x,p.y,-0.2f));
            }
            var tris=new List<int>();for(int i=1;i<verts.Count;i++){tris.Add(0);tris.Add(i==verts.Count-1?1:i+1);tris.Add(i);}
            var m=new Mesh{name="RoundedCueMesh"};m.SetVertices(verts);m.SetTriangles(tris,0);return m;
        }

        public void Dispose()
        {
            outer1.Dispose();outer2.Dispose();inner1.Dispose();inner2.Dispose();
            UnityEngine.Object.Destroy(cueMesh);UnityEngine.Object.Destroy(cueMaterial);UnityEngine.Object.Destroy(root);
        }
    }
}


