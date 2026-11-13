// 2 つの版の行の対応を、Myers の差分で取る。
// 前後の同じ行を先に除き、残りの真ん中だけを比べる (打っている間の差分は小さいので、ほとんどはそこで済む)。
// 真ん中の違いが MaxEdits を超えたら、真ん中の行は全部対応しないとみなす。
using System.Collections.Generic;

namespace LiderCore;

public static class LineDiff
{
    public const int MaxEdits = 400;

    // after の行ごとに、before で同じ行の番号 (どれとも対応しなければ -1)
    public static int[] Match(List<string> before, List<string> after)
    {
        var n = before.Count;
        var m = after.Count;
        var map = new int[m];
        for (var i = 0; i < m; i++)
            map[i] = -1;
        var head = 0;
        while (head < n && head < m && before[head] == after[head])
        {
            map[head] = head;
            head++;
        }
        var tail = 0;
        while (tail < n - head && tail < m - head && before[n - 1 - tail] == after[m - 1 - tail])
        {
            map[m - 1 - tail] = n - 1 - tail;
            tail++;
        }
        var a = n - head - tail;
        var b = m - head - tail;
        if (a == 0 || b == 0)
            return map;

        // v[k + off]: 対角線 k (x - y) で一番進んだ x。trace[d] は d 回目の編集の前の v の、k が -d..d の所
        var max = a + b;
        var off = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();
        var edits = -1;
        for (var d = 0; d <= max && d <= MaxEdits; d++)
        {
            var snap = new int[2 * d + 1];
            for (var k = -d; k <= d; k++)
                snap[k + d] = v[k + off];
            trace.Add(snap);
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[k - 1 + off] < v[k + 1 + off]) ? v[k + 1 + off] : v[k - 1 + off] + 1;
                var y = x - k;
                while (x < a && y < b && before[head + x] == after[head + y])
                {
                    x++;
                    y++;
                }
                v[k + off] = x;
                if (x >= a && y >= b)
                {
                    edits = d;
                    break;
                }
            }
            if (edits >= 0)
                break;
        }
        if (edits < 0)
            return map;

        // 終わりから、各編集の前の点へ戻りながら、通った対角線の行を対応させる
        var cx = a;
        var cy = b;
        for (var d = edits; d > 0; d--)
        {
            var snap = trace[d];
            var k = cx - cy;
            var prevK = k == -d || (k != d && snap[k - 1 + d] < snap[k + 1 + d]) ? k + 1 : k - 1;
            var px = snap[prevK + d];
            var py = px - prevK;
            while (cx > px && cy > py)
            {
                cx--;
                cy--;
                map[head + cy] = head + cx;
            }
            cx = px;
            cy = py;
        }
        while (cx > 0 && cy > 0)
        {
            cx--;
            cy--;
            map[head + cy] = head + cx;
        }
        return map;
    }
}
