// ============================================================================
//  BciCoreNfReader.cs — INfReader implementation that reads NF values from the
//  in-process BciServer instead of a TCP connection or a file.
//
//  Exact same shape as NfTcpReader — same polling semantics, same
//  "only fresh once per sample" behaviour — so LeafGameController.UpdateTrial()
//  needs no changes at all.
//
//  NfIndex mapping (matches TrialDefinition.NfIndex):
//    0 → smi_14gt18_shaped   (C1 / Pointing)
//    1 → smi_18gt14_shaped   (C2 / Moving)
// ============================================================================
using System;
using BciCore;

namespace LeafGame
{
    public sealed class BciCoreNfReader : INfReader
    {
        double _smi0, _smi1;
        long   _seq = -1, _lastRead = -1;
        readonly object _gate = new object();

        public BciCoreNfReader()
        {
            BciServer.OnNfSample += OnSample;
        }

        void OnSample(NfSample s)
        {
            lock (_gate)
            {
                bool isFirst = _seq < 0;
                _smi0 = s.Smi14gt18Shaped;
                _smi1 = s.Smi18gt14Shaped;
                _seq++;
                if (isFirst)
                    UnityEngine.Debug.Log(
                        $"[BciCore] First NF frame received — smi0(14>18_shaped)={_smi0:F4}, smi1(18>14_shaped)={_smi1:F4}. NF polling is active.");
            }
        }

        public bool   IsReady     => BciServer.State == ConnectionState.Connected;
        public string Error       => null;   // surface via BciServer.OnConnectionStateChanged if needed
        public string Description => "bcicore (in-process)";

        public bool TryRead(int zeroBasedIndex, out double value)
        {
            lock (_gate)
            {
                value = zeroBasedIndex == 0 ? _smi0 : _smi1;
                if (_seq == _lastRead) return false;
                _lastRead = _seq;
                return !double.IsNaN(value) && !double.IsInfinity(value);
            }
        }

        public void Dispose() => BciServer.OnNfSample -= OnSample;
    }
}
