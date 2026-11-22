// String tools used by the editor core.
// Strings are UTF-8 and positions count bytes (same as Lite XL).
// Functions that return the same results as Lua's string functions (sub, find, rep) match their names and arguments.
using System;
using TinySystem.Text;
using LiderCore;

namespace LiderEditor;

public static class Str
{
    // Replacement for Lite XL's math.huge (upper limit of lines and columns)
    public const int Huge = 1 << 30;

    public static int Len(string s)
    {
        return new Utf8Bytes(s).Length;
    }

    // count bytes from byte start (from 0). Clamps outside the range
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

    // From byte start (from 0) to the end
    public static string From(string s, int start)
    {
        return Slice(s, start, Huge);
    }

    // Lua's string.sub (i and j from 1, negatives count from the end)
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

    // The byte at byte at (from 0) (-1 outside the range)
    public static int Byte(string s, int at)
    {
        var u = new Utf8Bytes(s);
        return at >= 0 && at < u.Length ? u[at] : -1;
    }

    // Whether it is a byte from the 2nd byte onward of a UTF-8 character
    public static bool IsCont(int b)
    {
        return b >= 0x80 && b < 0xC0;
    }

    // Lua's %s
    public static bool IsSpace(int b)
    {
        return b == 32 || (b >= 9 && b <= 13);
    }

    // Byte position of the first occurrence of needle in s from byte from (from 0) onward (-1 if none)
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

    // Whether s has prefix at byte from (from 0)
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

    // Byte count of the leading spaces and tabs (Lua's ^[ \t]*)
    public static int IndentLength(string s)
    {
        var u = new Utf8Bytes(s);
        var i = 0;
        while (i < u.Length && (u[i] == 32 || u[i] == 9))
            i++;
        return i;
    }

    // Whether everything from byte from (from 0) onward is whitespace
    public static bool BlankFrom(string s, int from)
    {
        var u = new Utf8Bytes(s);
        for (var i = Math.Max(0, from); i < u.Length; i++)
            if (!IsSpace(u[i]))
                return false;
        return true;
    }

    // Position of the first non-whitespace byte (from 0; -1 if none)
    public static int FirstInk(string s)
    {
        var u = new Utf8Bytes(s);
        for (var i = 0; i < u.Length; i++)
            if (!IsSpace(u[i]))
                return i;
        return -1;
    }

    // Byte count of a character, from its first byte
    public static int RuneBytes(int lead)
    {
        return Utf8.RuneBytes(lead);
    }

    // Codepoint of the character starting at byte at of u
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

    // Column count (tab is tab columns, full-width is 2 columns)
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

    // Turns tabs into tab columns of spaces
    public static string ExpandTabs(string s, int tab)
    {
        if (!s.Contains("\t"))
            return s;
        return s.Replace("\t", Repeat(" ", tab));
    }

    // Lua's string.lower (ASCII only)
    public static string Lower(string s)
    {
        return s.ToLower();
    }
}
