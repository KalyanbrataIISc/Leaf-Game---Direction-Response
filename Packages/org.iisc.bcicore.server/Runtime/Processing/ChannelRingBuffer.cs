// ============================================================================
//  ChannelRingBuffer.cs — Thread-safe ring buffer storing the last N samples
//  per channel. Written by the process thread; read by the main thread for
//  calibration waveform plots.
//
//  Single-producer (process thread) / single-consumer (main thread) per channel.
//  Uses a simple lock per channel array write, sized for 250 SPS × 10 sec = 2500 samples.
// ============================================================================
using System;

namespace BciCore
{
    public sealed class ChannelRingBuffer
    {
        const int DefaultWindowSamples = 2500;   // 10 s at 250 SPS

        float[][] _data;
        int[]     _head;
        int       _cap;
        int       _numCh;
        readonly object _lock = new object();

        public int NumChannels => _numCh;
        public int Capacity    => _cap;

        public ChannelRingBuffer(int numCh = 32, int windowSamples = DefaultWindowSamples)
        {
            Resize(numCh, windowSamples);
        }

        /// <summary>Resize (reinitialises, drops existing data).</summary>
        public void Resize(int numCh, int windowSamples = DefaultWindowSamples)
        {
            lock (_lock)
            {
                _numCh = Math.Max(1, numCh);
                _cap   = Math.Max(2, windowSamples);
                _data  = new float[_numCh][];
                _head  = new int[_numCh];
                for (int c = 0; c < _numCh; c++)
                    _data[c] = new float[_cap];
            }
        }

        /// <summary>Write one sample for every channel (called from process thread).</summary>
        public void Write(float[] samples)
        {
            if (samples == null) return;
            int n = Math.Min(samples.Length, _numCh);
            lock (_lock)
            {
                for (int c = 0; c < n; c++)
                {
                    _data[c][_head[c]] = samples[c];
                    _head[c] = (_head[c] + 1) % _cap;
                }
            }
        }

        /// <summary>Write a single sample for one channel.</summary>
        public void WriteSingle(int channel, float value)
        {
            if (channel < 0 || channel >= _numCh) return;
            lock (_lock)
            {
                _data[channel][_head[channel]] = value;
                _head[channel] = (_head[channel] + 1) % _cap;
            }
        }

        /// <summary>
        /// Copy the ring buffer for the given channel into dest[] in chronological order.
        /// dest must be at least Capacity elements.
        /// Returns the number of elements copied.
        /// </summary>
        public int ReadAll(int channel, float[] dest)
        {
            if (channel < 0 || channel >= _numCh || dest == null) return 0;
            lock (_lock)
            {
                int h = _head[channel];
                float[] src = _data[channel];
                int n = Math.Min(dest.Length, _cap);
                // oldest sample is at _head; copy wrap-around
                for (int i = 0; i < n; i++)
                    dest[i] = src[(h + i) % _cap];
                return n;
            }
        }

        /// <summary>
        /// Read the last `count` samples (most recent) for `channel` into dest[].
        /// Returns number of samples copied.
        /// </summary>
        public int ReadRecent(int channel, float[] dest, int count)
        {
            if (channel < 0 || channel >= _numCh || dest == null) return 0;
            count = Math.Min(count, Math.Min(dest.Length, _cap));
            lock (_lock)
            {
                int h = _head[channel];
                float[] src = _data[channel];
                // newest sample is at h-1, working backwards
                int start = ((h - count) % _cap + _cap) % _cap;
                for (int i = 0; i < count; i++)
                    dest[i] = src[(start + i) % _cap];
                return count;
            }
        }
    }
}
