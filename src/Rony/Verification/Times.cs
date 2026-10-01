using System;

namespace Rony.Net
{
    /// <summary>
    /// How many times a request is expected, for <c>Verify(...)</c>.
    /// </summary>
    public readonly struct Times : IEquatable<Times>
    {
        private Times(int min, int max)
        {
            if (min < 0) throw new ArgumentOutOfRangeException(nameof(min), "The count can't be negative.");
            if (max < min) throw new ArgumentOutOfRangeException(nameof(max), "The maximum can't be less than the minimum.");
            Min = min;
            Max = max;
        }

        /// <summary>The smallest accepted count.</summary>
        public int Min { get; }

        /// <summary>The largest accepted count.</summary>
        public int Max { get; }

        /// <summary>The request must not have been received.</summary>
        public static Times Never() => new Times(0, 0);
        /// <summary>Exactly one time.</summary>
        public static Times Once() => new Times(1, 1);
        /// <summary>One or more times.</summary>
        public static Times AtLeastOnce() => new Times(1, int.MaxValue);
        /// <summary>Exactly <paramref name="count"/> times.</summary>
        public static Times Exactly(int count) => new Times(count, count);
        /// <summary><paramref name="count"/> or more times.</summary>
        public static Times AtLeast(int count) => new Times(count, int.MaxValue);
        /// <summary>No more than <paramref name="count"/> times (including zero).</summary>
        public static Times AtMost(int count) => new Times(0, count);
        /// <summary>From <paramref name="min"/> to <paramref name="max"/> times, inclusive.</summary>
        public static Times Between(int min, int max) => new Times(min, max);

        /// <summary>Whether <paramref name="count"/> satisfies this expectation.</summary>
        public bool Matches(int count) => count >= Min && count <= Max;

        public bool Equals(Times other) => Min == other.Min && Max == other.Max;
        public override bool Equals(object obj) => obj is Times other && Equals(other);
        public override int GetHashCode() => (Min * 397) ^ Max;

        public override string ToString()
        {
            if (Min == Max) return Min == 0 ? "never" : $"exactly {Plural(Min)}";
            if (Max == int.MaxValue) return $"at least {Plural(Min)}";
            if (Min == 0) return $"at most {Plural(Max)}";
            return $"between {Min} and {Plural(Max)}";
        }

        internal static string Plural(int count) => count == 1 ? "1 time" : $"{count} times";
    }
}
