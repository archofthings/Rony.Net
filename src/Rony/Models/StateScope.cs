namespace Rony.Net
{
    /// <summary>Where the scenario state used by <c>InState(...)</c> and <c>GoTo(...)</c> lives.</summary>
    public enum StateScope
    {
        /// <summary>One state for the whole server: a <c>GoTo(...)</c> on one connection affects every client.</summary>
        Server,

        /// <summary>
        /// Every TCP connection (or, for UDP, every client address) has its own state, starting at
        /// <see cref="Rony.Handlers.RequestHandler.InitialState"/>. Use it for per-session protocols, such as a login per connection.
        /// </summary>
        Connection
    }
}
