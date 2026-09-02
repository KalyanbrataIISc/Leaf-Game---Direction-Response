using System;
using System.Collections.Generic;
using UnityEngine;

namespace LeafGame
{
    public struct LeafState
    {
        public Vector2 position;
        public Vector2 velocity;
        public int flock;
    }

    public struct FieldChoice
    {
        public bool feasible;
        public Rect rect;
        public int gcd, repeatsX, repeatsY;
        public string message;
    }

    public struct LayoutResult
    {
        public bool feasible;
        public string message;
        public List<LeafState> leaves;
    }

    public static class LeafLaneLayout
    {
        public static FieldChoice ChooseField(int screenWidth,int screenHeight,int requiredGcd,int maxShrink,int minRepeats)
        {
            var result=new FieldChoice { feasible=false, rect=new Rect(0,0,screenWidth,screenHeight) };
            maxShrink=Math.Min(maxShrink,Math.Min(screenWidth-1,screenHeight-1));
            if(maxShrink<0 || requiredGcd*minRepeats>Math.Min(screenWidth,screenHeight))
            { result.message="Display is too small for the requested fixed paths."; return result; }
            long best=-1; int bw=0,bh=0,bg=0;
            for(int w=screenWidth; w>=screenWidth-maxShrink; w--)
            for(int h=screenHeight; h>=screenHeight-maxShrink; h--)
            {
                int g=Gcd(w,h); if(g<requiredGcd || w/g<minRepeats || h/g<minRepeats) continue;
                long area=(long)w*h; if(area>best){best=area;bw=w;bh=h;bg=g;}
            }
            if(best<0)
            {
                result.message=$"No field within {maxShrink}px of {screenWidth}x{screenHeight} has gcd >= {requiredGcd} repeated {minRepeats}+ times.";
                return result;
            }
            int mx=(screenWidth-bw)/2, my=(screenHeight-bh)/2;
            result.feasible=true; result.rect=new Rect(mx,my,bw,bh); result.gcd=bg;
            result.repeatsX=bw/bg; result.repeatsY=bh/bg;
            result.message=$"{bw}x{bh} (gcd {bg}, {result.repeatsX}x{result.repeatsY} repeats), giving up {screenWidth-bw} x {screenHeight-bh}px.";
            return result;
        }

