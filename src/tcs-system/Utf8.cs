using System.Text;

namespace TinySystem.Text;

// tcs strings are UTF-8. Only the .NET string is UTF-16, so character and byte positions are reconciled here.
// tcs C and VM call C code with the same steps in runtime/utf8 (matching results are checked by DifferentialTests.Utf8 in tests/tcs-backends).

/// <summary>Operations on a string per character (Unicode scalar value, one item of <c>EnumerateRunes</c>). All take time proportional to the number of characters.</summary>
public static class Runes
{
    public static int RuneCount(this string s)
    {
        int count = s.Length;
        for (int i = 0; i + 1 < s.Length; i++)
        {
            if (char.IsSurrogatePair(s[i], s[i + 1]))
            {
                count--;
                i++;
            }
        }
        return count;
    }

    /// <summary>count characters from character start. Going outside the string throws ArgumentOutOfRangeException.</summary>
    public static string RuneSubstring(this string s, int start, int count)
    {
        // netstandard2.0 (which the TinyC# analyzer reads together with the compiler) has no ThrowIfNegative
        if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        int from = Advance(s, 0, start);
        return s.Substring(from, Advance(s, from, count) - from);
    }

    private static int Advance(string s, int at, int runes)
    {
        for (int i = 0; i < runes; i++)
        {
            if (at >= s.Length) throw new ArgumentOutOfRangeException(nameof(runes));
            at += at + 1 < s.Length && char.IsSurrogatePair(s[at], s[at + 1]) ? 2 : 1;
        }
        return at;
    }
}

/// <summary>Read-only view of the UTF-8 bytes of a string. Indexing is O(1), and Slice does not copy bytes.
/// .NET converts to byte[] only once at creation, and tcs C and VM point to the string directly.
/// <c>default</c> is a view of length 0.</summary>
public readonly struct Utf8Bytes
{
    private readonly byte[] _bytes;
    private readonly int _start;

    public int Length { get; }

    public Utf8Bytes(string s)
    {
        _bytes = Encoding.UTF8.GetBytes(s);
        _start = 0;
        Length = _bytes.Length;
    }

    private Utf8Bytes(byte[] bytes, int start, int length)
    {
        _bytes = bytes;
        _start = start;
        Length = length;
    }

    public byte this[int index] =>
        (uint)index < (uint)Length ? _bytes[_start + index] : throw new IndexOutOfRangeException();

    public Utf8Bytes Slice(int start, int count)
    {
        if (start < 0 || start > Length) throw new ArgumentOutOfRangeException(nameof(start));
        if (count < 0 || count > Length - start) throw new ArgumentOutOfRangeException(nameof(count));
        return new Utf8Bytes(_bytes, _start + start, count);
    }

    /// <summary>String of the bytes. A broken sequence in a slice cut in the middle of a character becomes U+FFFD.</summary>
    public override string ToString() =>
        Length == 0 ? "" : Encoding.UTF8.GetString(_bytes, _start, Length);
}
