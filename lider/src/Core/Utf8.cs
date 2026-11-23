// Conversion between UTF-8 byte columns and display columns.
// East Asian full-width characters (Unicode East Asian Width W or F) take 2 columns; other characters take 1.
// Counts the same way as the width in the editor core (src/Editor).
using System;
using System.Collections.Generic;
using TinySystem.Text;

namespace LiderCore;

public static class Utf8
{
    // Byte count of a character, from its first byte
    public static int RuneBytes(int lead)
    {
        if (lead < 0x80)
            return 1;
        if (lead >= 0xF0)
            return 4;
        if (lead >= 0xE0)
            return 3;
        if (lead >= 0xC0)
            return 2;
        return 1;
    }

    // Codepoint of the character starting at byte at of s
    public static int Decode(string s, int at)
    {
        return Decode(new Utf8Bytes(s), at);
    }

    public static int Decode(Utf8Bytes u, int at)
    {
        int lead = u[at];
        var n = RuneBytes(lead);
        if (n == 1 || at + n > u.Length)
            return lead;
        var cp = lead & (0x7F >> n);
        for (var i = 1; i < n; i++)
            cp = (cp << 6) | (u[at + i] & 0x3F);
        return cp;
    }

    public static string Encode(int cp)
    {
        return char.ConvertFromUtf32(cp);
    }

