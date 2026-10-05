using Rony.Helpers;
using Rony.Models;
using Rony.Net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Rony.Handlers
{
    /// <summary>
    /// Holds the configured responses of a <see cref="MockServer"/> and records the requests it receives.
    /// Start a configuration with <c>Send(...)</c> or <c>SendMatching(...)</c>, then finish it with
    /// <c>Receive(...)</c>, <see cref="Disconnect"/> or <see cref="NoReply"/>.
    /// </summary>
    public class RequestHandler
    {
        /// <summary>The scenario state before any response moved it with <c>GoTo(...)</c>.</summary>
        public const string InitialState = "initial";

        private static readonly byte[] AnyRequest = new byte[0];
        private static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(5);

        private readonly ConcurrentDictionary<byte[], Config> _configs;
        private readonly List<PredicateConfig> _predicateConfigs = new List<PredicateConfig>();
        private readonly Journal<ReceivedRequest> _journal = new Journal<ReceivedRequest>();
        private volatile int _maxReceivedRequests;

        // Guards matching and everything below, so finding a rule and moving to its next state is atomic.
        private readonly object _matchLock = new object();
        private readonly Dictionary<string, Dictionary<byte[], Config>> _stateConfigs = new Dictionary<string, Dictionary<byte[], Config>>();
        private readonly Dictionary<object, string> _connectionStates = new Dictionary<object, string>();
        private string _state = InitialState;
        private Config _connectConfig;
        private Config _unmatchedConfig;
        private PendingRequest _pending;

        /// <summary>
        /// Responses configured for exact requests, keyed by the raw request bytes. An empty key matches any request.
        /// Requests configured with a pattern, a predicate or <c>InState(...)</c> are not listed here.
        /// </summary>
        public IReadOnlyDictionary<byte[], Config> Configs => _configs;

        /// <summary>
        /// Whether the scenario state is shared by the whole server (default) or kept separately for every connection.
        /// </summary>
        public StateScope StateScope { get; set; } = StateScope.Server;

        /// <summary>
        /// The server-wide scenario state, <see cref="InitialState"/> until a response moves it with <c>GoTo(...)</c>.
        /// Set it to start a test in a given state. With <see cref="Rony.Net.StateScope.Connection"/>, see
        /// <see cref="ClientConnection.State"/> instead.
        /// </summary>
        public string State
        {
            get
            {
                lock (_matchLock)
                    return _state;
            }
            set
            {
                lock (_matchLock)
                    _state = value ?? throw new ArgumentNullException(nameof(value));
            }
        }

        /// <summary>
        /// Fail fast: once a request without a configured response arrives, every <c>Verify...</c> call and every
        /// <c>WaitFor...</c> call (including ones already waiting) throws <see cref="MockVerificationException"/>
        /// listing the unmatched requests, instead of reporting something less direct or waiting for its timeout.
        /// </summary>
        public bool FailOnUnmatched { get; set; }

        /// <summary>Creates an empty handler.</summary>
        public RequestHandler()
        {
            _configs = new ConcurrentDictionary<byte[], Config>(ByteArrayComparer.Instance);
        }

        #region Configuration

        /// <summary>Configures the response to this exact request. An empty request matches any request.</summary>
        public RequestHandler Send(string receiveData)
        {
            return Send((receiveData ?? string.Empty).GetBytes());
        }

        /// <inheritdoc cref="Send(string)"/>
        public RequestHandler Send(byte[] receiveData) => SendExact(receiveData, null);

        /// <summary>Configures the response to every request whose text matches <paramref name="pattern"/>.</summary>
        public RequestHandler Send(Regex pattern) => SendPattern(pattern, null);

        /// <summary>Configures the response to every request whose text satisfies <paramref name="predicate"/>.</summary>
        public RequestHandler SendMatching(Func<string, bool> predicate) => SendText(predicate, null);

        /// <summary>Configures the response to every request whose bytes satisfy <paramref name="predicate"/>.</summary>
        public RequestHandler SendMatchingBytes(Func<byte[], bool> predicate) => SendBytes(predicate, null);

        /// <summary>
        /// Configures the response to every request that is valid JSON (UTF-8) and satisfies <paramref name="predicate"/>.
        /// A request that is not valid JSON does not match, and the predicate is not called for it.
        /// </summary>
        /// <example><code>server.Mock.SendJson(j => j["type"].AsString() == "login").Receive("{\"ok\":true}");</code></example>
        public RequestHandler SendJson(Func<JsonData, bool> predicate) => SendJson(predicate, null);

        /// <summary>
        /// Starts a rule that only applies while the scenario is in <paramref name="state"/>. Responses move the
        /// scenario between states with <c>GoTo(...)</c>. For the same request, a rule for the current state wins
        /// over a rule without a state.
        /// </summary>
        /// <example><code>
        /// server.Mock.Send("LOGIN bob").Receive("OK").GoTo("loggedIn");
        /// server.Mock.InState("loggedIn").Send("LIST").Receive("a,b,c");
        /// server.Mock.Send("LIST").Receive("ERR not logged in");
        /// </code></example>
        public StateRequestBuilder InState(string state)
        {
            return new StateRequestBuilder(this, state ?? throw new ArgumentNullException(nameof(state)));
        }

        /// <summary>
        /// Configures what the server sends as soon as a client connects, before it sends anything: a greeting or
        /// banner, a different one for every connection with <c>Then(...)</c>, or <see cref="Disconnect"/> to
        /// refuse connections. TCP only.
        /// </summary>
        public RequestHandler OnConnect()
        {
            _pending = new PendingRequest(RuleKind.Connect, null, null, null, "on connect");
            return this;
        }

        /// <summary>
        /// Configures the response to requests that no other rule matches. Unlike <c>Send("")</c>, these requests
        /// are still recorded as unmatched, so <see cref="VerifyAllRequestsMatched"/> and <see cref="FailOnUnmatched"/>
        /// report them. Without it, an unmatched request gets an empty response and its connection is closed.
        /// </summary>
        public RequestHandler OnUnmatched()
        {
            _pending = new PendingRequest(RuleKind.Unmatched, null, null, null, "unmatched requests");
            return this;
        }

        internal RequestHandler SendExact(byte[] receiveData, string state)
        {
            var request = receiveData ?? AnyRequest;
            _pending = new PendingRequest(RuleKind.Request, request, null, state,
                request.Length == 0 ? "any request" : ByteFormatter.Describe(request));
            return this;
        }

        internal RequestHandler SendPattern(Regex pattern, string state)
        {
            if (pattern == null) throw new ArgumentNullException(nameof(pattern));
            _pending = new PendingRequest(RuleKind.Request, null, request => pattern.IsMatch(request.GetString()), state, $"/{pattern}/", pattern);
            return this;
        }

        internal RequestHandler SendJson(Func<JsonData, bool> predicate, string state)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            _pending = new PendingRequest(RuleKind.Request, null,
                request => JsonData.TryParse(request.GetString(), out var json) && predicate(json), state, "<json predicate>");
            return this;
        }

        internal RequestHandler SendText(Func<string, bool> predicate, string state)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            _pending = new PendingRequest(RuleKind.Request, null, request => predicate(request.GetString()), state, "<predicate>");
            return this;
        }

        internal RequestHandler SendBytes(Func<byte[], bool> predicate, string state)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            _pending = new PendingRequest(RuleKind.Request, null, predicate, state, "<predicate>");
            return this;
        }

        /// <summary>Responds with this text.</summary>
        public ResponseBuilder Receive(string response) => Add(ResponseStep.Reply(response));

        /// <summary>Responds with these bytes.</summary>
        public ResponseBuilder Receive(byte[] response) => Add(ResponseStep.Reply(response));

        /// <summary>Responds with the result of <paramref name="func"/>, called with the request text. If it throws, the response is empty.</summary>
        public ResponseBuilder Receive(Func<string, string> func) => Add(ResponseStep.Reply(func));

        /// <summary>Responds with the result of <paramref name="func"/>, called with the request bytes. If it throws, the response is empty.</summary>
        public ResponseBuilder Receive(Func<byte[], byte[]> func) => Add(ResponseStep.Reply(func));

        /// <summary>
        /// Responds with the result of <paramref name="func"/>, called with the regular expression match of the request,
        /// so it can use capture groups. A null result is an empty response; if <paramref name="func"/> throws, the response is empty.
        /// Only for a rule started with <c>Send(Regex)</c>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="func"/> is null.</exception>
        /// <exception cref="InvalidOperationException">The rule was not started with <c>Send(Regex)</c>.</exception>
        /// <example><code>server.Mock.Send(new Regex(@"^HELLO (\w+)$")).ReceiveMatch(m => $"HI {m.Groups[1].Value}");</code></example>
        public ResponseBuilder ReceiveMatch(Func<Match, string> func)
        {
            if (func == null) throw new ArgumentNullException(nameof(func));
            if (_pending != null && _pending.Pattern == null)
                throw new InvalidOperationException(NeedsRegex(nameof(ReceiveMatch)));
            return Add(ResponseStep.Reply(_pending?.Pattern, func));
        }

        internal static string NeedsRegex(string method) =>
            $"{method}() needs a rule started with Send(Regex), because it uses the regular expression match.";

        /// <summary>Closes the connection without replying (TCP). For UDP this behaves like <see cref="NoReply"/>.</summary>
        public ResponseBuilder Disconnect() => Add(ResponseStep.CloseConnection());

        /// <summary>
        /// Aborts the connection with a TCP reset (RST) without replying, so the client sees a connection reset instead
        /// of a clean end of stream (TCP only). Like <see cref="Disconnect"/> on a listener that cannot reset.
        /// </summary>
        public ResponseBuilder ResetConnection() => Add(ResponseStep.ResetConnection());

        /// <summary>Accepts the request but never replies, to test client timeouts.</summary>
        public ResponseBuilder NoReply() => Add(ResponseStep.NoReply());

        /// <summary>
        /// Removes every configured response (including <see cref="OnConnect"/> and <see cref="OnUnmatched"/>) and every
        /// recorded request, and moves the scenario back to <see cref="InitialState"/>. Settings such as
        /// <see cref="StateScope"/> and <see cref="FailOnUnmatched"/> are kept.
        /// </summary>
        public void Reset()
        {
            // One step, in the lock order of Find and ReplaceRulesWith, so a reload never meets a half-cleared rule set.
            lock (_matchLock)
            {
                _configs.Clear();
                lock (_predicateConfigs)
                    _predicateConfigs.Clear();
                _stateConfigs.Clear();
                _connectionStates.Clear();
                _state = InitialState;
                _connectConfig = null;
                _unmatchedConfig = null;
            }
            _journal.Clear();
            _pending = null;
        }

        /// <summary>
        /// Replaces every configured response, <see cref="StateScope"/> and <see cref="FailOnUnmatched"/> with those of
        /// <paramref name="source"/> (a stand-alone handler that is not used afterwards). Recorded requests and the scenario state are kept.
        /// Matching sees either all old or all new rules.
        /// </summary>
        internal void ReplaceRulesWith(RequestHandler source)
        {
            // Same lock order as Find: _matchLock, then _predicateConfigs.
            lock (_matchLock)
            {
                _configs.Clear();
                foreach (var pair in source._configs)
                    _configs[pair.Key] = pair.Value;
                lock (_predicateConfigs)
                {
                    _predicateConfigs.Clear();
                    _predicateConfigs.AddRange(source._predicateConfigs);
                }
                _stateConfigs.Clear();
                foreach (var pair in source._stateConfigs)
                    _stateConfigs[pair.Key] = pair.Value;
                _connectConfig = source._connectConfig;
                _unmatchedConfig = source._unmatchedConfig;
                StateScope = source.StateScope;
                FailOnUnmatched = source.FailOnUnmatched;
            }
        }

        #endregion

        #region Matching

        /// <summary>Finds the response for a text request, as the server would. See <see cref="Match(byte[])"/>.</summary>
        public byte[] Match(string request)
        {
            return Match((request ?? string.Empty).GetBytes());
        }

        /// <summary>
        /// Finds the response for a request and records the request. Exact requests win over patterns and
        /// predicates (checked in the order they were added), which win over the "any request" config.
        /// </summary>
        public byte[] Match(byte[] request)
        {
            request ??= AnyRequest;
            return Handle(request, null, null, null, null).Step?.Produce(request) ?? new byte[0];
        }

        /// <summary>
        /// Finds the rule for a request, records the request and applies the rule's state change.
        /// <paramref name="stateKey"/> identifies the conversation for <see cref="Rony.Net.StateScope.Connection"/>
        /// (null uses the server state); <paramref name="onError"/> is told about exceptions thrown by configured predicates.
        /// </summary>
        internal MatchResult Handle(byte[] request, EndPoint remoteEndPoint, int? connectionId, object stateKey, Action<Exception, string> onError)
        {
            lock (_matchLock)
            {
                var state = GetStateCore(stateKey);
                var config = Find(request, state, onError);
                var matched = config != null;
                config ??= _unmatchedConfig;

                var received = new ReceivedRequest(request, remoteEndPoint, DateTimeOffset.Now, matched, connectionId);
                _journal.Record(received, MaxReceivedRequests, null);
                var result = Use(config, matched, state, stateKey);
                result.Request = received;
                return result;
            }
        }

        /// <summary>Picks the <see cref="OnConnect"/> response for a new connection, if one is configured.</summary>
        internal MatchResult HandleConnect(object stateKey)
        {
            lock (_matchLock)
                return Use(_connectConfig, true, GetStateCore(stateKey), stateKey);
        }

        /// <summary>Forgets the scenario state kept for a conversation (a dropped connection record).</summary>
        internal void ForgetState(object stateKey)
        {
            lock (_matchLock)
                _connectionStates.Remove(stateKey);
        }

        /// <summary>The number of conversations with a remembered scenario state.</summary>
        internal int ConnectionStateCount
        {
            get
            {
                lock (_matchLock)
                    return _connectionStates.Count;
            }
        }

        /// <summary>The scenario state of a conversation (see <see cref="StateScope"/>).</summary>
        internal string GetState(object stateKey)
        {
            lock (_matchLock)
                return GetStateCore(stateKey);
        }

        private MatchResult Use(Config config, bool matched, string state, object stateKey)
        {
            var step = config?.NextStep();
            if (step?.NextState != null)
                SetStateCore(stateKey, step.NextState);
            return new MatchResult(step, config?.Description, config?.State != null, matched, state, step?.NextState);
        }

        private string GetStateCore(object stateKey)
        {
            if (StateScope == StateScope.Connection && stateKey != null)
                return _connectionStates.TryGetValue(stateKey, out var state) ? state : InitialState;
            return _state;
        }

        private void SetStateCore(object stateKey, string state)
        {
            if (StateScope == StateScope.Connection && stateKey != null)
                _connectionStates[stateKey] = state;
            else
                _state = state;
        }

        // Exact requests win over patterns and predicates, which win over "any request". At every level, a rule
        // for the current state wins over a rule without a state.
        private Config Find(byte[] request, string state, Action<Exception, string> onError)
        {
            _stateConfigs.TryGetValue(state, out var stateConfigs);
            Config config;
            if (request.Length > 0)
            {
                if (stateConfigs != null && stateConfigs.TryGetValue(request, out config))
                    return config;
                if (_configs.TryGetValue(request, out config))
                    return config;
            }

            lock (_predicateConfigs)
            {
                foreach (var predicateConfig in _predicateConfigs)
                {
                    if (predicateConfig.State == state && predicateConfig.Matches(request, onError))
                        return predicateConfig.Config;
                }

                foreach (var predicateConfig in _predicateConfigs)
                {
                    if (predicateConfig.State == null && predicateConfig.Matches(request, onError))
                        return predicateConfig.Config;
                }
            }

            if (stateConfigs != null && stateConfigs.TryGetValue(AnyRequest, out config))
                return config;
            return _configs.TryGetValue(AnyRequest, out config) ? config : null;
        }

        #endregion

        #region Verification

        /// <summary>
        /// The most received requests that are kept; 0 (the default) means unlimited. When the limit is exceeded the oldest
        /// requests are dropped. A lower value takes effect when the next request is recorded. Only the kept requests are seen by
        /// <see cref="ReceivedRequests"/>, <see cref="UnmatchedRequests"/>, the <c>Verify</c> methods, <c>Should()</c> and the
        /// <c>WaitFor</c> methods, so <see cref="WaitForRequestsAsync"/> with a count above the limit cannot complete.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
        public int MaxReceivedRequests
        {
            get => _maxReceivedRequests;
            set
            {
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "The limit cannot be negative.");
                _maxReceivedRequests = value;
            }
        }

        /// <summary>Every request received so far (up to <see cref="MaxReceivedRequests"/>), oldest first.</summary>
        public IReadOnlyList<ReceivedRequest> ReceivedRequests => _journal.Snapshot();

        /// <summary>Received requests that no configured response handled.</summary>
        public IReadOnlyList<ReceivedRequest> UnmatchedRequests => _journal.Snapshot().Where(r => !r.Matched).ToArray();

        /// <summary>Forgets every recorded request. Configured responses are kept.</summary>
        public void ClearReceivedRequests() => _journal.Clear();

        /// <summary>Verifies the request was received at least once.</summary>
        public void Verify(string request) => Verify(request, Times.AtLeastOnce());

        /// <summary>Verifies how many times this exact request was received. Throws <see cref="MockVerificationException"/> otherwise.</summary>
        public void Verify(string request, Times times) => Verify((request ?? string.Empty).GetBytes(), times);

        /// <inheritdoc cref="Verify(string)"/>
        public void Verify(byte[] request) => Verify(request, Times.AtLeastOnce());

        /// <inheritdoc cref="Verify(string, Times)"/>
        public void Verify(byte[] request, Times times)
        {
            var expected = request ?? AnyRequest;
            Verify(r => ByteArrayComparer.Instance.Equals(r.Body, expected), times, $"request {ByteFormatter.Describe(expected)}");
        }

        /// <summary>Like <see cref="Verify(byte[], Times)"/>, counting only the requests received on one connection.</summary>
        internal void VerifyOnConnection(int connectionId, byte[] request, Times times)
        {
            var expected = request ?? AnyRequest;
            Verify(r => ByteArrayComparer.Instance.Equals(r.Body, expected), times, $"request {ByteFormatter.Describe(expected)}", connectionId);
        }

        /// <summary>Like <see cref="Verify(Func{ReceivedRequest, bool}, Times)"/>, counting only the requests received on one connection.</summary>
        internal void VerifyOnConnection(int connectionId, Func<ReceivedRequest, bool> predicate, Times times) =>
            Verify(predicate ?? throw new ArgumentNullException(nameof(predicate)), times, "a request matching the predicate", connectionId);

        /// <summary>Verifies a request satisfying <paramref name="predicate"/> was received at least once.</summary>
        public void Verify(Func<ReceivedRequest, bool> predicate) => Verify(predicate, Times.AtLeastOnce());

        /// <summary>Verifies how many received requests satisfy <paramref name="predicate"/>. Throws <see cref="MockVerificationException"/> otherwise.</summary>
        public void Verify(Func<ReceivedRequest, bool> predicate, Times times) =>
            Verify(predicate ?? throw new ArgumentNullException(nameof(predicate)), times, "a request matching the predicate");

        /// <summary>Strict mode: throws if any received request had no configured response.</summary>
        public void VerifyAllRequestsMatched()
        {
            var unmatched = UnmatchedRequests;
            if (unmatched.Count == 0) return;
            throw UnmatchedException(unmatched);
        }

        /// <summary>
        /// Verifies these exact requests were received in this order. Other requests may come before, after or
        /// in between. Throws <see cref="MockVerificationException"/> otherwise.
        /// </summary>
        /// <example><code>server.Mock.VerifyInOrder("LOGIN bob", "LIST", "QUIT");</code></example>
        public void VerifyInOrder(params string[] requests)
        {
            if (requests == null) throw new ArgumentNullException(nameof(requests));
            VerifyInOrder(requests.Select(r => (r ?? string.Empty).GetBytes()).ToArray());
        }

        /// <inheritdoc cref="VerifyInOrder(string[])"/>
        public void VerifyInOrder(params byte[][] requests)
        {
            if (requests == null) throw new ArgumentNullException(nameof(requests));
            VerifyInOrder(ExpectedBytes(requests), null);
        }

        /// <summary>Like <see cref="VerifyInOrder(byte[][])"/>, looking only at the requests received on one connection.</summary>
        internal void VerifyInOrderOnConnection(int connectionId, byte[][] requests)
        {
            if (requests == null) throw new ArgumentNullException(nameof(requests));
            VerifyInOrder(ExpectedBytes(requests), connectionId);
        }

        /// <summary>Like <see cref="VerifyInOrder(Func{ReceivedRequest, bool}[])"/>, looking only at the requests received on one connection.</summary>
        internal void VerifyInOrderOnConnection(int connectionId, Func<ReceivedRequest, bool>[] predicates)
        {
            if (predicates == null) throw new ArgumentNullException(nameof(predicates));
            VerifyInOrder(ExpectedPredicates(predicates), connectionId);
        }

        /// <summary>
        /// Verifies requests satisfying these predicates were received in this order, each one after the previous.
        /// Other requests may come before, after or in between.
        /// </summary>
        public void VerifyInOrder(params Func<ReceivedRequest, bool>[] predicates)
        {
            if (predicates == null) throw new ArgumentNullException(nameof(predicates));
            VerifyInOrder(ExpectedPredicates(predicates), null);
        }

        private static (Func<ReceivedRequest, bool> Matches, string Description)[] ExpectedBytes(byte[][] requests) =>
            requests
                .Select(r => r ?? AnyRequest)
                .Select(expected => ((Func<ReceivedRequest, bool>)(r => ByteArrayComparer.Instance.Equals(r.Body, expected)), ByteFormatter.Describe(expected)))
                .ToArray();

        private static (Func<ReceivedRequest, bool> Matches, string Description)[] ExpectedPredicates(Func<ReceivedRequest, bool>[] predicates) =>
            predicates
                .Select((p, i) => (p ?? throw new ArgumentNullException(nameof(predicates)), $"<predicate {i + 1}>"))
                .ToArray();

        private void VerifyInOrder((Func<ReceivedRequest, bool> Matches, string Description)[] expected, int? connectionId)
        {
            var requests = Snapshot(connectionId);
            var onConnection = connectionId == null ? string.Empty : $" on connection #{connectionId}";
            var position = 0;
            for (var i = 0; i < expected.Length; i++)
            {
                while (position < requests.Count && !expected[i].Matches(requests[position]))
                    position++;

                if (position == requests.Count)
                {
                    var order = string.Join(", ", expected.Select(e => e.Description)) + onConnection;
                    var detail = i == 0
                        ? $"{expected[0].Description} was not received"
                        : $"{expected[i].Description} was not received after {expected[i - 1].Description}";
                    throw new MockVerificationException(
                        $"Expected requests in order: {order}, but {detail}." + Environment.NewLine + RequestJournal.Describe(requests));
                }

                position++;
            }
        }

        private void Verify(Func<ReceivedRequest, bool> predicate, Times times, string description, int? connectionId = null)
        {
            var requests = Snapshot(connectionId);
            if (connectionId != null) description += $" on connection #{connectionId}";
            var count = requests.Count(predicate);
            if (times.Matches(count)) return;

            throw new MockVerificationException(
                $"Expected {description} {times}, but it was received {Times.Plural(count)}." + Environment.NewLine +
                RequestJournal.Describe(requests));
        }

        /// <summary>The recorded requests, after the <see cref="FailOnUnmatched"/> check.</summary>
        private IReadOnlyList<ReceivedRequest> Snapshot(int? connectionId = null)
        {
            var requests = _journal.Snapshot();
            ThrowIfFailingOnUnmatched(requests);
            return connectionId == null ? requests : requests.Where(r => r.ConnectionId == connectionId).ToArray();
        }

        private void ThrowIfFailingOnUnmatched(IReadOnlyList<ReceivedRequest> requests)
        {
            if (!FailOnUnmatched) return;
            var unmatched = requests.Where(r => !r.Matched).ToArray();
            if (unmatched.Length > 0)
                throw UnmatchedException(unmatched);
        }

        private static MockVerificationException UnmatchedException(IReadOnlyList<ReceivedRequest> unmatched)
        {
            return new MockVerificationException(
                $"{unmatched.Count} {(unmatched.Count == 1 ? "request" : "requests")} had no configured response:" + Environment.NewLine +
                string.Join(Environment.NewLine, unmatched.Select(r => $"  {ByteFormatter.Describe(r.Body)}")));
        }

        #endregion

        #region Waiting

        /// <summary>
        /// Waits for the first request (including ones already received). Throws <see cref="TimeoutException"/>
        /// after <paramref name="timeout"/>, which defaults to 5 seconds.
        /// </summary>
        public Task<ReceivedRequest> WaitForRequestAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            WaitForRequestAsync(_ => true, "a request", timeout, cancellationToken);

        /// <summary>Waits until this exact request has been received.</summary>
        public Task<ReceivedRequest> WaitForRequestAsync(string request, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            WaitForRequestAsync((request ?? string.Empty).GetBytes(), timeout, cancellationToken);

        /// <inheritdoc cref="WaitForRequestAsync(string, TimeSpan?, CancellationToken)"/>
        public Task<ReceivedRequest> WaitForRequestAsync(byte[] request, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var expected = request ?? AnyRequest;
            return WaitForRequestAsync(r => ByteArrayComparer.Instance.Equals(r.Body, expected),
                $"request {ByteFormatter.Describe(expected)}", timeout, cancellationToken);
        }

        /// <summary>Waits until a request satisfying <paramref name="predicate"/> has been received.</summary>
        public Task<ReceivedRequest> WaitForRequestAsync(Func<ReceivedRequest, bool> predicate, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            WaitForRequestAsync(predicate ?? throw new ArgumentNullException(nameof(predicate)), "a request matching the predicate", timeout, cancellationToken);

        /// <summary>Waits until at least <paramref name="count"/> requests have been received in total.</summary>
        public Task<IReadOnlyList<ReceivedRequest>> WaitForRequestsAsync(int count, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var actualTimeout = timeout ?? DefaultWaitTimeout;
            return _journal.WaitAsync(
                requests =>
                {
                    ThrowIfFailingOnUnmatched(requests);
                    return requests.Count >= count ? requests : null;
                },
                actualTimeout,
                requests => $"Expected {count} requests within {actualTimeout}, but received {requests.Count}." + Environment.NewLine + RequestJournal.Describe(requests),
                cancellationToken);
        }

        private Task<ReceivedRequest> WaitForRequestAsync(Func<ReceivedRequest, bool> predicate, string description, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            var actualTimeout = timeout ?? DefaultWaitTimeout;
            return _journal.WaitAsync(
                requests =>
                {
                    ThrowIfFailingOnUnmatched(requests);
                    return requests.FirstOrDefault(predicate);
                },
                actualTimeout,
                requests => $"Expected {description} within {actualTimeout}, but it was not received." + Environment.NewLine + RequestJournal.Describe(requests),
                cancellationToken);
        }

        #endregion

        private ResponseBuilder Add(ResponseStep step)
        {
            var pending = _pending ?? throw new InvalidOperationException(
                $"Call {nameof(Send)}(), {nameof(SendMatching)}(), {nameof(OnConnect)}() or {nameof(OnUnmatched)}() before configuring the response.");
            _pending = null;

            var description = pending.State == null ? pending.Description : $"{pending.Description} in state \"{pending.State}\"";
            var config = new Config(step) { Description = description, State = pending.State, Pattern = pending.Pattern };
            if (pending.Kind != RuleKind.Request)
            {
                lock (_matchLock)
                {
                    if ((pending.Kind == RuleKind.Connect ? _connectConfig : _unmatchedConfig) != null)
                        throw new ArgumentException($"A response is already configured for {pending.Description}.");
                    if (pending.Kind == RuleKind.Connect)
                        _connectConfig = config;
                    else
                        _unmatchedConfig = config;
                }
            }
            else if (pending.Request != null && pending.State != null)
            {
                lock (_matchLock)
                {
                    if (!_stateConfigs.TryGetValue(pending.State, out var stateConfigs))
                        _stateConfigs[pending.State] = stateConfigs = new Dictionary<byte[], Config>(ByteArrayComparer.Instance);
                    if (stateConfigs.ContainsKey(pending.Request))
                        throw new ArgumentException($"A response is already configured for request {description}.");
                    stateConfigs.Add(pending.Request, config);
                }
            }
            else if (pending.Request != null)
            {
                if (!_configs.TryAdd(pending.Request, config))
                    throw new ArgumentException($"A response is already configured for request {description}.");
            }
            else
            {
                lock (_predicateConfigs)
                    _predicateConfigs.Add(new PredicateConfig(pending.Predicate, pending.State, config));
            }

            return new ResponseBuilder(config);
        }

        private enum RuleKind
        {
            Request,
            Connect,
            Unmatched
        }

        private sealed class PendingRequest
        {
            public PendingRequest(RuleKind kind, byte[] request, Func<byte[], bool> predicate, string state, string description, Regex pattern = null)
            {
                Pattern = pattern;
                Kind = kind;
                Request = request;
                Predicate = predicate;
                State = state;
                Description = description;
            }

            public RuleKind Kind { get; }
            public byte[] Request { get; }
            public Func<byte[], bool> Predicate { get; }
            public string State { get; }
            public string Description { get; }
            public Regex Pattern { get; }
        }

        private sealed class PredicateConfig
        {
            private readonly Func<byte[], bool> _predicate;

            public PredicateConfig(Func<byte[], bool> predicate, string state, Config config)
            {
                _predicate = predicate;
                State = state;
                Config = config;
            }

            public string State { get; }
            public Config Config { get; }

            public bool Matches(byte[] request, Action<Exception, string> onError)
            {
                try
                {
                    return _predicate(request);
                }
                catch (Exception exception)
                {
                    onError?.Invoke(exception, $"the matcher {Config.Description}");
                    return false;
                }
            }
        }
    }
}
