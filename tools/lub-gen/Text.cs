using System.Collections.Generic;
using TinySystem.Text;

namespace LubGen;

/// <summary>文字列の小さな道具 (TinyC# の string は UTF-8 で、位置は byte で数える)。</summary>
public static class Text
{
    /// <summary>byte の長さ。</summary>
    public static int Len(string s) => new Utf8Bytes(s).Length;

    /// <summary>byte の位置 start から count byte。</summary>
    public static string Sub(string s, int start, int count) => new Utf8Bytes(s).Slice(start, count).ToString();

    /// <summary>byte の位置 start から終わりまで。</summary>
    public static string From(string s, int start)
    {
        var u = new Utf8Bytes(s);
        if (start >= u.Length) return "";
        return u.Slice(start, u.Length - start).ToString();
    }

    /// <summary>終わりの n byte を除いたもの。</summary>
    public static string DropEnd(string s, int n)
    {
        var u = new Utf8Bytes(s);
        return u.Slice(0, u.Length - n).ToString();
    }

    /// <summary>needle の最初の byte の位置 (無ければ -1)。</summary>
    public static int IndexOf(string s, string needle, int from)
    {
        var u = new Utf8Bytes(s);
        var n = new Utf8Bytes(needle);
        for (int i = from; i + n.Length <= u.Length; i++)
        {
            bool ok = true;
            for (int k = 0; k < n.Length; k++)
            {
                if (u[i + k] != n[k])
                {
                    ok = false;
                    break;
                }
            }
            if (ok) return i;
        }
        return -1;
    }

    /// <summary>行に分ける (\n で。\r は行の末に残す)。</summary>
    public static List<string> Lines(string text)
    {
        var r = new List<string>();
        foreach (var l in text.Split('\n')) r.Add(l);
        return r;
    }

    private static bool IsSpaceByte(int c) => c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == 11 || c == 12;

    public static string Trim(string s) => s.Trim();

    public static string TrimStart(string s)
    {
        var u = new Utf8Bytes(s);
        int i = 0;
        while (i < u.Length && IsSpaceByte(u[i])) i++;
        return u.Slice(i, u.Length - i).ToString();
    }

    public static string TrimEnd(string s)
    {
        var u = new Utf8Bytes(s);
        int n = u.Length;
        while (n > 0 && IsSpaceByte(u[n - 1])) n--;
        return u.Slice(0, n).ToString();
    }

    /// <summary>open と close の間 (最初の組)。無ければ ""。</summary>
    public static string Between(string s, string open, string close)
    {
        int a = IndexOf(s, open, 0);
        if (a < 0) return "";
        int start = a + Len(open);
        int b = IndexOf(s, close, start);
        if (b < 0) return "";
        return Sub(s, start, b - start);
    }

    /// <summary>`<...>` の tag を除く。</summary>
    public static string StripTags(string s)
    {
        var u = new Utf8Bytes(s);
        var parts = new List<string>();
        int run = 0;
        int i = 0;
        while (i < u.Length)
        {
            if (u[i] == '<')
            {
                int j = i + 1;
                while (j < u.Length && u[j] != '>') j++;
                if (j < u.Length && j > i + 1)
                {
                    if (i > run) parts.Add(u.Slice(run, i - run).ToString());
                    i = j + 1;
                    run = i;
                    continue;
                }
            }
            i++;
        }
        if (u.Length > run) parts.Add(u.Slice(run, u.Length - run).ToString());
        return string.Join("", parts);
    }

