// ============================================================================
//  GameFanoutListener.cs — Dedicated TCP server relaying live NF/CCA records
//  to connected game clients (e.g. LeafGame's NfTcpReader or external games).
//
//  Listens on port 5006 (or configured port) and broadcasts 24-byte records:
//    [0..7]  : double fb_AgtB   (LE)
//    [8..15] : double fb_BgtA   (LE)
//    [16..23]: double sampleCount (LE)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace BciCore
{
    public sealed class GameFanoutListener : IDisposable
    {
        readonly int _port;
        readonly object _lock = new object();
        readonly List<TcpClient> _clients = new List<TcpClient>();
        TcpListener _listener;
        Thread _acceptThread;
        volatile bool _disposed;

        public int ConnectedClientsCount
        {
            get { lock (_lock) return _clients.Count; }
        }

        public GameFanoutListener(int port)
        {
            _port = port;
            try
            {
                _listener = new TcpListener(IPAddress.Any, _port);
                _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _listener.Start(5);
                _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "BciCore-FanoutAccept" };
                _acceptThread.Start();
                Debug.Log($"[BciCore] Game fan-out TCP server listening on port {_port}. (Game connects here as client)");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BciCore] Failed to start game fan-out server on port {_port}: {ex.Message}");
            }
        }

        void AcceptLoop()
        {
            while (!_disposed && _listener != null)
            {
                try
                {
                    var client = _listener.AcceptTcpClient();
                    client.NoDelay = true;
                    lock (_lock)
                    {
                        if (_disposed)
                        {
                            client.Dispose();
                            return;
                        }
                        _clients.Add(client);
                    }
                    var ep = client.Client.RemoteEndPoint;
                    Debug.Log($"[BciCore] Game client connected to NF fan-out port {_port} from {ep}");
                }
                catch (SocketException) when (_disposed) { break; }
                catch (ObjectDisposedException) when (_disposed) { break; }
                catch (Exception ex)
                {
                    if (!_disposed)
                    {
                        Debug.LogWarning($"[BciCore] Fan-out accept error: {ex.Message}");
                        Thread.Sleep(500);
                    }
                }
            }
        }

        /// <summary>
        /// Broadcasts a 24-byte record (3 LE doubles: [v0, v1, v2]) to all connected game clients.
        /// </summary>
        public void Broadcast(double v0, double v1, double v2)
        {
            byte[] pkt = FrameProtocol.BuildGameFanoutRecord(v0, v1, v2);
            lock (_lock)
            {
                if (_disposed || _clients.Count == 0) return;

                List<TcpClient> dead = null;
                for (int i = 0; i < _clients.Count; i++)
                {
                    var c = _clients[i];
                    try
                    {
                        var stream = c.GetStream();
                        stream.Write(pkt, 0, pkt.Length);
                    }
                    catch
                    {
                        if (dead == null) dead = new List<TcpClient>();
                        dead.Add(c);
                    }
                }

                if (dead != null)
                {
                    for (int i = 0; i < dead.Count; i++)
                    {
                        var c = dead[i];
                        _clients.Remove(c);
                        try { c.Dispose(); } catch { }
                    }
                }
            }
        }

        public void Dispose()
        {
            _disposed = true;
            try { _listener?.Stop(); } catch { }
            _listener = null;

            lock (_lock)
            {
                foreach (var c in _clients)
                {
                    try { c.Dispose(); } catch { }
                }
                _clients.Clear();
            }
        }
    }
}
