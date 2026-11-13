// エディタの core が使う文字列の道具。
// 文字列は UTF-8 で、位置は byte で数える (Lite XL と同じ)。
// Lua の string の関数 (sub、find、rep) と同じ結果を返すものは、名前と引数をそれに合わせる。
using System;
using TinySystem.Text;
using LiderCore;

namespace LiderEditor;

public static class Str
{
    // Lite XL の math.huge の代わり (行と桁の上限)
    public const int Huge = 1 << 30;

    public static int Len(string s)
    {
        return new Utf8Bytes(s).Length;
    }

    // start byte 目 (0 から) から count byte。範囲の外は切り詰める
    public static string Slice(string s, int start, int count)
    {
        var u = new Utf8Bytes(s);
        if (start < 0)
            start = 0;
        if (start > u.Length)
            start = u.Length;
        if (count > u.Length - start)
            count = u.Length - start;
        if (count <= 0)
            return "";
        if (start == 0 && count == u.Length)
            return s;
        return u.Slice(start, count).ToString();
    }

    // start byte 目 (0 から) から最後まで
    public static string From(string s, int start)
    {
        return Slice(s, start, Huge);
    }

    // Lua の string.sub (i と j は 1 から、負は後ろから数える)
    public static string Sub(string s, int i, int j)
    {
        var n = Len(s);
        if (i < 0)
            i = Math.Max(n + i + 1, 1);
        else if (i == 0)
            i = 1;
        if (j < 0)
            j = n + j + 1;
        else if (j > n)
            j = n;
        if (i > j)
            return "";
        return Slice(s, i - 1, j - i + 1);
    }

    // at byte 目 (0 から) の byte (範囲の外は -1)
    public static int Byte(string s, int at)
    {
        var u = new Utf8Bytes(s);
        return at >= 0 && at < u.Length ? u[at] : -1;
    }

    // UTF-8 の文字の 2 byte 目より後の byte か
    public static bool IsCont(int b)
    {
        return b >= 0x80 && b < 0xC0;
    }

    // Lua の %s
    public static bool IsSpace(int b)
    {
        return b == 32 || (b >= 9 && b <= 13);
    }

    // s の from byte 目 (0 から) より後で、needle が最初に現れる byte の位置 (無ければ -1)
    public static int Find(string s, string needle, int from)
    {
        var h = new Utf8Bytes(s);
        var n = new Utf8Bytes(needle);
        if (from < 0)
            from = 0;
        if (n.Length == 0)
            return from <= h.Length ? from : -1;
        var first = n[0];
        for (var i = from; i + n.Length <= h.Length; i++)
        {
            if (h[i] != first)
                continue;
            var j = 1;
            while (j < n.Length && h[i + j] == n[j])
                j++;
            if (j == n.Length)
                return i;
        }
        return -1;
    }

    // s が from byte 目 (0 から) に prefix を持つか
    public static bool At(string s, int from, string prefix)
    {
        var h = new Utf8Bytes(s);
        var p = new Utf8Bytes(prefix);
        if (from < 0 || from + p.Length > h.Length)
            return false;
        for (var i = 0; i < p.Length; i++)
            if (h[from + i] != p[i])
                return false;
        return true;
    }

    public static string Repeat(string s, int n)
    {
        var r = "";
        for (var i = 0; i < n; i++)
            r += s;
        return r;
    }

    // 先頭の空白と tab の byte の数 (Lua の ^[ \t]*)
    public static int IndentLength(string s)
    {
        var u = new Utf8Bytes(s);
        var i = 0;
        while (i < u.Length && (u[i] == 32 || u[i] == 9))
            i++;
        return i;
    }

    // from byte 目 (0 から) から後が空白だけか
    public static bool BlankFrom(string s, int from)
    {
        var u = new Utf8Bytes(s);
        for (var i = Math.Max(0, from); i < u.Length; i++)
            if (!IsSpace(u[i]))
                return false;
        return true;
    }

    // 最初の空白でない byte の位置 (0 から。無ければ -1)
    public static int FirstInk(string s)
    {
        var u = new Utf8Bytes(s);
        for (var i = 0; i < u.Length; i++)
            if (!IsSpace(u[i]))
                return i;
        return -1;
    }

    // 先頭の byte から、その文字の byte の数
    public static int RuneBytes(int lead)
    {
        return Utf8.RuneBytes(lead);
    }

    // u の at byte 目から始まる文字の codepoint
    public static int Decode(Utf8Bytes u, int at)
    {
        int lead = u[at];
        var n = Utf8.RuneBytes(lead);
        if (n == 1 || at + n > u.Length)
            return lead;
        var cp = lead & (0x7F >> n);
        for (var i = 1; i < n; i++)
            cp = (cp << 6) | (u[at + i] & 0x3F);
        return cp;
    }

    // 桁の数 (tab は tab 桁、全角は 2 桁)
    public static int Cells(string s, int tab)
    {
        var u = new Utf8Bytes(s);
        var cells = 0;
        var at = 0;
        while (at < u.Length)
        {
            int b = u[at];
            if (b < 0x80)
            {
                cells += b == 9 ? tab : 1;
                at++;
                continue;
            }
            cells += Utf8.CellWidth(Decode(u, at));
            at += Math.Max(1, Utf8.RuneBytes(b));
        }
        return cells;
    }

    // tab を tab 桁の空白にする
    public static string ExpandTabs(string s, int tab)
    {
        if (!s.Contains("\t"))
            return s;
        return s.Replace("\t", Repeat(" ", tab));
    }

    // Lua の string.lower (ASCII だけ)
    public static string Lower(string s)
    {
        return s.ToLower();
    }
}
