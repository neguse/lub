using System.Collections.Generic;
using TinySystem.Text;

namespace TcsSelf;

// source map (`<output>.lua.map`) の JSON と、Lua の stack trace の行に C# の位置を足す処理 (`tcs --map-stacktrace`)
public sealed class SourceMapData
{
    // Lua の行の昇順
    public List<int> LuaLines = new List<int>();
    public List<string> Files = new List<string>();
    public List<int> Lines = new List<int>();
}

public static class SourceMapText
{
    // Lua の行 → C# の位置。同じ Lua の行は後の項が勝つ
    public static SourceMapData Sorted(List<SourceMapEntry> entries, int luaLineOffset)
    {
        var map = new SourceMapData();
        foreach (var e in entries) Put(map, e.LuaLine + luaLineOffset, e.File, e.Line);
        return map;
    }

    private static void Put(SourceMapData map, int luaLine, string file, int line)
    {
        int lo = 0;
        int hi = map.LuaLines.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (map.LuaLines[mid] < luaLine) lo = mid + 1;
            else hi = mid;
        }
        if (lo < map.LuaLines.Count && map.LuaLines[lo] == luaLine)
        {
            map.Files[lo] = file;
            map.Lines[lo] = line;
            return;
        }
        map.LuaLines.Insert(lo, luaLine);
        map.Files.Insert(lo, file);
        map.Lines.Insert(lo, line);
    }

    public static string ToJson(List<SourceMapEntry> entries, int luaLineOffset)
    {
        var map = Sorted(entries, luaLineOffset);
        var parts = new List<string>();
        parts.Add("{\n");
        parts.Add("  \"version\": 1,\n");
        parts.Add("  \"mappings\": {\n");
        for (int i = 0; i < map.LuaLines.Count; i++)
        {
            if (i > 0) parts.Add(",\n");
            parts.Add("    \"" + map.LuaLines[i] + "\": {\"file\": \"" + JsonEscape(map.Files[i]) + "\", \"line\": " + map.Lines[i] + "}");
        }
        parts.Add("\n");
        parts.Add("  }\n");
        parts.Add("}");
        return string.Join("", parts);
    }

    // .NET の JsonEncodedText.Encode と同じ escape (非 ASCII と HTML で意味を持つ文字は \uXXXX)
    public static string JsonEscape(string s)
    {
        var u = new Utf8Bytes(s);
        var parts = new List<string>();
        int start = 0;
        int i = 0;
        while (i < u.Length)
        {
            int c = u[i];
            if (c >= 0x20 && c < 0x7F && c != '"' && c != '\\' && c != '<' && c != '>' && c != '&' && c != '\'' && c != '+' && c != '`')
            {
                i++;
                continue;
            }
            if (i > start) parts.Add(u.Slice(start, i - start).ToString());
            int len = 1;
            if (c == '\\') parts.Add("\\\\");
            else if (c == '\n') parts.Add("\\n");
            else if (c == '\r') parts.Add("\\r");
            else if (c == '\t') parts.Add("\\t");
            else if (c == 8) parts.Add("\\b");
            else if (c == 12) parts.Add("\\f");
            else if (c < 0x80) parts.Add(Unicode(c));
            else
            {
                int cp = c;
                if (c >= 0xF0 && i + 3 < u.Length)
                {
                    cp = ((c & 0x07) << 18) | ((u[i + 1] & 0x3F) << 12) | ((u[i + 2] & 0x3F) << 6) | (u[i + 3] & 0x3F);
                    len = 4;
                }
                else if (c >= 0xE0 && i + 2 < u.Length)
                {
                    cp = ((c & 0x0F) << 12) | ((u[i + 1] & 0x3F) << 6) | (u[i + 2] & 0x3F);
                    len = 3;
                }
                else if (c >= 0xC0 && i + 1 < u.Length)
                {
                    cp = ((c & 0x1F) << 6) | (u[i + 1] & 0x3F);
                    len = 2;
                }
                if (cp >= 0x10000)
                {
                    int v = cp - 0x10000;
                    parts.Add(Unicode(0xD800 + (v >> 10)));
                    parts.Add(Unicode(0xDC00 + (v & 0x3FF)));
                }
                else parts.Add(Unicode(cp));
            }
            i = i + len;
            start = i;
        }
        if (start == 0) return s;
        if (start < u.Length) parts.Add(u.Slice(start, u.Length - start).ToString());
        return string.Join("", parts);
    }

    private static string Unicode(int unit)
    {
        const string digits = "0123456789ABCDEF";
        var d = new Utf8Bytes(digits);
        var hex = new List<string>();
        for (int shift = 12; shift >= 0; shift = shift - 4) hex.Add(d.Slice((unit >> shift) & 15, 1).ToString());
        return "\\u" + string.Join("", hex);
    }

    // ---- 読む ----

    // `"mappings"` の object の `"<Lua の行>": {"file": "...", "line": N}` を読む。JSON として読めなければ null
    public static SourceMapData? Parse(string json)
    {
        var r = new JsonReader(json);
        var map = new SourceMapData();
        if (!r.Expect('{')) return null;
        if (r.Accept('}')) return map;
        while (true)
        {
            var key = r.ReadString();
            if (key == null || !r.Expect(':')) return null;
            if (key == "mappings")
            {
                if (!ReadMappings(r, map)) return null;
            }
            else if (!r.Skip()) return null;
            if (r.Accept(',')) continue;
            if (r.Expect('}')) return map;
            return null;
        }
    }

    private static bool ReadMappings(JsonReader r, SourceMapData map)
    {
        if (!r.Expect('{')) return false;
        if (r.Accept('}')) return true;
        while (true)
        {
            var key = r.ReadString();
            if (key == null || !r.Expect(':')) return false;
            int luaLine = ParseInt(key);
            if (!r.Expect('{')) return false;
            string? file = null;
            int line = 0;
            bool hasLine = false;
            if (!r.Accept('}'))
            {
                while (true)
                {
                    var field = r.ReadString();
                    if (field == null || !r.Expect(':')) return false;
                    if (field == "file")
                    {
                        file = r.ReadString();
                        if (file == null) return false;
                    }
                    else if (field == "line")
                    {
                        var number = r.ReadNumber();
                        if (number == null) return false;
                        line = ParseInt(number);
                        hasLine = true;
                    }
                    else if (!r.Skip()) return false;
                    if (r.Accept(',')) continue;
                    if (r.Expect('}')) break;
                    return false;
                }
            }
            if (luaLine >= 0 && file != null && hasLine) Put(map, luaLine, file, line);
            if (r.Accept(',')) continue;
            return r.Expect('}');
        }
    }

    // 10 進の非負の整数 (それ以外は -1)
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

    // ---- stack trace ----

    public static string AnnotateStackTrace(string trace, SourceMapData map)
    {
        var parts = new List<string>();
        foreach (var line in SplitLines(trace)) parts.Add(AnnotateLine(line, map) + "\n");
        return string.Join("", parts);
    }

    // 改行 (\n、\r\n、\r) で分ける。最後の改行の後の空の行は数えない
    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var u = new Utf8Bytes(text);
        int start = 0;
        int i = 0;
        while (i < u.Length)
        {
            int c = u[i];
            if (c == '\n' || c == '\r')
            {
                lines.Add(u.Slice(start, i - start).ToString());
                if (c == '\r' && i + 1 < u.Length && u[i + 1] == '\n') i++;
                i++;
                start = i;
                continue;
            }
            i++;
        }
        if (start < u.Length) lines.Add(u.Slice(start, u.Length - start).ToString());
        return lines;
    }

    // 行の中で最初の `<path>.lua:<行>:` (path は ':' を含まない。先頭の `C:` は許す) の Lua の行に近い C# の位置を足す
    private static string AnnotateLine(string line, SourceMapData map)
    {
        int luaLine = FirstLuaFrameLine(line);
        if (luaLine < 0) return line;
        int k = -1;
        for (int i = 0; i < map.LuaLines.Count; i++)
        {
            if (map.LuaLines[i] <= luaLine) k = i;
        }
        if (k < 0) return line;
        return line + "  --> " + map.Files[k] + ":" + map.Lines[k];
    }

    private static int FirstLuaFrameLine(string line)
    {
        var u = new Utf8Bytes(line);
        int segment = 0;
        for (int p = 0; p < u.Length; p++)
        {
            if (u[p] != ':') continue;
            // p の前が `.lua` で、その前に path の文字が 1 つ以上あり、p の後が数字と ':'
            int pathLength = p - segment;
            if (pathLength >= 5 && u[p - 4] == '.' && u[p - 3] == 'l' && u[p - 2] == 'u' && u[p - 1] == 'a')
            {
                int q = p + 1;
                int value = 0;
                while (q < u.Length && u[q] >= '0' && u[q] <= '9')
                {
                    value = value * 10 + (u[q] - '0');
                    q++;
                }
                if (q > p + 1 && q < u.Length && u[q] == ':') return value;
            }
            segment = p + 1;
        }
        return -1;
    }
}

