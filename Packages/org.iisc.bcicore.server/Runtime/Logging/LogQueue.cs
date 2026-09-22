// ============================================================================
//  LogQueue.cs — Lock-free FIFO that feeds the background log writer thread.
//
//  BinaryLogQueue<T> accepts raw items and a binary serialization action;
//  writing directly to a buffered FileStream with zero string conversion, zero
//  StringBuilder allocations, and zero GC pressure during acquisition.
//
//  LogQueue<T> and LogQueue (non-generic) are kept for backwards compatibility.
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace BciCore
{
    /// <summary>
    /// High-performance asynchronous binary log writer.
    /// Streams raw numeric records to disk with zero string formatting.
    /// </summary>
    internal sealed class BinaryLogQueue<T> : IDisposable
    {
        readonly BlockingCollection<T>   _q = new BlockingCollection<T>(65536);
        readonly FileStream              _fs;
        readonly BinaryWriter            _writer;
        readonly Thread                  _thread;
        readonly Action<BinaryWriter, T> _writeAction;

        public BinaryLogQueue(string path, Action<BinaryWriter, T> writeAction)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            _writer = new BinaryWriter(_fs);
            _writeAction = writeAction;

            _thread = new Thread(Run) { IsBackground = true, Name = "BciCore-binlog" };
            _thread.Start();
        }

        void Run()
        {
            try
            {
                foreach (var item in _q.GetConsumingEnumerable())
                {
                    _writeAction(_writer, item);
                    if (_q.Count == 0) _fs.Flush();
                }
            }
            catch { }
            finally
            {
                try { _fs.Flush(); _writer.Close(); } catch { }
            }
        }

        public void Enqueue(T item)
        {
            try { _q.TryAdd(item, 0); }
            catch { }
        }

        public int Count => _q.Count;

        public void Dispose()
        {
            _q.CompleteAdding();
            _thread.Join(3000);
        }
    }

    /// <summary>
    /// Asynchronous text/CSV log writer. Formatting runs on the background thread.
    /// </summary>
    internal sealed class LogQueue<T> : IDisposable
    {
        readonly BlockingCollection<T> _q = new BlockingCollection<T>(65536);
        readonly StreamWriter          _writer;
        readonly Thread                _thread;
        readonly Func<T, string>       _format;

        public LogQueue(string path, string header, Func<T, string> format)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _writer = new StreamWriter(path, append: false, System.Text.Encoding.UTF8, bufferSize: 1 << 16)
            {
                AutoFlush = false
            };
            _writer.WriteLine(header);
            _format = format;

            _thread = new Thread(Run) { IsBackground = true, Name = "BciCore-log" };
            _thread.Start();
        }

        void Run()
        {
            try
            {
                foreach (var item in _q.GetConsumingEnumerable())
                {
                    _writer.WriteLine(_format(item));
                    if (_q.Count == 0) _writer.Flush();
                }
            }
            catch { }
            finally
            {
                try { _writer.Flush(); _writer.Close(); } catch { }
            }
        }

        public void Enqueue(T item)
        {
            try { _q.TryAdd(item, 0); }
            catch { }
        }

        public int Count => _q.Count;

        public void Dispose()
        {
            _q.CompleteAdding();
            _thread.Join(3000);
        }
    }

    /// <summary>Non-generic convenience wrapper for pre-formatted strings.</summary>
    internal sealed class LogQueue : IDisposable
    {
        readonly LogQueue<string> _inner;

        public LogQueue(string path, string header)
            => _inner = new LogQueue<string>(path, header, s => s);

        public void Enqueue(string line) => _inner.Enqueue(line);
        public int  Count                => _inner.Count;
        public void Dispose()            => _inner.Dispose();
    }
}
