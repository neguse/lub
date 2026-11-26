using TinySystem.Text;

namespace Tcs;

// Byte-level string utilities (the subset's string has no position APIs)
public static class Strings
{
    public static int ByteLength(string s)
    {
        return new Utf8Bytes(s).Length;
    }

    // From byte start onward
    public static string From(string s, int start)
    {
        var u = new Utf8Bytes(s);
        if (start >= u.Length) return "";
        return u.Slice(start, u.Length - start).ToString();
    }

    // The first n bytes
    public static string Prefix(string s, int n)
    {
        var u = new Utf8Bytes(s);
        if (n >= u.Length) return s;
        return u.Slice(0, n).ToString();
    }

    public static bool IsAsciiLetterOrDigit(int c)
    {
        return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
    }
}