        public static LayoutResult Create(int n,Rect field,float outerLength,float outerWidth,
            Direction4 p1,Direction4 m1,Direction4 p2,Direction4 m2,float speedPerFrame,float margin,System.Random rng)
        {
            var leaves=new List<LeafState>(2*n);
            Vector2 e1=Extents(p1,outerLength,outerWidth), e2=Extents(p2,outerLength,outerWidth);
            Vector2 v1=DirectionUtil.Vector(m1), v2=DirectionUtil.Vector(m2);
            float[] x=new float[2*n],y=new float[2*n];
            bool sameAxis=(v1.x!=0)==(v2.x!=0);
            string message="";
            if(sameAxis)
            {
                bool onX=v1.x!=0;
                float crossSize=onX?field.height:field.width, alongSize=onX?field.width:field.height;
                float crossExtent=Math.Max(onX?e1.y:e1.x,onX?e2.y:e2.x);
                float alongE1=onX?e1.x:e1.y, alongE2=onX?e2.x:e2.y;
                int laneCount=(int)Math.Floor(crossSize/(crossExtent+margin));
                if(laneCount<2) return Fail("Fewer than two disjoint lanes fit.");
                var lanes1=new List<float>(); var lanes2=new List<float>();
                for(int i=0;i<laneCount;i++)
                {
                    float c=(i+0.5f)*(crossSize/laneCount);
                    if((i&1)==0) lanes1.Add(c); else lanes2.Add(c);
                }
                if(!PlaceAlong(n,lanes1,alongSize,alongE1+margin,rng,out var a1,out var c1,out message)) return Fail(message);
                if(!PlaceAlong(n,lanes2,alongSize,alongE2+margin,rng,out var a2,out var c2,out message)) return Fail(message);
                for(int i=0;i<n;i++)
                {
                    if(onX){x[i]=field.xMin+a1[i];y[i]=field.yMin+c1[i];x[n+i]=field.xMin+a2[i];y[n+i]=field.yMin+c2[i];}
                    else{x[i]=field.xMin+c1[i];y[i]=field.yMin+a1[i];x[n+i]=field.xMin+c2[i];y[n+i]=field.yMin+a2[i];}
                }
            }
            else
            {
                bool flock1OnX=v1.x!=0;
                Vector2 eA=flock1OnX?e1:e2, eB=flock1OnX?e2:e1;
                Vector2 vecA=flock1OnX?v1:v2, vecB=flock1OnX?v2:v1;
                float sigmaA=vecA.x, sigmaB=vecB.y;
                int g=Gcd(Mathf.RoundToInt(field.width),Mathf.RoundToInt(field.height));
                float cReq=(eA.x+eB.x)/2f+(eA.y+eB.y)/2f+2f*margin;
                if(g<=2f*cReq) return Fail($"Field gcd {g} must exceed {2f*cReq:F0}px for perpendicular paths.");
                float hA=(g/2f-cReq)/2f, hB=hA;
                int maxLanesA=Math.Max(1,(int)Math.Floor(field.height/(eA.y+margin)));
                int maxLanesB=Math.Max(1,(int)Math.Floor(field.width/(eB.x+margin)));
                int slotsA=Math.Max(1,Mathf.RoundToInt(field.width/g))*CountSlots(hA,eA.x+margin);
                int slotsB=Math.Max(1,Mathf.RoundToInt(field.height/g))*CountSlots(hB,eB.y+margin);
                int nLanesA=Math.Min(maxLanesA,Math.Max(1,CeilDiv(n,slotsA)));
                int nLanesB=Math.Min(maxLanesB,Math.Max(1,CeilDiv(n,slotsB)));
                var lanesA=LaneCentres(nLanesA,field.height); var lanesB=LaneCentres(nLanesB,field.width);
                if(!PlaceInvariant(n,lanesA,field.width,g,hA,0,sigmaA,sigmaB,eA.x+margin,rng,out var alongA,out var crossA,out message)) return Fail(message);
                if(!PlaceInvariant(n,lanesB,field.height,g,hB,g/2f,sigmaB,sigmaA,eB.y+margin,rng,out var alongB,out var crossB,out message)) return Fail(message);
                for(int i=0;i<n;i++)
                {
                    if(flock1OnX){x[i]=field.xMin+alongA[i];y[i]=field.yMin+crossA[i];x[n+i]=field.xMin+crossB[i];y[n+i]=field.yMin+alongB[i];}
                    else{x[n+i]=field.xMin+alongA[i];y[n+i]=field.yMin+crossA[i];x[i]=field.xMin+crossB[i];y[i]=field.yMin+alongB[i];}
                }
            }
            for(int i=0;i<2*n;i++)
            {
                bool first=i<n; Vector2 vel=(first?v1:v2)*speedPerFrame;
                leaves.Add(new LeafState { position=new Vector2(x[i],y[i]),velocity=vel,flock=first?1:2 });
            }
            return new LayoutResult { feasible=true,message="ok",leaves=leaves };
        }

        public static void Advance(List<LeafState> leaves,Rect field)
        {
            for(int i=0;i<leaves.Count;i++)
            {
                var l=leaves[i]; l.position+=l.velocity;
                l.position.x=Mod(l.position.x-field.xMin,field.width)+field.xMin;
                l.position.y=Mod(l.position.y-field.yMin,field.height)+field.yMin;
                leaves[i]=l;
            }
        }

