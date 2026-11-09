using System.Collections.Generic;
using TinySystem.Text;

namespace TcsSelf;

// tool の文字列の道具 (JSON の文字列の escape と、10 進の数の読み取り)
public static class ToolText
{
    // .NET の JsonEncodedText.Encode と同じ escape (非 ASCII と HTML で意味を持つ文字は \uXXXX)
    public static string JsonEscape(string s)
    {
        var u = new Utf8Bytes(s);
        var parts = new List<string>();
        int start = 0;
        int i = 0;
        while (i < u.Length)
        {
            int c = u[i];
            if (c >= 0x20 && c < 0x7F && c != '"' && c != '\\' && c != '<' && c != '>' && c != '&' && c != '\'' && c != '+' && c != '`')
            {
                i++;
                continue;
            }
            if (i > start) parts.Add(u.Slice(start, i - start).ToString());
            int len = 1;
            if (c == '\\') parts.Add("\\\\");
            else if (c == '\n') parts.Add("\\n");
            else if (c == '\r') parts.Add("\\r");
            else if (c == '\t') parts.Add("\\t");
            else if (c == 8) parts.Add("\\b");
            else if (c == 12) parts.Add("\\f");
            else if (c < 0x80) parts.Add(Unicode(c));
            else
            {
                int cp = c;
                if (c >= 0xF0 && i + 3 < u.Length)
                {
                    cp = ((c & 0x07) << 18) | ((u[i + 1] & 0x3F) << 12) | ((u[i + 2] & 0x3F) << 6) | (u[i + 3] & 0x3F);
                    len = 4;
                }
                else if (c >= 0xE0 && i + 2 < u.Length)
                {
                    cp = ((c & 0x0F) << 12) | ((u[i + 1] & 0x3F) << 6) | (u[i + 2] & 0x3F);
                    len = 3;
                }
                else if (c >= 0xC0 && i + 1 < u.Length)
                {
                    cp = ((c & 0x1F) << 6) | (u[i + 1] & 0x3F);
                    len = 2;
                }
                if (cp >= 0x10000)
                {
                    int v = cp - 0x10000;
                    parts.Add(Unicode(0xD800 + (v >> 10)));
                    parts.Add(Unicode(0xDC00 + (v & 0x3FF)));
                }
                else parts.Add(Unicode(cp));
            }
            i = i + len;
            start = i;
        }
        if (start == 0) return s;
        if (start < u.Length) parts.Add(u.Slice(start, u.Length - start).ToString());
        return string.Join("", parts);
    }

    private static string Unicode(int unit)
    {
        const string digits = "0123456789ABCDEF";
        var d = new Utf8Bytes(digits);
        var hex = new List<string>();
        for (int shift = 12; shift >= 0; shift = shift - 4) hex.Add(d.Slice((unit >> shift) & 15, 1).ToString());
        return "\\u" + string.Join("", hex);
    }

    // 10 進の非負の整数 (それ以外は -1)
    public static int ParseInt(string s)
    {
        var u = new Utf8Bytes(s);
        if (u.Length == 0 || u.Length > 9) return -1;
        int v = 0;
        for (int i = 0; i < u.Length; i++)
        {
            int c = u[i];
            if (c < '0' || c > '9') return -1;
            v = v * 10 + (c - '0');
        }
        return v;
    }
}
