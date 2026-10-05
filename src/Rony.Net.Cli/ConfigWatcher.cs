using Rony.Net;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Rony.Cli
{
    /// <summary>
    /// The <c>--watch</c> option: polls the configuration file and reloads the rules of the server when its text changes.
    /// Polling (not <see cref="FileSystemWatcher"/>) because file system events are unreliable on bind mounts and with editors
    /// that replace the file. Reloads run one after the other on a single task.
    /// </summary>
    internal sealed class ConfigWatcher : IDisposable
    {
        /// <summary>Time between two checks of the file; tests make it short.</summary>
        internal static TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(500);

        private readonly MockServer _server;
        private readonly string _path;
        private readonly Sink _sink;
        private readonly TimeSpan _interval;
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private string _lastText;
        private string _candidate;
        private Task _loop;
        private bool _reportedUnexpected;

        /// <summary>Creates a watcher; <paramref name="baselineText"/> is the text the server was loaded from (null when it could not be read).</summary>
        public ConfigWatcher(MockServer server, string path, string baselineText, Sink sink)
        {
            _server = server;
            _path = path;
            _lastText = baselineText;
            _sink = sink;
            _interval = DefaultInterval;
        }

        /// <summary>Reads the text of a file; null when it cannot be read at this moment.</summary>
        public static string TryRead(string path)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Starts polling.</summary>
        public void Start() => _loop = Task.Run(() => LoopAsync(_cancellation.Token));

        /// <summary>Stops polling and waits until the polling task has ended.</summary>
        public async Task StopAsync()
        {
            _cancellation.Cancel();
            if (_loop != null) await _loop.ConfigureAwait(false);
            _loop = null;
        }

        private async Task LoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_interval, token).ConfigureAwait(false);
                    CheckOnce();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    if (!_reportedUnexpected)
                    {
                        _reportedUnexpected = true;
                        Report($"{_path}: watching failed unexpectedly: {exception.Message}");
                    }
                }
            }
        }

        private void CheckOnce()
        {
            var text = TryRead(_path);
            if (string.IsNullOrWhiteSpace(text) || text == _lastText)
            {
                _candidate = null;
                return;
            }

            // A change is loaded only when the same text was read twice in a row, so a file that is still being written is not loaded.
            if (text != _candidate)
            {
                _candidate = text;
                return;
            }

            // Remembered before the attempt, so the same broken content is reported once.
            _lastText = text;
            _candidate = null;
            try
            {
                _server.ReloadJson(text, Path.GetDirectoryName(Path.GetFullPath(_path)));
                _sink.WriteLine($"Reloaded {_path}");
            }
            catch (Exception exception) when (exception is FormatException || exception is IOException
                || exception is UnauthorizedAccessException || exception is ArgumentException)
            {
                Report($"{_path}: {exception.Message}; keeping the previous rules.");
            }
        }

        private void Report(string message)
        {
            try
            {
                _sink.Error.WriteLine(message);
            }
            catch (Exception)
            {
                // A closed writer must not stop the watcher.
            }
        }

        /// <summary>Releases the cancellation source; call <see cref="StopAsync"/> first to stop the polling task.</summary>
        public void Dispose() => _cancellation.Dispose();
    }
}
