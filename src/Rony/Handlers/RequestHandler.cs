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
    public class RequestHandler
    {
        private static readonly byte[] AnyRequest = new byte[0];
        private static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(5);

        private readonly ConcurrentDictionary<byte[], Config> _configs;
        private readonly List<PredicateConfig> _predicateConfigs = new List<PredicateConfig>();
        private readonly RequestJournal _journal = new RequestJournal();
        private PendingRequest _pending;

        /// <summary>
        /// Responses configured for exact requests, keyed by the raw request bytes. An empty key matches any request.
        /// Requests configured with a pattern or predicate are not listed here.
        /// </summary>
        public IReadOnlyDictionary<byte[], Config> Configs => _configs;

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
        public RequestHandler Send(byte[] receiveData)
        {
            var request = receiveData ?? AnyRequest;
            _pending = new PendingRequest(request, null, ByteFormatter.Describe(request));
            return this;
        }

        /// <summary>Configures the response to every request whose text matches <paramref name="pattern"/>.</summary>
        public RequestHandler Send(Regex pattern)
        {
            if (pattern == null) throw new ArgumentNullException(nameof(pattern));
            _pending = new PendingRequest(null, request => pattern.IsMatch(request.GetString()), $"/{pattern}/");
            return this;
        }

        /// <summary>Configures the response to every request whose text satisfies <paramref name="predicate"/>.</summary>
        public RequestHandler SendMatching(Func<string, bool> predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            _pending = new PendingRequest(null, request => predicate(request.GetString()), "<predicate>");
            return this;
        }

        /// <summary>Configures the response to every request whose bytes satisfy <paramref name="predicate"/>.</summary>
        public RequestHandler SendMatchingBytes(Func<byte[], bool> predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            _pending = new PendingRequest(null, predicate, "<predicate>");
            return this;
        }

        public ResponseBuilder Receive(string response) => Add(ResponseStep.Reply(response));

        public ResponseBuilder Receive(byte[] response) => Add(ResponseStep.Reply(response));

        public ResponseBuilder Receive(Func<string, string> func) => Add(ResponseStep.Reply(func));

        public ResponseBuilder Receive(Func<byte[], byte[]> func) => Add(ResponseStep.Reply(func));

        /// <summary>Closes the connection without replying (TCP). For UDP this behaves like <see cref="NoReply"/>.</summary>
        public ResponseBuilder Disconnect() => Add(ResponseStep.CloseConnection());

        /// <summary>Accepts the request but never replies, to test client timeouts.</summary>
        public ResponseBuilder NoReply() => Add(ResponseStep.NoReply());

        /// <summary>Removes every configured response and every recorded request.</summary>
        public void Reset()
        {
            _configs.Clear();
            lock (_predicateConfigs)
                _predicateConfigs.Clear();
            _journal.Clear();
            _pending = null;
        }

        #endregion

        #region Matching

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
            return Handle(request, null)?.Produce(request) ?? new byte[0];
        }

        internal ResponseStep Handle(byte[] request, EndPoint remoteEndPoint)
        {
            var config = Find(request);
            _journal.Record(new ReceivedRequest(request, remoteEndPoint, DateTimeOffset.Now, config != null));
            return config?.NextStep();
        }

        private Config Find(byte[] request)
        {
            if (request.Length > 0 && _configs.TryGetValue(request, out var config))
                return config;

            lock (_predicateConfigs)
            {
                foreach (var predicateConfig in _predicateConfigs)
                {
                    if (predicateConfig.Matches(request))
                        return predicateConfig.Config;
                }
            }

            return _configs.TryGetValue(AnyRequest, out config) ? config : null;
        }

        #endregion

        #region Verification

        /// <summary>Every request received so far, oldest first.</summary>
        public IReadOnlyList<ReceivedRequest> ReceivedRequests => _journal.Snapshot();

        /// <summary>Received requests that no configured response handled.</summary>
        public IReadOnlyList<ReceivedRequest> UnmatchedRequests => _journal.Snapshot().Where(r => !r.Matched).ToArray();

        public void ClearReceivedRequests() => _journal.Clear();

        /// <summary>Verifies the request was received at least once.</summary>
        public void Verify(string request) => Verify(request, Times.AtLeastOnce());

        public void Verify(string request, Times times) => Verify((request ?? string.Empty).GetBytes(), times);

        /// <inheritdoc cref="Verify(string)"/>
        public void Verify(byte[] request) => Verify(request, Times.AtLeastOnce());

        public void Verify(byte[] request, Times times)
        {
            var expected = request ?? AnyRequest;
            Verify(r => ByteArrayComparer.Instance.Equals(r.Body, expected), times, $"request {ByteFormatter.Describe(expected)}");
        }

        /// <summary>Verifies a request satisfying <paramref name="predicate"/> was received at least once.</summary>
        public void Verify(Func<ReceivedRequest, bool> predicate) => Verify(predicate, Times.AtLeastOnce());

        public void Verify(Func<ReceivedRequest, bool> predicate, Times times) =>
            Verify(predicate ?? throw new ArgumentNullException(nameof(predicate)), times, "a request matching the predicate");

        /// <summary>Strict mode: throws if any received request had no configured response.</summary>
        public void VerifyAllRequestsMatched()
        {
            var unmatched = UnmatchedRequests;
            if (unmatched.Count == 0) return;
            throw new MockVerificationException(
                $"{unmatched.Count} {(unmatched.Count == 1 ? "request" : "requests")} had no configured response:" + Environment.NewLine +
                string.Join(Environment.NewLine, unmatched.Select(r => $"  {ByteFormatter.Describe(r.Body)}")));
        }

        private void Verify(Func<ReceivedRequest, bool> predicate, Times times, string description)
        {
            var requests = _journal.Snapshot();
            var count = requests.Count(predicate);
            if (times.Matches(count)) return;

            throw new MockVerificationException(
                $"Expected {description} {times}, but it was received {Times.Plural(count)}." + Environment.NewLine +
                RequestJournal.Describe(requests));
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
                requests => requests.Count >= count ? requests : null,
                actualTimeout,
                requests => $"Expected {count} requests within {actualTimeout}, but received {requests.Count}." + Environment.NewLine + RequestJournal.Describe(requests),
                cancellationToken);
        }

        private Task<ReceivedRequest> WaitForRequestAsync(Func<ReceivedRequest, bool> predicate, string description, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            var actualTimeout = timeout ?? DefaultWaitTimeout;
            return _journal.WaitAsync(
                requests => requests.FirstOrDefault(predicate),
                actualTimeout,
                requests => $"Expected {description} within {actualTimeout}, but it was not received." + Environment.NewLine + RequestJournal.Describe(requests),
                cancellationToken);
        }

        #endregion

        private ResponseBuilder Add(ResponseStep step)
        {
            var pending = _pending ?? throw new InvalidOperationException(
                $"Call {nameof(Send)}() or {nameof(SendMatching)}() before configuring the response.");
            _pending = null;

            var config = new Config(step);
            if (pending.Request != null)
            {
                if (!_configs.TryAdd(pending.Request, config))
                    throw new ArgumentException($"A response is already configured for request {pending.Description}.");
            }
            else
            {
                lock (_predicateConfigs)
                    _predicateConfigs.Add(new PredicateConfig(pending.Predicate, config));
            }

            return new ResponseBuilder(config);
        }

        private sealed class PendingRequest
        {
            public PendingRequest(byte[] request, Func<byte[], bool> predicate, string description)
            {
                Request = request;
                Predicate = predicate;
                Description = description;
            }

            public byte[] Request { get; }
            public Func<byte[], bool> Predicate { get; }
            public string Description { get; }
        }

        private sealed class PredicateConfig
        {
            private readonly Func<byte[], bool> _predicate;

            public PredicateConfig(Func<byte[], bool> predicate, Config config)
            {
                _predicate = predicate;
                Config = config;
            }

            public Config Config { get; }

            public bool Matches(byte[] request)
            {
                try
                {
                    return _predicate(request);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }
    }
}
