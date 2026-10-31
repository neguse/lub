using System.Text;

namespace TinySystem.Text;

// tcs の文字列は UTF-8。.NET の string だけが UTF-16 なので、文字と byte の位置はここで揃える。
// tcs2c と Lua は runtime/utf8 の同じ手順の C を呼ぶ (結果の一致は tcs2c.Tests の DifferentialTests.Utf8)。

/// <summary>string の文字 (Unicode scalar value、<c>EnumerateRunes</c> の 1 個) 単位の操作。どれも文字数に比例する時間がかかる。</summary>
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

    /// <summary>start 文字目から count 文字。文字列の範囲を出ると ArgumentOutOfRangeException。</summary>
    public static string RuneSubstring(this string s, int start, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
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

/// <summary>string の UTF-8 の byte 列の読み取り専用の view。添字は O(1)、Slice は byte を複製しない。
/// .NET は作るときに 1 度だけ byte[] に変換し、tcs2c と Lua は文字列をそのまま指す。
/// <c>default</c> は長さ 0 の view。</summary>
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
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start, Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Length - start);
        return new Utf8Bytes(_bytes, _start + start, count);
    }

    /// <summary>byte 列の string。文字の途中で切った slice の壊れた並びは U+FFFD になる。</summary>
    public override string ToString() =>
        Length == 0 ? "" : Encoding.UTF8.GetString(_bytes, _start, Length);
}
