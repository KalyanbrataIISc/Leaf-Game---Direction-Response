using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace LeafGame
{
    public enum Direction4 { Up, Down, Left, Right }
    public enum CueKind { C1, C2 }
    public enum NfSourceType { File, Tcp, BciCore }

    [Serializable]
    public sealed class TrialDefinition
    {
        public Direction4 c1Point, c1Move, c2Point, c2Move;
        public CueKind cue;
        public Direction4 CorrectResponse => cue == CueKind.C1 ? c1Point : c2Move;
        public string CueWord => cue == CueKind.C1 ? "Pointing" : "Moving";
        public int NfIndex => cue == CueKind.C1 ? 0 : 1;
    }

    public static class DirectionUtil
    {
        public static Vector2 Vector(Direction4 d)
        {
            switch (d)
            {
                case Direction4.Up: return new Vector2(0, -1);
                case Direction4.Down: return new Vector2(0, 1);
                case Direction4.Left: return new Vector2(-1, 0);
                default: return new Vector2(1, 0);
            }
        }
        public static string Text(Direction4 d) => d.ToString().ToLowerInvariant();
    }

    public static class TrialPlanner
    {
        public static List<TrialDefinition> Build(int count, double c1Probability, System.Random rng)
        {
            var dirs = (Direction4[])Enum.GetValues(typeof(Direction4));
            var combos = new List<TrialDefinition>(24);
            foreach (var p1 in dirs)
            foreach (var m1 in dirs)
            {
                if (p1 == m1) continue;
                var rem = dirs.Where(d => d != p1 && d != m1).ToArray();
                combos.Add(new TrialDefinition { c1Point=p1, c1Move=m1, c2Point=rem[0], c2Move=rem[1] });
                combos.Add(new TrialDefinition { c1Point=p1, c1Move=m1, c2Point=rem[1], c2Move=rem[0] });
            }
            var cues = new List<CueKind>(count);
            int c1Count = Math.Max(0, Math.Min(count, (int)Math.Round(count * Math.Max(0.0, Math.Min(1.0, c1Probability)))));
            for (int i=0; i<c1Count; i++) cues.Add(CueKind.C1);
            while (cues.Count < count) cues.Add(CueKind.C2);
            Shuffle(cues, rng);

            var result = new List<TrialDefinition>(count);
            int cursor = 0;
            while (cursor < count)
            {
                var cycle = combos.Select(c => new TrialDefinition {
                    c1Point=c.c1Point, c1Move=c.c1Move, c2Point=c.c2Point, c2Move=c.c2Move
                }).ToList();
                Shuffle(cycle, rng);
                int take = Math.Min(cycle.Count, count-cursor);
                for (int i=0; i<take; i++) { cycle[i].cue=cues[cursor+i]; result.Add(cycle[i]); }
                cursor += take;
            }
            return result;
        }

        public static double TruncatedExponential(double mean, double cap, System.Random rng)
        {
            for (int i=0; i<100; i++)
            {
                double u = Math.Max(double.Epsilon, rng.NextDouble());
                double candidate = -mean * Math.Log(u);
                if (candidate <= cap) return candidate;
            }
            return cap;
        }

        static void Shuffle<T>(IList<T> list, System.Random rng)
        {
            for (int i=list.Count-1; i>0; i--)
            {
                int j=rng.Next(i+1); T t=list[i]; list[i]=list[j]; list[j]=t;
            }
        }
    }

    public static class MonotonicClock
    {
        static readonly double TickToSeconds = 1.0 / Stopwatch.Frequency;
        public static double Now => Stopwatch.GetTimestamp() * TickToSeconds;
    }

    public sealed class SsvepPhaseSchedule
    {
        public double RefreshHz { get; private set; }
        public double Period => 1.0 / RefreshHz;
        public int Count => _c1.Length;
        readonly float[] _c1;
        readonly float[] _c2;

        public SsvepPhaseSchedule(double refreshHz, double horizonSeconds, double f1, double f2,
            double meanLuminance, double modulationDepth, double phase1Degrees, double phase2Degrees)
        {
            RefreshHz = Math.Max(1.0, refreshHz);
            int n = Math.Max(2, (int)Math.Ceiling(Math.Max(0.01, horizonSeconds) * RefreshHz) + 4);
            _c1 = new float[n]; _c2 = new float[n];
            double mean = Math.Max(0.0, Math.Min(1.0, meanLuminance));
            double depth = Math.Max(0.0, Math.Min(1.0, modulationDepth));
            double amplitude = Math.Min(mean, 1.0 - mean) * depth;
            double p1 = phase1Degrees * Math.PI / 180.0;
            double p2 = phase2Degrees * Math.PI / 180.0;
            for (int slot=0; slot<n; slot++)
            {
                double t = slot / RefreshHz;
                _c1[slot] = (float)(mean + amplitude * Math.Sin(2.0 * Math.PI * f1 * t + p1));
                _c2[slot] = (float)(mean + amplitude * Math.Sin(2.0 * Math.PI * f2 * t + p2));
            }
        }

        public bool TrySampleForPresentation(double elapsedSeconds, out int slot, out float c1, out float c2)
        {
            slot = Math.Max(0, (int)Math.Round(elapsedSeconds * RefreshHz));
            if (slot >= _c1.Length) { c1=0; c2=0; return false; }
            c1=_c1[slot]; c2=_c2[slot]; return true;
        }
    }

    public sealed class TriggerSender : IDisposable
    {
        readonly string host; readonly int port;
        public TriggerSender(string host, int port) { this.host=host; this.port=port; }
        public void Send(string name, int value)
        {
            try
            {
                byte[] data=Encoding.ASCII.GetBytes(value.ToString(CultureInfo.InvariantCulture));
                using (var udp=new UdpClient()) udp.Send(data,data.Length,host,port);
            }
            catch (Exception e) { UnityEngine.Debug.LogWarning("Failed to send UDP trigger: "+e.Message); }
        }
        public void Dispose() { }
    }

    public interface INfReader : IDisposable
    {
        bool IsReady { get; }
        string Error { get; }
        string Description { get; }
        bool TryRead(int zeroBasedIndex,out double value);
    }

    public sealed class NfBinaryReader : INfReader
    {
        readonly string path;
        readonly byte[] bytes=new byte[8];
        FileStream stream;
        public NfBinaryReader(string path){this.path=path;}
        public bool IsReady=>true;
        public string Error=>null;
        public string Description=>path;
        bool EnsureOpen()
        {
            if(stream!=null)return true;
            try{stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);return true;}
            catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}
        }
        public bool TryRead(int zeroBasedIndex,out double value)
        {
            value=0;
            try
            {
                if(!EnsureOpen())return false;
                int need=(zeroBasedIndex+1)*8;if(stream.Length<need)return false;
                stream.Position=zeroBasedIndex*8;if(stream.Read(bytes,0,8)!=8)return false;
                if(!BitConverter.IsLittleEndian)Array.Reverse(bytes);
                value=BitConverter.ToDouble(bytes,0);
                if(!BitConverter.IsLittleEndian)Array.Reverse(bytes);
                return !double.IsNaN(value)&&!double.IsInfinity(value);
            }
            catch(IOException){Reset();return false;}catch(UnauthorizedAccessException){Reset();return false;}
        }
        void Reset(){stream?.Dispose();stream=null;}
        public void Dispose(){Reset();}
    }

    public sealed class NfTcpReader : INfReader
    {
        const int ValueCount=3;
        const int RecordByteCount=ValueCount*8;
        readonly object gate=new object();
        readonly string host;
        readonly int port;
        readonly Thread worker;
        readonly double[] latest=new double[ValueCount];
        TcpClient client;
        long sequence;
        long lastReadSequence=-1;
        bool ready;
        volatile bool disposed;
        string error;

        public NfTcpReader(string host,int port)
        {
            if(string.IsNullOrWhiteSpace(host))throw new ArgumentException("TCP host cannot be empty.",nameof(host));
            if(port<1||port>65535)throw new ArgumentOutOfRangeException(nameof(port),"TCP port must be between 1 and 65535.");
            this.host=host.Trim();this.port=port;
            worker=new Thread(Run){IsBackground=true,Name="LeafGame NF TCP reader"};
            worker.Start();
        }

        public bool IsReady{get{lock(gate)return ready;}}
        public string Error{get{lock(gate)return error;}}
        public string Description=>$"tcp://{host}:{port}";

        void Run()
        {
            try
            {
                var connectedClient=new TcpClient{NoDelay=true};
                lock(gate)
                {
                    if(disposed){connectedClient.Dispose();return;}
                    client=connectedClient;
                }
                connectedClient.Connect(host,port);
                NetworkStream stream=connectedClient.GetStream();
                var record=new byte[RecordByteCount];
                while(!disposed)
                {
                    ReadExactly(stream,record);
                    double v0=ReadLittleEndianDouble(record,0);
                    double v1=ReadLittleEndianDouble(record,8);
                    double v2=ReadLittleEndianDouble(record,16);
                    lock(gate)
                    {
                        latest[0]=v0;latest[1]=v1;latest[2]=v2;
                        sequence++;ready=true;
                    }
                }
            }
            catch(Exception e)
            {
                lock(gate)if(!disposed)error=e.Message;
            }
        }

        static void ReadExactly(NetworkStream stream,byte[] buffer)
        {
            int offset=0;
            while(offset<buffer.Length)
            {
                int count=stream.Read(buffer,offset,buffer.Length-offset);
                if(count<=0)throw new EndOfStreamException("The NF TCP server closed the connection.");
                offset+=count;
            }
        }

        static double ReadLittleEndianDouble(byte[] buffer,int offset)
        {
            if(BitConverter.IsLittleEndian)return BitConverter.ToDouble(buffer,offset);
            var copy=new byte[8];Array.Copy(buffer,offset,copy,0,8);Array.Reverse(copy);
            return BitConverter.ToDouble(copy,0);
        }

        public bool TryRead(int zeroBasedIndex,out double value)
        {
            value=0;
            if(zeroBasedIndex<0||zeroBasedIndex>=ValueCount)return false;
            lock(gate)
            {
                if(!ready||sequence==lastReadSequence)return false;
                value=latest[zeroBasedIndex];lastReadSequence=sequence;
            }
            return !double.IsNaN(value)&&!double.IsInfinity(value);
        }

        public void Dispose()
        {
            disposed=true;
            lock(gate){client?.Close();client=null;}
            if(Thread.CurrentThread!=worker&&worker.IsAlive)worker.Join(250);
        }
    }

    public struct NfTraceRow    {
        public int frame; public double sampleTime, raw, clipped, level, greenHold;
        public bool green, revealed, postCue, readOk;
    }
    public struct DroppedFrameRow
    {
        public int frame; public double vblTime, missedBy;
    }

    public sealed class CsvLogger
    {
        const string TrialHeader="TrialNumber,TrialStart,C1PointDir,C1MoveDir,C2PointDir,C2MoveDir,Cue,CueOnsetTime,FirstGreenTime,ColorOnsetTime,CorrectResponse,ParticipantResponse,Accuracy,ReactionTime,RevealTimeout,ResponseTimeout,TrialEnd,DroppedFrameCount";
        const string TraceHeader="TrialNumber,FrameNumber,SampleTime,NFIndexUsed,NFValueRaw,NFValueClipped,NFLevel,InGreenZone,GreenHoldSec,ColorsRevealed,PostCueOnset,NFReadOk";
        const string DropHeader="TrialNumber,FrameNumber,VBLTime,MissedBySec";
        readonly string trialPath, tracePath, dropPath;
        static readonly CultureInfo Inv=CultureInfo.InvariantCulture;

        public CsvLogger(string root, string participant, string block)
        {
            Directory.CreateDirectory(root); string tag="p"+participant+"_b"+block;
            trialPath=Ensure(root,tag+"_leaves_trialdata.csv",TrialHeader);
            tracePath=Ensure(root,tag+"_leaves_nftrace.csv",TraceHeader);
            dropPath=Ensure(root,tag+"_leaves_droppedframes.csv",DropHeader);
        }
        static string Ensure(string root,string name,string header)
        {
            string p=Path.Combine(root,name);
            if (!File.Exists(p)) { File.WriteAllText(p,header+Environment.NewLine); return p; }
            string existing;
            using(var sr=new StreamReader(p)) existing=sr.ReadLine();
            if (string.Equals((existing??"").Trim(),header,StringComparison.Ordinal)) return p;
            string stem=Path.GetFileNameWithoutExtension(name), ext=Path.GetExtension(name);
            p=Path.Combine(root,stem+"_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+ext);
            File.WriteAllText(p,header+Environment.NewLine); return p;
        }
        static string F(double v) => double.IsNaN(v) ? "NaN" : v.ToString("F6",Inv);
        static int B(bool v)=>v?1:0;

        public void WriteTrial(int number,double start,TrialDefinition t,double cue,double firstGreen,double color,
            string participantResponse,bool accuracy,double rt,bool revealTimeout,bool responseTimeout,double end,int dropped)
        {
            string row=string.Join(",", number.ToString(Inv),F(start),DirectionUtil.Text(t.c1Point),DirectionUtil.Text(t.c1Move),
                DirectionUtil.Text(t.c2Point),DirectionUtil.Text(t.c2Move),t.cue==CueKind.C1?"c1":"c2",F(cue),F(firstGreen),F(color),
                DirectionUtil.Text(t.CorrectResponse),participantResponse,B(accuracy).ToString(Inv),F(rt),B(revealTimeout).ToString(Inv),
                B(responseTimeout).ToString(Inv),F(end),dropped.ToString(Inv));
            File.AppendAllText(trialPath,row+Environment.NewLine);
        }
        public void WriteTrace(int trial,int nfIndex,IList<NfTraceRow> rows)
        {
            var sb=new StringBuilder(rows.Count*100);
            foreach(var r in rows) sb.Append(trial).Append(',').Append(r.frame).Append(',').Append(F(r.sampleTime)).Append(',')
                .Append(nfIndex).Append(',').Append(F(r.raw)).Append(',').Append(F(r.clipped)).Append(',').Append(F(r.level)).Append(',')
                .Append(B(r.green)).Append(',').Append(F(r.greenHold)).Append(',').Append(B(r.revealed)).Append(',')
                .Append(B(r.postCue)).Append(',').Append(B(r.readOk)).AppendLine();
            File.AppendAllText(tracePath,sb.ToString());
        }
        public void WriteDropped(int trial,IList<DroppedFrameRow> rows)
        {
            var sb=new StringBuilder(rows.Count*50);
            foreach(var r in rows) sb.Append(trial).Append(',').Append(r.frame).Append(',').Append(F(r.vblTime)).Append(',').Append(F(r.missedBy)).AppendLine();
            File.AppendAllText(dropPath,sb.ToString());
        }
    }
}
