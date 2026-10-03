using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Rony.Cli
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            using var stop = new CancellationTokenSource();

            var requests = 0;

            // The first signal asks for a clean stop; a second one is not swallowed, so a hanging shutdown can be escaped.
            bool Cancel()
            {
                if (Interlocked.Increment(ref requests) > 1) return false;
                try
                {
                    stop.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The command has already ended.
                }

                return true;
            }

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = Cancel();
            };

            using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = Cancel();
            });

            return await CliApp.RunAsync(args, Console.Out, Console.Error, stop.Token).ConfigureAwait(false);
        }
    }
}
