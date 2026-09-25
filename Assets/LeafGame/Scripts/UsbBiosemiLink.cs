using System;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace LeafGame
{
    /// <summary>
    /// ADB reverse exposes the P-system bridge at tablet loopback. The two
    /// directions of this TCP socket are independent: newline-delimited numeric
    /// markers go to MATLAB; fixed 24-byte NF records come back from MATLAB.
    /// </summary>
    public sealed class UsbBiosemiLink : ITriggerSender, INfPairReader
    {
        const int RecordBytes=24;
        readonly object gate=new object();
        readonly int port, resetCode;
        readonly Thread worker;
        readonly byte[] record=new byte[RecordBytes];
        TcpClient client;
        NetworkStream stream;
        double first, second;
        long sequence, lastReadSequence=-1;
        bool ready, connected;
        volatile bool disposed;
        string error;

        public UsbBiosemiLink(int port,int resetCode)
        {
            if(port<1||port>65535)throw new ArgumentOutOfRangeException(nameof(port));
            this.port=port;this.resetCode=resetCode;
            worker=new Thread(Run){IsBackground=true,Name="Biosemi USB link"};
            worker.Start();
        }

        public bool IsReady{get{lock(gate)return ready;}}
        public string Error{get{lock(gate)return error;}}
        public string Description=>$"USB Biosemi bridge 127.0.0.1:{port}";

        void Run()
        {
            while(!disposed)
            {
                TcpClient candidate=null;
                try
                {
                    candidate=new TcpClient{NoDelay=true};
                    candidate.Connect("127.0.0.1",port);
                    candidate.SendTimeout=100;
                    var candidateStream=candidate.GetStream();
                    candidateStream.WriteTimeout=100;
                    lock(gate)
                    {
                        if(disposed){candidate.Dispose();return;}
                        client=candidate;stream=candidateStream;connected=true;error=null;
                    }
                    // Reset the marker line on every new USB session, including
                    // a replug before the first game trial.
                    SendRaw(resetCode);
                    while(!disposed)
                    {
                        ReadExactly(candidateStream,record);
                        double a=ReadDouble(record,0),b=ReadDouble(record,8);
                        double sampleCount=ReadDouble(record,16);
                        if(double.IsNaN(a)||double.IsInfinity(a)||double.IsNaN(b)||double.IsInfinity(b))continue;
                        lock(gate){first=a;second=b;sequence++;ready=true;}
                        if(!double.IsNaN(sampleCount)&&!double.IsInfinity(sampleCount))
                            SendControl("R "+sampleCount.ToString("R",CultureInfo.InvariantCulture)+"\n");
                    }
                }
                catch(Exception e)
                {
                    lock(gate)
                    {
                        if(!disposed && connected)error="USB bridge disconnected: "+e.Message;
                        ready=false;
                    }
                    // A disconnect during a block must remain visible to the
                    // controller; reconnect only by starting a new session.
                    if(connected)break;
                }
                finally
                {
                    lock(gate)
                    {
                        if(ReferenceEquals(client,candidate)){stream=null;client=null;connected=false;}
                    }
                    candidate?.Dispose();
                }
                if(!disposed)Thread.Sleep(250);
            }
        }

        static void ReadExactly(NetworkStream source,byte[] buffer)
        {
            int offset=0;
            while(offset<buffer.Length)
            {
                int count=source.Read(buffer,offset,buffer.Length-offset);
                if(count<=0)throw new EndOfStreamException("The MATLAB bridge closed the connection.");
                offset+=count;
            }
        }

        static double ReadDouble(byte[] buffer,int offset)
        {
            if(BitConverter.IsLittleEndian)return BitConverter.ToDouble(buffer,offset);
            byte[] copy=new byte[8];Array.Copy(buffer,offset,copy,0,8);Array.Reverse(copy);
            return BitConverter.ToDouble(copy,0);
        }

        void SendRaw(int value)
        {
            SendControl(value.ToString(CultureInfo.InvariantCulture)+"\n");
        }

        void SendControl(string line)
        {
            byte[] bytes=Encoding.ASCII.GetBytes(line);
            lock(gate)
            {
                if(stream==null)throw new IOException("The USB bridge is not connected.");
                stream.Write(bytes,0,bytes.Length);
            }
        }

        public void Send(string name,int value)
        {
            if(value<0||value>255)throw new ArgumentOutOfRangeException(nameof(value),"Biosemi marker must fit in one byte.");
            // The connection worker already sent reset at connection time.
            if(name=="reset")return;
            try{SendRaw(value);}
            catch(Exception e)
            {
                lock(gate)error="Could not send marker "+value+": "+e.Message;
                Debug.LogError("[Biosemi USB] "+error);
            }
        }

        public bool TryRead(int zeroBasedIndex,out double value)
        {
            lock(gate)
            {
                value=zeroBasedIndex==0?first:second;
                if(!ready||sequence==lastReadSequence||zeroBasedIndex<0||zeroBasedIndex>1)return false;
                lastReadSequence=sequence;
                return true;
            }
        }

        public bool TryReadPair(out double firstValue,out double secondValue)
        {
            lock(gate)
            {
                firstValue=first;secondValue=second;
                if(!ready||sequence==lastReadSequence)return false;
                lastReadSequence=sequence;
                return true;
            }
        }

        public void Dispose()
        {
            disposed=true;
            lock(gate)
            {
                try
                {
                    if(stream!=null)
                    {
                        byte[] goodbye=Encoding.ASCII.GetBytes("BYE\n");
                        stream.Write(goodbye,0,goodbye.Length);
                    }
                }
                catch { }
                client?.Close();stream=null;client=null;ready=false;
            }
            if(Thread.CurrentThread!=worker&&worker.IsAlive)worker.Join(250);
        }
    }
}
