using System.Collections.Generic;
using TinySystem.Text;

namespace TcsSelf;

// String helpers for the tool (decimal number parsing)
public static class ToolText
{
    // Non-negative decimal integer (-1 otherwise)
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
