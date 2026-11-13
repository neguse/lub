// あいまいな一致の点 (Lite XL の system.fuzzy_match を写したもの)。
// text が query の文字を順に含むかを、大きいほど良い点にする。空白は読まない。大文字と小文字は区別しない (区別した一致に少し足す)。
// files なら後ろから比べる (path の名前の側を重く見る)。
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
