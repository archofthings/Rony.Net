using System.Collections.Generic;
using System.Linq;

namespace Rony.Helpers
{
    /// <summary>
    /// Compares byte arrays by content, so they can be used as dictionary keys
    /// </summary>
    public sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new ByteArrayComparer();

        public bool Equals(byte[] x, byte[] y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x == null || y == null) return false;
            return x.SequenceEqual(y);
        }

        public int GetHashCode(byte[] obj)
        {
            if (obj == null) return 0;
            unchecked
            {
                var hash = (int)2166136261;
                foreach (var b in obj)
                    hash = (hash ^ b) * 16777619;
                return hash;
            }
        }
    }
}