// source map の JSON を読むだけの小さな reader (数、文字列、object、配列、true / false / null)
public sealed class JsonReader
{
    private Utf8Bytes _u;
    private int _i;

    public JsonReader(string text)
    {
        _u = new Utf8Bytes(text);
        _i = 0;
    }

    private void SkipSpace()
    {
        while (_i < _u.Length)
        {
            int c = _u[_i];
            if (c == ' ' || c == '\n' || c == '\r' || c == '\t') _i++;
            else break;
        }
    }

    public bool Accept(int c)
    {
        SkipSpace();
        if (_i < _u.Length && _u[_i] == c)
        {
            _i++;
            return true;
        }
        return false;
    }

    public bool Expect(int c)
    {
        return Accept(c);
    }

    public string? ReadNumber()
    {
        SkipSpace();
        int start = _i;
        while (_i < _u.Length)
        {
            int c = _u[_i];
            if (c >= '0' && c <= '9' || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') _i++;
            else break;
        }
        if (_i == start) return null;
        return _u.Slice(start, _i - start).ToString();
    }

    public string? ReadString()
    {
        SkipSpace();
        if (_i >= _u.Length || _u[_i] != '"') return null;
        _i++;
        var parts = new List<string>();
        int start = _i;
        while (_i < _u.Length)
        {
            int c = _u[_i];
            if (c == '"')
            {
                if (_i > start) parts.Add(_u.Slice(start, _i - start).ToString());
                _i++;
                return string.Join("", parts);
            }
            if (c != '\\')
            {
                _i++;
                continue;
            }
            if (_i > start) parts.Add(_u.Slice(start, _i - start).ToString());
            if (_i + 1 >= _u.Length) return null;
            int e = _u[_i + 1];
            _i = _i + 2;
            if (e == 'n') parts.Add("\n");
            else if (e == 'r') parts.Add("\r");
            else if (e == 't') parts.Add("\t");
            else if (e == 'b') parts.Add(char.ConvertFromUtf32(8));
            else if (e == 'f') parts.Add(char.ConvertFromUtf32(12));
            else if (e == 'u')
            {
                int unit = Hex4();
                if (unit < 0) return null;
                if (unit >= 0xD800 && unit < 0xDC00 && _i + 1 < _u.Length && _u[_i] == '\\' && _u[_i + 1] == 'u')
                {
                    _i = _i + 2;
                    int low = Hex4();
                    if (low < 0) return null;
                    unit = 0x10000 + ((unit - 0xD800) << 10) + (low - 0xDC00);
                }
                parts.Add(char.ConvertFromUtf32(unit));
            }
            else parts.Add(_u.Slice(_i - 1, 1).ToString());
            start = _i;
        }
        return null;
    }

    private int Hex4()
    {
        if (_i + 4 > _u.Length) return -1;
        int v = 0;
        for (int k = 0; k < 4; k++)
        {
            int c = _u[_i + k];
            int d;
            if (c >= '0' && c <= '9') d = c - '0';
            else if (c >= 'a' && c <= 'f') d = c - 'a' + 10;
            else if (c >= 'A' && c <= 'F') d = c - 'A' + 10;
            else return -1;
            v = v * 16 + d;
        }
        _i = _i + 4;
        return v;
    }

    // 値を 1 つ読み飛ばす
    public bool Skip()
    {
        SkipSpace();
        if (_i >= _u.Length) return false;
        int c = _u[_i];
        if (c == '"') return ReadString() != null;
        if (c == '{' || c == '[')
        {
            int close = c == '{' ? '}' : ']';
            _i++;
            if (Accept(close)) return true;
            while (true)
            {
                if (c == '{')
                {
                    if (ReadString() == null || !Expect(':')) return false;
                }
                if (!Skip()) return false;
                if (Accept(',')) continue;
                return Expect(close);
            }
        }
        int start = _i;
        while (_i < _u.Length)
        {
            int x = _u[_i];
            if (x == ',' || x == '}' || x == ']' || x == ' ' || x == '\n' || x == '\r' || x == '\t') break;
            _i++;
        }
        return _i > start;
    }
}
