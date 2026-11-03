using System.Collections.Generic;
using TinySystem.Text;

namespace LubGen;

public enum JKind
{
    Object,
    Array,
    String,
    Number,
    Bool,
    Null,
}

/// <summary>JSON の値 (System.Text.Json の WriteIndented と同じ形に書く)。</summary>
public sealed class JValue
{
    public JKind Kind;
    public string Text = "";
    public bool B;
    public List<string> Keys = new List<string>();
    public List<JValue> Items = new List<JValue>();

    public JValue(JKind kind)
    {
        Kind = kind;
    }

    public static JValue Obj() => new JValue(JKind.Object);

    public static JValue Arr() => new JValue(JKind.Array);

    public static JValue Str(string s)
    {
        var v = new JValue(JKind.String);
        v.Text = s;
        return v;
    }

    public static JValue Num(string raw)
    {
        var v = new JValue(JKind.Number);
        v.Text = raw;
        return v;
    }

    public static JValue Int(int i) => Num(i.ToString());

    public static JValue Bool(bool b)
    {
        var v = new JValue(JKind.Bool);
        v.B = b;
        return v;
    }

    public JValue Add(JValue item)
    {
        Items.Add(item);
        return this;
    }

    public JValue Set(string key, JValue value)
    {
        Keys.Add(key);
        Items.Add(value);
        return this;
    }
}

public static class Json
{
    /// <summary>indent は 2 つの空白、改行は \n。relaxed は JavaScriptEncoder.UnsafeRelaxedJsonEscaping (ASCII でない文字をそのまま書く)、
    /// そうでなければ既定の encoder (ASCII でない文字と HTML の文字を \uXXXX にする)。</summary>
    public static string Write(JValue v, bool relaxed)
    {
        var parts = new List<string>();
        Emit(v, 0, relaxed, parts);
        return string.Join("", parts);
    }

    private static void Emit(JValue v, int depth, bool relaxed, List<string> o)
    {
        switch (v.Kind)
        {
            case JKind.Object:
            {
                if (v.Items.Count == 0)
                {
                    o.Add("{}");
                    return;
                }
                o.Add("{");
                for (int i = 0; i < v.Items.Count; i++)
                {
                    o.Add(i == 0 ? "\n" : ",\n");
                    o.Add(Text.Repeat("  ", depth + 1));
                    o.Add(Quote(v.Keys[i], relaxed));
                    o.Add(": ");
                    Emit(v.Items[i], depth + 1, relaxed, o);
                }
                o.Add("\n");
                o.Add(Text.Repeat("  ", depth));
                o.Add("}");
                return;
            }
            case JKind.Array:
            {
                if (v.Items.Count == 0)
                {
                    o.Add("[]");
                    return;
                }
                o.Add("[");
                for (int i = 0; i < v.Items.Count; i++)
                {
                    o.Add(i == 0 ? "\n" : ",\n");
                    o.Add(Text.Repeat("  ", depth + 1));
                    Emit(v.Items[i], depth + 1, relaxed, o);
                }
                o.Add("\n");
                o.Add(Text.Repeat("  ", depth));
                o.Add("]");
                return;
            }
            case JKind.String:
                o.Add(Quote(v.Text, relaxed));
                return;
            case JKind.Number:
                o.Add(v.Text);
                return;
            case JKind.Bool:
                o.Add(v.B ? "true" : "false");
                return;
            default:
                o.Add("null");
                return;
        }
    }

    private static string HexDigits = "0123456789ABCDEF";

    private static string U4(int c)
    {
        var parts = new List<string>();
        parts.Add("\\u");
        for (int shift = 12; shift >= 0; shift -= 4) parts.Add(Text.Sub(HexDigits, (c >> shift) & 15, 1));
        return string.Join("", parts);
    }

    public static string Quote(string s, bool relaxed)
    {
        var parts = new List<string>();
        parts.Add("\"");
        foreach (var r in s.EnumerateRunes())
        {
            int c = r.Value;
            if (c == '"') parts.Add(relaxed ? "\\\"" : "\\u0022");
            else if (c == '\\') parts.Add("\\\\");
            else if (c == '\n') parts.Add("\\n");
            else if (c == '\r') parts.Add("\\r");
            else if (c == '\t') parts.Add("\\t");
            else if (c == 8) parts.Add("\\b");
            else if (c == 12) parts.Add("\\f");
            else if (c < 0x20 || c == 0x7F) parts.Add(U4(c));
            else if (!relaxed && (c == '&' || c == '\'' || c == '+' || c == '<' || c == '>' || c == '`')) parts.Add(U4(c));
            else if (!relaxed && c > 0x7F)
            {
                if (c >= 0x10000)
                {
                    int v = c - 0x10000;
                    parts.Add(U4(0xD800 + (v >> 10)));
                    parts.Add(U4(0xDC00 + (v & 0x3FF)));
                }
                else parts.Add(U4(c));
            }
            else parts.Add(r.ToString());
        }
        parts.Add("\"");
        return string.Join("", parts);
    }
}
