using System.Globalization;
using System.Text.RegularExpressions;

namespace SafeDiskCleaner.Core;

/// <summary>
/// vkey() from the reference: every run of digits in a name, compared like a Python tuple,
/// so "app-1.0.9001" &lt; "app-1.0.10000" and (566, 36) &lt;= (566, 36).
/// </summary>
public sealed partial class VersionKey : IComparable<VersionKey>, IEquatable<VersionKey>
{
    private readonly long[] _parts;

    private VersionKey(long[] parts) => _parts = parts;

    public IReadOnlyList<long> Parts => _parts;

    public static VersionKey Parse(string? name)
    {
        var parts = new List<long>();
        if (!string.IsNullOrEmpty(name))
        {
            foreach (Match m in Digits().Matches(name))
            {
                parts.Add(long.TryParse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture, out long v) ? v : long.MaxValue);
            }
        }
        if (parts.Count == 0)
        {
            parts.Add(0);
        }
        return new VersionKey([.. parts]);
    }

    public int CompareTo(VersionKey? other)
    {
        if (other is null)
        {
            return 1;
        }
        int n = Math.Min(_parts.Length, other._parts.Length);
        for (int i = 0; i < n; i++)
        {
            int c = _parts[i].CompareTo(other._parts[i]);
            if (c != 0)
            {
                return c;
            }
        }
        return _parts.Length.CompareTo(other._parts.Length);
    }

    public bool Equals(VersionKey? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is VersionKey v && Equals(v);

    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (long p in _parts)
        {
            h.Add(p);
        }
        return h.ToHashCode();
    }

    public override string ToString() => string.Join('.', _parts);

    public static bool operator <=(VersionKey a, VersionKey b) => a.CompareTo(b) <= 0;

    public static bool operator >=(VersionKey a, VersionKey b) => a.CompareTo(b) >= 0;

    public static bool operator <(VersionKey a, VersionKey b) => a.CompareTo(b) < 0;

    public static bool operator >(VersionKey a, VersionKey b) => a.CompareTo(b) > 0;

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
