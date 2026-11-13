// UTF-8 の byte の桁と、見た目の桁の変換。
// 東アジアの全角の文字 (Unicode の East Asian Width が W か F) は 2 桁、ほかの文字は 1 桁。
// エディタの core (src/Editor) の幅と同じ数え方。
using System;
using System.Collections.Generic;
using TinySystem.Text;

namespace LiderCore;

public static class Utf8
{
    // 先頭の byte から、その文字の byte 数
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

    // s の at byte 目から始まる文字の codepoint
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

    // 文字の桁の数 (全角は 2)
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

    // s の先頭から byteCol byte までの桁数
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

    // s 全体の桁数
    public static int Width(string s)
    {
        return Cells(s, new Utf8Bytes(s).Length);
    }

    // s を cells 桁に収める。
    // 収まらなければ、cells - 1 桁に収まる頭に … (1 桁) を付け、切ったことを見せる (切った数が別の数に見えないように)
    public static string Fit(string s, int cells)
    {
        if (cells <= 0)
            return "";
        var u = new Utf8Bytes(s);
        var cell = 0;
        var at = 0;
        // cells - 1 桁に収まる頭の byte の数
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

    // s の byte の数
    public static int Bytes(string s)
    {
        return new Utf8Bytes(s).Length;
    }

    // s の byte の並び (ASCII の文字を 1 つずつ見るのに使う)
    public static List<int> ByteList(string s)
    {
        var u = new Utf8Bytes(s);
        var list = new List<int>();
        for (var i = 0; i < u.Length; i++)
            list.Add(u[i]);
        return list;
    }

    // s の i byte 目 (0 から)
    public static int At(string s, int i)
    {
        return new Utf8Bytes(s)[i];
    }

    // s の start byte 目 (0 から) から count byte
    public static string Sub(string s, int start, int count)
    {
        return new Utf8Bytes(s).Slice(start, count).ToString();
    }

    // s の start byte 目 (0 から) から最後まで
    public static string Sub(string s, int start)
    {
        var u = new Utf8Bytes(s);
        return u.Slice(start, u.Length - start).ToString();
    }

    // s の from byte 目より後で needle が最初に現れる byte の位置 (無ければ -1)
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

    // s の中で needle が最後に現れる byte の位置 (無ければ -1)
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

    // 全角の範囲 (first, last, step) の並び。Lite XL 2.1.8 の src/unidata.h の doublewidth_table と同じ
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
