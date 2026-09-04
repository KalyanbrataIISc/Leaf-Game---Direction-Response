// ============================================================================
//  BciCoreTriggerSender.cs — Sends marker codes in-process via BciServer
//  instead of UDP to an external listener.
//
//  Drop-in replacement for TriggerSender: same Send(string name, int value)
//  signature. Markers are sent as a CMD frame (type 0x08) directly to the
//  connected ESP32.
// ============================================================================
using System;
using BciCore;
using UnityEngine;

namespace LeafGame
{
    public sealed class BciCoreTriggerSender : IDisposable
    {
        public void Send(string name, int value)
        {
            if (!BciServer.SendMarker(value))
                Debug.LogWarning($"[BciCore] Marker '{name}'={value} could not be sent (board not connected).");
        }

        public void Dispose() { }
    }
}
