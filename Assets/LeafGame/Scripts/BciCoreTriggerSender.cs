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
    public sealed class BciCoreTriggerSender : ITriggerSender
    {
        readonly TriggerSender _udpFallback;

        public BciCoreTriggerSender(string host = null, int port = 0)
        {
            if (!string.IsNullOrEmpty(host) && port > 0)
                _udpFallback = new TriggerSender(host, port);
        }

        public void Send(string name, int value)
        {
            // 1. Log the trigger immediately on the software side (independent of firmware echo)
            BciServer.LogTriggerSent(name, value);

            // 2. Send in-process CMD frame over TCP directly to ESP32 board
            bool sent = BciServer.SendMarker(value);
            if (!sent)
                Debug.LogWarning($"[BciCore] Marker '{name}'={value} could not be sent via TCP (board not connected).");

            // 3. Send UDP as fallback if configured
            _udpFallback?.Send(name, value);
        }

        public void Dispose()
        {
            _udpFallback?.Dispose();
        }
    }
}