    /// <summary>doc comment の XML の tag が入れ子で閉じ、実体参照が既知のものか (C# の compiler は整形式でない doc を捨てる)。</summary>
    public static bool WellFormedXml(string s)
    {
        var u = new Utf8Bytes(s);
        var stack = new List<string>();
        int i = 0;
        while (i < u.Length)
        {
            int c = u[i];
            if (c == '&')
            {
                int j = i + 1;
                while (j < u.Length && u[j] != ';' && u[j] != ' ' && u[j] != '&' && u[j] != '<') j++;
                if (j >= u.Length || u[j] != ';') return false;
                string name = u.Slice(i + 1, j - i - 1).ToString();
                if (!(name == "lt" || name == "gt" || name == "amp" || name == "quot" || name == "apos" || name.StartsWith("#"))) return false;
                i = j + 1;
                continue;
            }
            if (c == '>')
            {
                i++;
                continue;
            }
            if (c != '<')
            {
                i++;
                continue;
            }
            int k = i + 1;
            while (k < u.Length && u[k] != '>' && u[k] != '<') k++;
            if (k >= u.Length || u[k] != '>') return false;
            string tag = u.Slice(i + 1, k - i - 1).ToString();
            i = k + 1;
            if (tag.StartsWith("!--") || tag.StartsWith("?")) continue;
            if (tag.EndsWith("/")) continue;
            bool closing = tag.StartsWith("/");
            string body = closing ? From(tag, 1) : tag;
            string name2 = body.Trim().Split(' ')[0];
            if (name2 == "") return false;
            if (closing)
            {
                if (stack.Count == 0 || stack[stack.Count - 1] != name2) return false;
                stack.RemoveAt(stack.Count - 1);
            }
            else stack.Add(name2);
        }
        return stack.Count == 0;
    }

    /// <summary>空白と tab の続きを空白 1 つにする。</summary>
    public static string CollapseSpaces(string s)
    {
        var u = new Utf8Bytes(s);
        var parts = new List<string>();
        int run = 0;
        int i = 0;
        while (i < u.Length)
        {
            if (u[i] == ' ' || u[i] == '\t')
            {
                int j = i;
                while (j < u.Length && (u[j] == ' ' || u[j] == '\t')) j++;
                if (i > run) parts.Add(u.Slice(run, i - run).ToString());
                parts.Add(" ");
                i = j;
                run = j;
                continue;
            }
            i++;
        }
        if (u.Length > run) parts.Add(u.Slice(run, u.Length - run).ToString());
        return string.Join("", parts);
    }

    /// <summary>C# 版の IsWide (UTF-16 の 1 文字が 0x2E7F より大きく、surrogate でない)。</summary>
    public static bool IsWide(int rune) => rune > 0x2E7F && rune < 0x10000;

    public static int FirstRune(string s)
    {
        foreach (var r in s.EnumerateRunes()) return r.Value;
        return 0;
    }

    public static int LastRune(string s)
    {
        int last = 0;
        foreach (var r in s.EnumerateRunes()) last = r.Value;
        return last;
    }

    /// <summary>10 進の整数 (読めなければ 0)。</summary>
    public static int ParseInt(string s)
    {
        var u = new Utf8Bytes(s);
        int v = 0;
        int i = 0;
        bool neg = false;
        if (u.Length > 0 && u[0] == '-')
        {
            neg = true;
            i = 1;
        }
        for (; i < u.Length; i++)
        {
            int c = u[i];
            if (c < '0' || c > '9') break;
            v = v * 10 + (c - '0');
        }
        return neg ? -v : v;
    }

    /// <summary>ASCII の小文字を含むか。</summary>
    public static bool HasLower(string s)
    {
        var u = new Utf8Bytes(s);
        for (int i = 0; i < u.Length; i++)
        {
            if (u[i] >= 'a' && u[i] <= 'z') return true;
        }
        return false;
    }

    /// <summary>先頭の 1 byte を小文字にしたもの (ASCII)。</summary>
    public static string LowerFirst(string s)
    {
        if (s == "") return s;
        return Sub(s, 0, 1).ToLower() + From(s, 1);
    }

    /// <summary>先頭の 1 byte を大文字にしたもの (ASCII)。</summary>
    public static string UpperFirst(string s)
    {
        if (s == "") return s;
        return Sub(s, 0, 1).ToUpper() + From(s, 1);
    }

    /// <summary>s を n 回つないだもの。</summary>
    public static string Repeat(string s, int n)
    {
        var parts = new List<string>();
        for (int i = 0; i < n; i++) parts.Add(s);
        return string.Join("", parts);
    }

    /// <summary>行を足していく出力 (C# 版の StringBuilder.AppendLine)。</summary>
    public static string Joined(List<string> parts) => string.Join("", parts);
}

/// <summary>出力を貯める (C# 版の StringBuilder)。行は \n で終える。</summary>
public sealed class Out
{
    private List<string> _parts = new List<string>();

    public void Line(string text)
    {
        _parts.Add(text);
        _parts.Add("\n");
    }

    public void Blank() => _parts.Add("\n");

    public void Write(string text) => _parts.Add(text);

    public override string ToString() => string.Join("", _parts);
}