        public static bool Verify(List<LeafState> source,Rect field,Vector2 e1,Vector2 e2,int maxFrames,out float minClearance)
        {
            var leaves=new List<LeafState>(source); minClearance=float.PositiveInfinity;
            float speed=0; foreach(var l in leaves) speed=Math.Max(speed,l.velocity.magnitude);
            long period=Lcm(Mathf.RoundToInt(field.width),Mathf.RoundToInt(field.height));
            int frames=Math.Min(maxFrames,(int)Math.Ceiling(period/Math.Max(speed,1e-6f)));
            for(int f=0;f<frames;f++)
            {
                for(int i=0;i<leaves.Count;i++) for(int j=i+1;j<leaves.Count;j++)
                {
                    Vector2 a=leaves[i].position,b=leaves[j].position;
                    float dx=Math.Abs(a.x-b.x),dy=Math.Abs(a.y-b.y);
                    dx=Math.Min(dx,field.width-dx);dy=Math.Min(dy,field.height-dy);
                    Vector2 sum=leaves[i].flock==leaves[j].flock ? (leaves[i].flock==1?e1:e2) : (e1+e2)/2f;
                    float clearance=Math.Max(dx-sum.x,dy-sum.y); minClearance=Math.Min(minClearance,clearance);
                    if(clearance<0) return false;
                }
                Advance(leaves,field);
            }
            return true;
        }

        public static Vector2 Extents(Direction4 p,float length,float width)
            => (p==Direction4.Left||p==Direction4.Right)?new Vector2(length,width):new Vector2(width,length);

        static bool PlaceAlong(int n,List<float> lanes,float alongSize,float minSpacing,System.Random rng,
            out float[] along,out float[] cross,out string message)
        {
            along=new float[n];cross=new float[n];message="";int lc=lanes.Count;
            if(lc<1){message="No lanes available.";return false;}
            int[] counts=new int[lc];for(int i=0;i<n;i++)counts[i%lc]++;
            int cap=(int)Math.Floor(alongSize/minSpacing);foreach(int c in counts)if(c>cap){message="Along-lane capacity exceeded.";return false;}
            float[] offsets=new float[lc];int[] placed=new int[lc];for(int l=0;l<lc;l++)offsets[l]=(float)rng.NextDouble()*alongSize;
            for(int i=0;i<n;i++){int l=i%lc;float step=alongSize/counts[l];along[i]=Mod(offsets[l]+placed[l]++*step,alongSize);cross[i]=lanes[l];}
            return true;
        }

        static bool PlaceInvariant(int n,List<float> lanes,float alongSize,int modulus,float half,float centre,
            float ownSign,float otherSign,float spacing,System.Random rng,out float[] along,out float[] cross,out string message)
        {
            along=new float[n];cross=new float[n];message="";int lc=lanes.Count;
            int windows=Math.Max(1,Mathf.RoundToInt(alongSize/modulus)), slots=CountSlots(half,spacing);
            if(lc*windows*slots<n){message="Invariant-grid capacity exceeded.";return false;}
            float step=slots>1?spacing:0, slack=Math.Max(0,2*half-(slots-1)*step);
            float[] jitter=new float[lc];int[] counts=new int[lc];for(int i=0;i<n;i++)counts[i%lc]++;
            for(int l=0;l<lc;l++)jitter[l]=(float)rng.NextDouble()*slack;
            int placed=0,total=windows*slots;
            for(int lane=0;lane<lc;lane++) for(int j=0;j<counts[lane];j++)
            {
                int idx=(int)((long)j*total/counts[lane]);idx=(idx+lane)%total;
                int win=idx%windows,slot=idx/windows;
                float invariant=centre-half+jitter[lane]+slot*step;
                float phase=Mod(ownSign*(invariant-otherSign*lanes[lane]),modulus);
                along[placed]=Mod(phase+win*modulus,alongSize);cross[placed]=lanes[lane];placed++;
            }
            return true;
        }

        static List<float> LaneCentres(int n,float size){var a=new List<float>(n);for(int i=0;i<n;i++)a.Add((i+0.5f)*(size/n));return a;}
        static int CountSlots(float half,float spacing)=>Math.Max(1,(int)Math.Floor(2*half/spacing)+1);
        static int CeilDiv(int a,int b)=>(a+b-1)/b;
        static LayoutResult Fail(string m)=>new LayoutResult{feasible=false,message=m,leaves=new List<LeafState>()};
        static float Mod(float x,float m){float r=x%m;return r<0?r+m:r;}
        public static int Gcd(int a,int b){a=Math.Abs(a);b=Math.Abs(b);while(b!=0){int t=a%b;a=b;b=t;}return a;}
        static long Lcm(int a,int b)=>(long)a/Gcd(a,b)*b;
    }
}