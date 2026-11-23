// Fuzzy match score (a port of Lite XL's system.fuzzy_match).
// Turns whether text contains the characters of query in order into a score where higher is better. Whitespace is ignored. Case-insensitive (adds a little for case-sensitive matches).
// For files, compares from the end (weighting the name side of the path).
using System;
using TinySystem.Text;

namespace LiderEditor;

public static class Fuzzy
{
    public const int NoMatch = -1000000000;

    public static int Match(string str, string ptn, bool files)
    {
        var s = new Utf8Bytes(str);
        var p = new Utf8Bytes(ptn);
        var slen = s.Length;
        var plen = p.Length;
        var score = 0;
        var run = 0;
        var inc = files ? -1 : 1;
        var si = files ? slen - 1 : 0;
        var pi = files ? plen - 1 : 0;
        while (si >= 0 && si < slen && pi >= 0 && pi < plen)
        {
            while (si >= 0 && si < slen && s[si] == 32)
                si += inc;
            while (pi >= 0 && pi < plen && p[pi] == 32)
                pi += inc;
            if (si < 0 || si >= slen || pi < 0 || pi >= plen)
                break;
            int a = s[si];
            int b = p[pi];
            var la = a >= 65 && a <= 90 ? a + 32 : a;
            var lb = b >= 65 && b <= 90 ? b + 32 : b;
            if (la == lb)
            {
                score += run * 10 - (a != b ? 1 : 0);
                run++;
                pi += inc;
            }
            else
            {
                score -= 10;
                run = 0;
            }
            si += inc;
        }
        if (pi >= 0 && pi < plen)
            return NoMatch;
        return score - slen * 10;
    }
}
