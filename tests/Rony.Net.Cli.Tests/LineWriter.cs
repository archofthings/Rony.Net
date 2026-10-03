using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Rony.Cli.Tests
{
    /// <summary>A writer that remembers its lines and lets a test wait for a line, without polling.</summary>
    internal sealed class LineWriter : TextWriter
    {
        private readonly object _sync = new object();
        private readonly List<string> _lines = new List<string>();
        private readonly List<(Func<string, bool> Match, TaskCompletionSource<string> Source)> _waiters =
            new List<(Func<string, bool>, TaskCompletionSource<string>)>();

        public override Encoding Encoding => Encoding.UTF8;

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_sync)
                    return _lines.ToArray();
            }
        }

        public string Text => string.Join("\n", Lines);

        public override void Write(char value)
        {
        }

        public override void WriteLine(string value)
        {
            lock (_sync)
            {
                foreach (var part in (value ?? string.Empty).Split('\n'))
                {
                    _lines.Add(part);
                    foreach (var waiter in _waiters.ToArray())
                    {
                        if (!waiter.Match(part)) continue;
                        _waiters.Remove(waiter);
                        waiter.Source.TrySetResult(part);
                    }
                }
            }
        }

        /// <summary>Completes with the first line (already written or not) that satisfies <paramref name="match"/>.</summary>
        public Task<string> WaitForLineAsync(Func<string, bool> match)
        {
            lock (_sync)
            {
                foreach (var line in _lines)
                    if (match(line)) return Task.FromResult(line);

                var source = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((match, source));
                return source.Task;
            }
        }
    }
}
