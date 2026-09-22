// ============================================================================
//  BinToCsvConverter.cs — High-speed offline / post-session converter.
//  Converts compact binary streams (.bin) to standard research CSV format.
//
//  Binary record formats (firmware-aligned, no 9-byte packet framing):
//    • raw.bin      (134 B): uint32 seq, int32[32] counts, ushort marker
//    • signals.bin  (134 B): uint32 seq, float32[32] uv,   ushort marker
//    • health.bin   (74 B) : int32 wall_sec, uint32 ms, uint32 seq, uint32 drops,
//                            uint32 gaps, uint32 bad, uint32 miss, uint32 dspmax,
//                            uint32 srv_bad, uint32 heap, ushort marker, char[32] evt
//    • features.bin (112 B): byte is_cca, byte reserved, ushort marker, uint32 seq,
//                            float[10] (scores/smi/nf), float[16] alpha
// ============================================================================
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace BciCore
{
    public static class BinToCsvConverter
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public const int RawRecordBytes      = 4 + 32 * 4 + 2;   // 134 bytes
        public const int SignalRecordBytes   = 4 + 32 * 4 + 2;   // 134 bytes
        public const int HealthRecordBytes   = 4 * 10 + 2 + 32;  // 74 bytes
        public const int FeatureRecordBytes  = 1 + 1 + 2 + 4 + 10 * 4 + 16 * 4; // 112 bytes

        /// <summary>
        /// Converts all .bin files in a session directory to their corresponding .csv files.
        /// Runs asynchronously off the main thread.
        /// </summary>
        public static async Task ConvertSessionAsync(string sessionDir, Action<float, string> onProgress = null)
        {
            if (string.IsNullOrEmpty(sessionDir) || !Directory.Exists(sessionDir)) return;

            await Task.Run(() =>
            {
                try
                {
                    string[] binFiles = Directory.GetFiles(sessionDir, "eeg_*_raw.bin");
                    if (binFiles.Length > 0)
                    {
                        foreach (string rawBin in binFiles)
                        {
                            string rawCsv = Path.ChangeExtension(rawBin, ".csv");
                            onProgress?.Invoke(0.1f, "Converting raw data to CSV...");
                            ConvertRawBinToCsv(rawBin, rawCsv, p => onProgress?.Invoke(0.1f + p * 0.45f, "Converting raw data..."));
                        }
                    }

                    binFiles = Directory.GetFiles(sessionDir, "eeg_*_signals.bin");
                    if (binFiles.Length > 0)
                    {
                        foreach (string sigBin in binFiles)
                        {
                            string sigCsv = Path.ChangeExtension(sigBin, ".csv");
                            onProgress?.Invoke(0.6f, "Converting processed signals to CSV...");
                            ConvertSignalsBinToCsv(sigBin, sigCsv, p => onProgress?.Invoke(0.6f + p * 0.20f, "Converting signals..."));
                        }
                    }

                    binFiles = Directory.GetFiles(sessionDir, "eeg_*_features.bin");
                    if (binFiles.Length > 0)
                    {
                        foreach (string featBin in binFiles)
                        {
                            string featCsv = Path.ChangeExtension(featBin, ".csv");
                            onProgress?.Invoke(0.82f, "Converting features to CSV...");
                            ConvertFeaturesBinToCsv(featBin, featCsv);
                        }
                    }

                    binFiles = Directory.GetFiles(sessionDir, "eeg_*_health.bin");
                    if (binFiles.Length > 0)
                    {
                        foreach (string hlthBin in binFiles)
                        {
                            string hlthCsv = Path.ChangeExtension(hlthBin, ".csv");
                            onProgress?.Invoke(0.92f, "Converting health log to CSV...");
                            ConvertHealthBinToCsv(hlthBin, hlthCsv);
                        }
                    }

                    onProgress?.Invoke(1.0f, "Complete!");
                }
                catch (Exception e)
                {
                    Debug.LogError($"[BciCore] CSV conversion failed: {e.Message}\n{e.StackTrace}");
                }
            });
        }

        public static void ConvertRawBinToCsv(string binPath, string csvPath, Action<float> progress = null, int numCh = 32)
        {
            if (!File.Exists(binPath)) return;
            var fi = new FileInfo(binPath);
            long totalBytes = fi.Length;
            if (totalBytes < RawRecordBytes) return;

            using var fs = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            using var br = new BinaryReader(fs);
            using var sw = new StreamWriter(new FileStream(csvPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16), Encoding.UTF8);

            // Header
            sw.Write("seq");
            for (int c = 1; c <= numCh; c++) sw.Write($",raw{c}");
            sw.WriteLine(",marker");

            var sb = new StringBuilder(256);
            long readBytes = 0;
            long lastReport = 0;

            while (fs.Position + RawRecordBytes <= totalBytes)
            {
                uint seq = br.ReadUInt32();
                sb.Clear().Append(seq);

                for (int c = 0; c < 32; c++)
                {
                    int count = br.ReadInt32();
                    if (c < numCh) sb.Append(',').Append(count);
                }
                ushort marker = br.ReadUInt16();
                sb.Append(',').Append(marker);
                sw.WriteLine(sb.ToString());

                readBytes += RawRecordBytes;
                if (progress != null && readBytes - lastReport >= (RawRecordBytes * 500))
                {
                    lastReport = readBytes;
                    progress((float)readBytes / totalBytes);
                }
            }
            sw.Flush();
            progress?.Invoke(1.0f);
        }

        public static void ConvertSignalsBinToCsv(string binPath, string csvPath, Action<float> progress = null, int numCh = 32)
        {
            if (!File.Exists(binPath)) return;
            var fi = new FileInfo(binPath);
            long totalBytes = fi.Length;
            if (totalBytes < SignalRecordBytes) return;

            using var fs = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            using var br = new BinaryReader(fs);
            using var sw = new StreamWriter(new FileStream(csvPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16), Encoding.UTF8);

            // Header
            sw.Write("seq");
            for (int c = 1; c <= numCh; c++) sw.Write($",filt{c}");
            sw.WriteLine(",marker");

            var sb = new StringBuilder(256);
            long readBytes = 0;
            long lastReport = 0;

            while (fs.Position + SignalRecordBytes <= totalBytes)
            {
                uint seq = br.ReadUInt32();
                sb.Clear().Append(seq);

                for (int c = 0; c < 32; c++)
                {
                    float val = br.ReadSingle();
                    if (c < numCh) sb.Append(',').Append(val.ToString("F6", Inv));
                }
                ushort marker = br.ReadUInt16();
                sb.Append(',').Append(marker);
                sw.WriteLine(sb.ToString());

                readBytes += SignalRecordBytes;
                if (progress != null && readBytes - lastReport >= (SignalRecordBytes * 500))
                {
                    lastReport = readBytes;
                    progress((float)readBytes / totalBytes);
                }
            }
            sw.Flush();
            progress?.Invoke(1.0f);
        }

        public static void ConvertHealthBinToCsv(string binPath, string csvPath)
        {
            if (!File.Exists(binPath)) return;
            var fi = new FileInfo(binPath);
            long totalBytes = fi.Length;
            if (totalBytes < HealthRecordBytes) return;

            using var fs = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            using var br = new BinaryReader(fs);
            using var sw = new StreamWriter(new FileStream(csvPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16), Encoding.UTF8);

            sw.WriteLine("wall_clock,board_ms,seq,board_drops,pc_gaps,board_bad,board_miss,board_dspmax_us,srv_bad,free_heap,marker,event");

            byte[] evtBuf = new byte[32];
            while (fs.Position + HealthRecordBytes <= totalBytes)
            {
                int wallSec = br.ReadInt32();
                uint boardMs = br.ReadUInt32();
                uint seq = br.ReadUInt32();
                uint drops = br.ReadUInt32();
                uint gaps = br.ReadUInt32();
                uint bad = br.ReadUInt32();
                uint miss = br.ReadUInt32();
                uint dspMax = br.ReadUInt32();
                uint srvBad = br.ReadUInt32();
                uint freeHeap = br.ReadUInt32();
                ushort marker = br.ReadUInt16();
                br.Read(evtBuf, 0, 32);

                int nullIdx = Array.IndexOf(evtBuf, (byte)0);
                int evtLen = nullIdx >= 0 ? nullIdx : 32;
                string evtStr = Encoding.UTF8.GetString(evtBuf, 0, evtLen);

                int h = (wallSec / 3600) % 24;
                int m = (wallSec / 60) % 60;
                int s = wallSec % 60;
                string clockStr = $"{h:D2}:{m:D2}:{s:D2}";

                sw.WriteLine($"{clockStr},{boardMs},{seq},{drops},{gaps},{bad},{miss},{dspMax},{srvBad},{freeHeap},{marker},{evtStr}");
            }
            sw.Flush();
        }

        public static void ConvertFeaturesBinToCsv(string binPath, string csvPath, int analysisCh = 16)
        {
            if (!File.Exists(binPath)) return;
            var fi = new FileInfo(binPath);
            long totalBytes = fi.Length;
            if (totalBytes < FeatureRecordBytes) return;

            using var fs = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            using var br = new BinaryReader(fs);
            using var sw = new StreamWriter(new FileStream(csvPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16), Encoding.UTF8);

            bool headerWritten = false;
            var sb = new StringBuilder(256);

            while (fs.Position + FeatureRecordBytes <= totalBytes)
            {
                byte isCca = br.ReadByte();
                br.ReadByte(); // reserved
                ushort marker = br.ReadUInt16();
                uint seq = br.ReadUInt32();

                float v1 = br.ReadSingle();
                float v2 = br.ReadSingle();
                float v3 = br.ReadSingle();
                float v4 = br.ReadSingle();
                float aNf = br.ReadSingle();
                float sNf = br.ReadSingle();
                float aLeft = br.ReadSingle();
                float aRight = br.ReadSingle();
                float sRight14 = br.ReadSingle();
                float sLeft18 = br.ReadSingle();

                if (!headerWritten)
                {
                    if (isCca == 1)
                    {
                        sw.Write("seq,marker,fb_AgtB,fb_BgtA,score_A,score_B,alphaNF,ssvepNF,alphaLeft,alphaRight,ssvepRight14,ssvepLeft18");
                    }
                    else
                    {
                        sw.Write("seq,marker,smi_14gt18,smi_18gt14,smi_14gt18_shaped,smi_18gt14_shaped,alphaNF,ssvepNF,alphaLeft,alphaRight,ssvepRight14,ssvepLeft18");
                    }
                    for (int c = 1; c <= analysisCh; c++) sw.Write($",alpha{c}");
                    sw.WriteLine();
                    headerWritten = true;
                }

                sb.Clear()
                  .Append(seq).Append(',')
                  .Append(marker).Append(',')
                  .Append(v1.ToString("F6", Inv)).Append(',')
                  .Append(v2.ToString("F6", Inv)).Append(',')
                  .Append(v3.ToString("F6", Inv)).Append(',')
                  .Append(v4.ToString("F6", Inv)).Append(',')
                  .Append(aNf.ToString("F6", Inv)).Append(',')
                  .Append(sNf.ToString("F6", Inv)).Append(',')
                  .Append(aLeft.ToString("F6", Inv)).Append(',')
                  .Append(aRight.ToString("F6", Inv)).Append(',')
                  .Append(sRight14.ToString("F6", Inv)).Append(',')
                  .Append(sLeft18.ToString("F6", Inv));

                for (int c = 0; c < 16; c++)
                {
                    float alphaVal = br.ReadSingle();
                    if (c < analysisCh) sb.Append(',').Append(alphaVal.ToString("F6", Inv));
                }
                sw.WriteLine(sb.ToString());
            }
            sw.Flush();
        }
    }
}
