// Matches the lines of two versions with the Myers diff.
// Strips identical leading and trailing lines first and compares only the remaining middle (diffs while typing are small, so this is usually enough).
// If the middle differs by more than MaxEdits, all middle lines are treated as unmatched.
using System.Collections.Generic;

namespace LiderCore;

public static class LineDiff
{
    public const int MaxEdits = 400;

    // For each line of after, the index of the same line in before (-1 if it matches none)
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

        // v[k + off]: the furthest x on diagonal k (x - y). trace[d] is the part of v for k in -d..d before the d-th edit
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

        // Walking back from the end to the point before each edit, matches the lines on the diagonals passed
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
