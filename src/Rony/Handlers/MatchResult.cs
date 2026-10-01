using Rony.Models;

namespace Rony.Handlers
{
    /// <summary>What the handler decided for one request or connection.</summary>
    internal sealed class MatchResult
    {
        public MatchResult(ResponseStep step, string rule, bool matched, string state, string nextState)
        {
            Step = step;
            Rule = rule;
            Matched = matched;
            State = state;
            NextState = nextState;
        }

        /// <summary>The reaction to use; null when nothing is configured.</summary>
        public ResponseStep Step { get; }

        /// <summary>Description of the rule that was used, for logs.</summary>
        public string Rule { get; }

        public bool Matched { get; }

        /// <summary>The scenario state the request was matched in.</summary>
        public string State { get; }

        /// <summary>The state the scenario moved to, or null when it did not move.</summary>
        public string NextState { get; }
    }
}
