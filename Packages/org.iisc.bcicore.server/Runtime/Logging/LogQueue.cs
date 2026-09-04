// ============================================================================
//  LogQueue.cs — Lock-free FIFO that feeds the background CSV writer thread.
//
//  The process thread enqueues pre-formatted line strings; a dedicated writer
//  thread dequeues and appends to StreamWriters without ever blocking the
//  network path.
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace BciCore
{
    /// <summary>
    /// Lightweight asynchronous log writer. Open one instance per log file;
    /// call Enqueue() from any thread; call Flush()/Close() when done.
    /// </summary>
    internal sealed class LogQueue : IDisposable
    {
        readonly BlockingCollection<string> _q = new BlockingCollection<string>(65536);
        readonly StreamWriter               _writer;
        readonly Thread                     _thread;

        public LogQueue(string path, string header)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _writer = new StreamWriter(path, append: false, System.Text.Encoding.UTF8, bufferSize: 1 << 16)
            {
                AutoFlush = false
            };
            _writer.WriteLine(header);

            _thread = new Thread(Run) { IsBackground = true, Name = "BciCore-log" };
            _thread.Start();
        }

        void Run()
        {
            try
            {
                foreach (var line in _q.GetConsumingEnumerable())
                {
                    _writer.WriteLine(line);
                    if (_q.Count == 0) _writer.Flush();   // flush when idle
                }
            }
            catch { }
            finally
            {
                _writer.Flush();
                _writer.Close();
            }
        }

        /// <summary>Enqueue a single pre-formatted log line (no newline needed).</summary>
        public void Enqueue(string line)
        {
            try { _q.TryAdd(line, 0); }
            catch { }
        }

        public void Dispose()
        {
            _q.CompleteAdding();
            _thread.Join(3000);
        }
    }
}
