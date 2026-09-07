// ============================================================================
//  FftAnalyzer.cs — Single-sided amplitude spectrum via radix-2 FFT.
//
//  Operates entirely on the main thread at a low update rate (~4 Hz) so
//  performance is not critical. Inputs come from ChannelRingBuffer snapshots.
//
//  Usage:
//    float[] spectrum = FftAnalyzer.ComputeSpectrum(samples, sampleRate, out float[] freqAxis);
// ============================================================================
using System;

namespace BciCore
{
    public static class FftAnalyzer
    {
        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>
        /// Compute single-sided amplitude spectrum from <paramref name="samples"/>.
        /// Length of samples must be a power of two (or will be zero-padded to next power of two).
        /// </summary>
        /// <param name="samples">Time-domain signal (will be windowed with Hann internally).</param>
        /// <param name="sampleRateHz">Sampling rate in Hz — used to build the frequency axis.</param>
        /// <param name="freqAxis">Output frequency axis (length = N/2 + 1).</param>
        /// <returns>Amplitude spectrum (µV rms), length = N/2 + 1.</returns>
        public static float[] ComputeSpectrum(float[] samples, float sampleRateHz, out float[] freqAxis)
        {
            if (samples == null || samples.Length < 2)
            {
                freqAxis = new float[1];
                return new float[1];
            }

            int n = NextPowerOfTwo(samples.Length);
            var re = new double[n];
            var im = new double[n];

            // Hann window + copy (zero-pad if n > samples.Length)
            int copyLen = Math.Min(samples.Length, n);
            for (int i = 0; i < copyLen; i++)
            {
                double w = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (copyLen - 1));
                re[i] = samples[i] * w;
            }

            Fft(re, im);

            int bins = n / 2 + 1;
            var mag   = new float[bins];
            freqAxis  = new float[bins];
            double norm = 2.0 / n;   // single-sided normalisation

            for (int k = 0; k < bins; k++)
            {
                freqAxis[k] = k * sampleRateHz / n;
                double m = Math.Sqrt(re[k] * re[k] + im[k] * im[k]) * norm;
                if (k == 0 || k == bins - 1) m *= 0.5;   // DC and Nyquist halved
                mag[k] = (float)m;
            }

            return mag;
        }

        /// <summary>
        /// Real-time single-sided power spectrum (µV²) with mean subtraction, linear detrend,
        /// and zeroed DC bin, precisely matching get_fft() in bci_gui_v2.py.
        /// </summary>
        public static float[] ComputeRealtimePowerSpectrum(float[] samples, int count, float sampleRateHz, out float[] freqAxis)
        {
            if (samples == null || count < 2)
            {
                freqAxis = new float[1];
                return new float[1];
            }

            int len = Math.Min(count, samples.Length);
            int n = NextPowerOfTwo(len);
            var re = new double[n];
            var im = new double[n];

            // 1. Mean subtraction over the valid len samples
            double sumX = 0;
            for (int i = 0; i < len; i++) sumX += samples[i];
            double meanX = sumX / len;

            // 2. Linear detrend: polyfit(t, seg, 1) -> y = slope * t + intercept
            double meanT = (len - 1) * 0.5;
            double covTX = 0;
            double varT = len * (len * (double)len - 1.0) / 12.0;

            for (int i = 0; i < len; i++)
            {
                double dt = i - meanT;
                double dx = samples[i] - meanX;
                covTX += dt * dx;
            }

            double slope = (varT > 1e-12) ? (covTX / varT) : 0.0;
            double intercept = meanX - slope * meanT;

            for (int i = 0; i < len; i++)
            {
                re[i] = samples[i] - (intercept + slope * i);
            }

            // Zero-pad to next power of two
            for (int i = len; i < n; i++) re[i] = 0;

            // 3. FFT
            Fft(re, im);

            int bins = n / 2 + 1;
            var pwr = new float[bins];
            freqAxis = new float[bins];

            // 4. Power spectrum P1 = 2 * (|Y| / n)^2, with DC bin pwr[0] = 0
            double invN2 = 1.0 / ((double)n * n);

            for (int k = 0; k < bins; k++)
            {
                freqAxis[k] = k * sampleRateHz / n;
                double p = (re[k] * re[k] + im[k] * im[k]) * invN2;
                if (k > 0 && k < bins - 1) p *= 2.0;
                pwr[k] = (float)p;
            }

            pwr[0] = 0f; // zero DC bin (bci_gui_v2.py: pwr[0] = 0)
            return pwr;
        }

        // ── Cooley–Tukey in-place radix-2 FFT (DIT) ─────────────────────────

        static void Fft(double[] re, double[] im)
        {
            int n = re.Length;
            // Bit-reversal permutation
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { Swap(ref re[i], ref re[j]); Swap(ref im[i], ref im[j]); }
            }
            // FFT butterfly
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2.0 * Math.PI / len;
                double wRe = Math.Cos(ang), wIm = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double uRe = 1.0, uIm = 0.0;
                    for (int j = 0; j < len / 2; j++)
                    {
                        int    p = i + j, q = p + len / 2;
                        double tRe = uRe * re[q] - uIm * im[q];
                        double tIm = uRe * im[q] + uIm * re[q];
                        re[q] = re[p] - tRe; im[q] = im[p] - tIm;
                        re[p] += tRe;        im[p] += tIm;
                        double nRe = uRe * wRe - uIm * wIm;
                        uIm = uRe * wIm + uIm * wRe;
                        uRe = nRe;
                    }
                }
            }
        }

        static void Swap(ref double a, ref double b) { double t = a; a = b; b = t; }

        static int NextPowerOfTwo(int n)
        {
            int p = 1;
            while (p < n) p <<= 1;
            return p;
        }
    }
}
