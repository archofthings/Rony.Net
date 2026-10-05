using Rony.Models;
using System;
using System.IO;
using System.Text;

namespace Rony.Cli
{
    /// <summary>
    /// The <c>--journal</c> file: every received request appended as one line of JSON and flushed at once. Writes are serialized;
    /// a failing write is reported once and stops the journal without affecting the server.
    /// </summary>
    internal sealed class RequestJournalFile : IDisposable
    {
        private readonly object _lock = new object();
        private readonly Sink _sink;
        private readonly string _path;
        private readonly bool _created;
        private Stream _stream;

        internal RequestJournalFile(Stream stream, Sink sink, string path = null, bool created = false)
        {
            _stream = stream;
            _sink = sink;
            _path = path;
            _created = created;
        }

        /// <summary>Opens the file named by <c>--journal</c> for appending; null when the option is not given.</summary>
        public static RequestJournalFile Open(CommandLine line, Sink sink)
        {
            if (!line.Has("journal", out var path)) return null;
            var existed = File.Exists(path);
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                return new RequestJournalFile(new FileStream(path, options), sink, path, !existed);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException
                                              || exception is ArgumentException || exception is NotSupportedException)
            {
                throw new InputException($"Cannot write {path}: {exception.Message}");
            }
        }

        /// <summary>Appends the request; handler of <see cref="MockServer.RequestReceived"/>.</summary>
        public void Write(object sender, ReceivedRequest request)
        {
            lock (_lock)
            {
                if (_stream == null) return;
                try
                {
                    var bytes = new UTF8Encoding(false).GetBytes(request.ToJson() + "\n");
                    _stream.Write(bytes, 0, bytes.Length);
                    _stream.Flush();
                }
                catch (Exception exception)
                {
                    var failed = _stream;
                    _stream = null;
                    try
                    {
                        failed.Dispose();
                    }
                    catch (Exception)
                    {
                        // Disposing flushes again and fails again; the warning below names the original failure.
                    }

                    try
                    {
                        _sink.Error.WriteLine("Warning: writing the journal failed, journaling stopped: " + exception.Message);
                    }
                    catch (Exception)
                    {
                        // Reporting must not break the server either.
                    }
                }
            }
        }

        /// <summary>Closes the journal and deletes the file when this run created it and nothing was written, so a failed start leaves nothing behind.</summary>
        public void Abandon()
        {
            lock (_lock)
            {
                var length = -1L;
                try
                {
                    length = _stream?.Length ?? -1L;
                }
                catch (Exception)
                {
                    // Unknown length: leave the file.
                }

                _stream?.Dispose();
                _stream = null;
                if (_created && length == 0) TryDelete();
            }
        }

        private void TryDelete()
        {
            try
            {
                File.Delete(_path);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                // Best effort cleanup.
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _stream?.Dispose();
                _stream = null;
            }
        }
    }
}