    // Column count of a character (2 for full-width)
    public static int CellWidth(int cp)
    {
        if (cp < 0x1100)
            return 1;
        var lo = 0;
        var hi = Wide.Length / 3;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            var b = mid * 3;
            if (Wide[b + 1] < cp)
                lo = mid + 1;
            else if (Wide[b] > cp)
                hi = mid;
            else
                return (cp - Wide[b]) % Wide[b + 2] == 0 ? 2 : 1;
        }
        return 1;
    }

    // Column count from the start of s up to byte byteCol
    public static int Cells(string s, int byteCol)
    {
        var u = new Utf8Bytes(s);
        var cell = 0;
        var at = 0;
        while (at < byteCol && at < u.Length)
        {
            cell += CellWidth(Decode(u, at));
            at += RuneBytes(u[at]);
        }
        return cell;
    }

    // Column count of the whole s
    public static int Width(string s)
    {
        return Cells(s, new Utf8Bytes(s).Length);
    }

    // Fits s into cells columns.
    // If it does not fit, appends … (1 column) to the head that fits in cells - 1 columns, to show it was cut (so a cut number does not look like a different number)
    public static string Fit(string s, int cells)
    {
        if (cells <= 0)
            return "";
        var u = new Utf8Bytes(s);
        var cell = 0;
        var at = 0;
        // Byte count of the head that fits in cells - 1 columns
        var head = -1;
        while (at < u.Length)
        {
            var w = CellWidth(Decode(u, at));
            if (head < 0 && cell + w > cells - 1)
                head = at;
            cell += w;
            if (cell > cells)
                return u.Slice(0, head).ToString() + "…";
            at += RuneBytes(u[at]);
        }
        return s;
    }

    // Byte count of s
    public static int Bytes(string s)
    {
        return new Utf8Bytes(s).Length;
    }

    // Bytes of s (used to look at ASCII characters one by one)
    public static List<int> ByteList(string s)
    {
        var u = new Utf8Bytes(s);
        var list = new List<int>();
        for (var i = 0; i < u.Length; i++)
            list.Add(u[i]);
        return list;
    }

    // Byte i of s (from 0)
    public static int At(string s, int i)
    {
        return new Utf8Bytes(s)[i];
    }

    // count bytes of s from byte start (from 0)
    public static string Sub(string s, int start, int count)
    {
        return new Utf8Bytes(s).Slice(start, count).ToString();
    }

    // s from byte start (from 0) to the end
    public static string Sub(string s, int start)
    {
        var u = new Utf8Bytes(s);
        return u.Slice(start, u.Length - start).ToString();
    }

    // Byte position of the first occurrence of needle in s from byte from onward (-1 if none)
    public static int IndexOf(string s, string needle, int from = 0)
    {
        var h = new Utf8Bytes(s);
        var n = new Utf8Bytes(needle);
        if (n.Length == 0)
            return from <= h.Length ? from : -1;
        for (var i = from; i + n.Length <= h.Length; i++)
        {
            var j = 0;
            while (j < n.Length && h[i + j] == n[j])
                j++;
            if (j == n.Length)
                return i;
        }
        return -1;
    }

    // Byte position of the last occurrence of needle in s (-1 if none)
    public static int LastIndexOf(string s, string needle)
    {
        var at = -1;
        var i = IndexOf(s, needle, 0);
        while (i >= 0)
        {
            at = i;
            i = IndexOf(s, needle, i + 1);
        }
        return at;
    }

    // List of full-width ranges (first, last, step). Same as doublewidth_table in src/unidata.h of Lite XL 2.1.8
    static readonly int[] Wide = new int[]
    {
        0x1100, 0x115F, 0x1,  0x231A, 0x231B, 0x1,  0x2329, 0x232A, 0x1,  0x23E9, 0x23EC, 0x1,
        0x23F0, 0x23F3, 0x3,  0x25FD, 0x25FE, 0x1,  0x2614, 0x2615, 0x1,  0x2648, 0x2653, 0x1,
        0x267F, 0x2693, 0x14,  0x26A1, 0x26AA, 0x9,  0x26AB, 0x26BD, 0x12,  0x26BE, 0x26C4, 0x6,
        0x26C5, 0x26CE, 0x9,  0x26D4, 0x26EA, 0x16,  0x26F2, 0x26F3, 0x1,  0x26F5, 0x26FA, 0x5,
        0x26FD, 0x2705, 0x8,  0x270A, 0x270B, 0x1,  0x2728, 0x274C, 0x24,  0x274E, 0x2753, 0x5,
        0x2754, 0x2755, 0x1,  0x2757, 0x2795, 0x3E,  0x2796, 0x2797, 0x1,  0x27B0, 0x27BF, 0xF,
        0x2B1B, 0x2B1C, 0x1,  0x2B50, 0x2B55, 0x5,  0x2E80, 0x2E99, 0x1,  0x2E9B, 0x2EF3, 0x1,
        0x2F00, 0x2FD5, 0x1,  0x2FF0, 0x2FFB, 0x1,  0x3000, 0x303E, 0x1,  0x3041, 0x3096, 0x1,
        0x3099, 0x30FF, 0x1,  0x3105, 0x312F, 0x1,  0x3131, 0x318E, 0x1,  0x3190, 0x31E3, 0x1,
        0x31F0, 0x321E, 0x1,  0x3220, 0x3247, 0x1,  0x3250, 0x4DBF, 0x1,  0x4E00, 0xA48C, 0x1,
        0xA490, 0xA4C6, 0x1,  0xA960, 0xA97C, 0x1,  0xAC00, 0xD7A3, 0x1,  0xF900, 0xFAFF, 0x1,
        0xFE10, 0xFE19, 0x1,  0xFE30, 0xFE52, 0x1,  0xFE54, 0xFE66, 0x1,  0xFE68, 0xFE6B, 0x1,
        0xFF01, 0xFF60, 0x1,  0xFFE0, 0xFFE6, 0x1,  0x16FE0, 0x16FE4, 0x1,  0x16FF0, 0x16FF1, 0x1,
        0x17000, 0x187F7, 0x1,  0x18800, 0x18CD5, 0x1,  0x18D00, 0x18D08, 0x1,  0x1AFF0, 0x1AFF3, 0x1,
        0x1AFF5, 0x1AFFB, 0x1,  0x1AFFD, 0x1AFFE, 0x1,  0x1B000, 0x1B122, 0x1,  0x1B132, 0x1B150, 0x1E,
        0x1B151, 0x1B152, 0x1,  0x1B155, 0x1B164, 0xF,  0x1B165, 0x1B167, 0x1,  0x1B170, 0x1B2FB, 0x1,
        0x1F004, 0x1F0CF, 0xCB,  0x1F18E, 0x1F191, 0x3,  0x1F192, 0x1F19A, 0x1,  0x1F200, 0x1F202, 0x1,
        0x1F210, 0x1F23B, 0x1,  0x1F240, 0x1F248, 0x1,  0x1F250, 0x1F251, 0x1,  0x1F260, 0x1F265, 0x1,
        0x1F300, 0x1F320, 0x1,  0x1F32D, 0x1F335, 0x1,  0x1F337, 0x1F37C, 0x1,  0x1F37E, 0x1F393, 0x1,
        0x1F3A0, 0x1F3CA, 0x1,  0x1F3CF, 0x1F3D3, 0x1,  0x1F3E0, 0x1F3F0, 0x1,  0x1F3F4, 0x1F3F8, 0x4,
        0x1F3F9, 0x1F43E, 0x1,  0x1F440, 0x1F442, 0x2,  0x1F443, 0x1F4FC, 0x1,  0x1F4FF, 0x1F53D, 0x1,
        0x1F54B, 0x1F54E, 0x1,  0x1F550, 0x1F567, 0x1,  0x1F57A, 0x1F595, 0x1B,  0x1F596, 0x1F5A4, 0xE,
        0x1F5FB, 0x1F64F, 0x1,  0x1F680, 0x1F6C5, 0x1,  0x1F6CC, 0x1F6D0, 0x4,  0x1F6D1, 0x1F6D2, 0x1,
        0x1F6D5, 0x1F6D7, 0x1,  0x1F6DC, 0x1F6DF, 0x1,  0x1F6EB, 0x1F6EC, 0x1,  0x1F6F4, 0x1F6FC, 0x1,
        0x1F7E0, 0x1F7EB, 0x1,  0x1F7F0, 0x1F90C, 0x11C,  0x1F90D, 0x1F93A, 0x1,  0x1F93C, 0x1F945, 0x1,
        0x1F947, 0x1F9FF, 0x1,  0x1FA70, 0x1FA7C, 0x1,  0x1FA80, 0x1FA88, 0x1,  0x1FA90, 0x1FABD, 0x1,
        0x1FABF, 0x1FAC5, 0x1,  0x1FACE, 0x1FADB, 0x1,  0x1FAE0, 0x1FAE8, 0x1,  0x1FAF0, 0x1FAF8, 0x1,
        0x20000, 0x2FFFD, 0x1,  0x30000, 0x3FFFD, 0x1,
    };
}
