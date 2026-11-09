using TinySystem.Text;

namespace TcsSelf;

// 文字列の byte 単位の道具 (サブセットの string は位置の API を持たない)
public static class Strings
{
    public static int ByteLength(string s)
    {
        return new Utf8Bytes(s).Length;
    }

    // start byte 目から後ろ
    public static string From(string s, int start)
    {
        var u = new Utf8Bytes(s);
        if (start >= u.Length) return "";
        return u.Slice(start, u.Length - start).ToString();
    }

    // 先頭の n byte
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
