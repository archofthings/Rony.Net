using Rony.Helpers;
using Rony.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Rony.Handlers
{
    /// <summary>Turns a <see cref="Recording"/> into rules of a <see cref="RequestHandler"/>.</summary>
    internal static class RecordingReplay
    {
        /// <summary>What the server did after one client message (or on connect): the payloads it sent, and whether it closed the connection.</summary>
        private sealed class Reaction
        {
            public Reaction(List<byte[]> payloads, bool disconnect)
            {
                Payloads = payloads;
                Disconnect = disconnect;
            }

            public List<byte[]> Payloads { get; }
            public bool Disconnect { get; }

            public bool SameAs(Reaction other) =>
                other != null && Disconnect == other.Disconnect && Payloads.Count == other.Payloads.Count
                && Payloads.Zip(other.Payloads, (a, b) => ByteArrayComparer.Instance.Equals(a, b)).All(equal => equal);
        }

        // frame: frames a payload the way the listener sends it; used for the second and later payloads of a reaction.
        public static void Apply(RequestHandler mock, Recording recording, Func<byte[], byte[]> frame)
        {
            var greetings = new List<Reaction>();
            var requests = new List<byte[]>();
            var reactions = new Dictionary<byte[], List<Reaction>>(ByteArrayComparer.Instance);

            foreach (var connection in recording.Connections)
            {
                var messages = connection.Messages;
                var serverClosed = messages.Count > 0 && messages[messages.Count - 1].IsClose && messages[messages.Count - 1].Source == RecordedSource.Server;
                var isRequest = new Func<RecordedMessage, bool>(m => m.Source == RecordedSource.Client && !m.IsClose);

                var index = 0;
                var payloads = new List<byte[]>();
                for (; index < messages.Count && !isRequest(messages[index]); index++)
                    AddPayload(payloads, messages[index]);

                if (payloads.Count > 0)
                    greetings.Add(new Reaction(payloads, serverClosed && index == messages.Count));

                while (index < messages.Count)
                {
                    var request = messages[index++].Body;
                    payloads = new List<byte[]>();
                    for (; index < messages.Count && !isRequest(messages[index]); index++)
                        AddPayload(payloads, messages[index]);

                    // An empty request would match every request.
                    if (request.Length == 0) continue;
                    if (!reactions.TryGetValue(request, out var list))
                    {
                        reactions[request] = list = new List<Reaction>();
                        requests.Add(request);
                    }
                    list.Add(new Reaction(payloads, serverClosed && index == messages.Count));
                }
            }

            if (greetings.Count > 0)
            {
                // Same rule as for requests: identical greetings need one step only.
                if (greetings.All(g => g.SameAs(greetings[0]))) greetings = greetings.Take(1).ToList();

                ResponseBuilder greeting = null;
                foreach (var reaction in greetings)
                    greeting = Add(greeting == null ? mock.OnConnect() : null, greeting, reaction, frame, greeting == null);
            }

            foreach (var request in requests)
            {
                var list = reactions[request];
                // Identical reactions need one step only.
                if (list.All(r => r.SameAs(list[0]))) list = list.Take(1).ToList();

                ResponseBuilder builder = null;
                foreach (var reaction in list)
                    builder = Add(builder == null ? mock.Send(request) : null, builder, reaction, frame, builder == null);
            }
        }

        private static void AddPayload(List<byte[]> payloads, RecordedMessage message)
        {
            if (message.Source == RecordedSource.Server && !message.IsClose && message.Body.Length > 0)
                payloads.Add(message.Body);
        }

        /// <summary>Adds the first step through <paramref name="handler"/>, later ones with <c>Then...</c> on <paramref name="builder"/>.</summary>
        private static ResponseBuilder Add(RequestHandler handler, ResponseBuilder builder, Reaction reaction, Func<byte[], byte[]> frame, bool first)
        {
            if (reaction.Payloads.Count == 0)
                return first
                    ? (reaction.Disconnect ? handler.Disconnect() : handler.NoReply())
                    : (reaction.Disconnect ? builder.ThenDisconnect() : builder.ThenNoReply());

            builder = first ? handler.Receive(reaction.Payloads[0]) : builder.Then(reaction.Payloads[0]);
            if (reaction.Payloads.Count > 1)
                builder.AppendFrames(reaction.Payloads.Skip(1).ToList(), frame);
            return reaction.Disconnect ? builder.AndDisconnect() : builder;
        }
    }
}
