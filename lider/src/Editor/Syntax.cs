// C# highlighting. A port of the patterns of Lite XL's tokenizer (core/tokenizer.lua) and lider's language_csharp.lua.
// Patterns are tried from the top, and the kind of the first match is used (if none match, 1 character becomes normal).
//   //.*                         comment
//   /* to */                     comment (spans lines)
//   """ to """                   string (spans lines)
//   [$@]+" to " (\ escapes)      string
//   " to " (\ escapes)           string
//   ' to ' (\ escapes)           string
//   ^%s*#[%a_][%w_]*             keyword (only at line start)
//   0[xX][%x_]+[uUlL]*           number
//   0[bB][01_]+[uUlL]*           number
//   %d[%d_]*%.?[%d_]*[eE][%+%-]?%d+[fFdDmM]?  number
//   %d[%d_]*%.?[%d_]*[fFdDmMuUlL]*            number
//   %.%d[%d_]*[fFdDmM]?          number
//   [%+%-=/%*%^%%<>!~|&%?:]      operator
//   [%a_][%w_]*%f[(]             function
//   @?[%a_][%w_]*                symbol
// If a name is in the keyword table, uses that kind.
// Joins adjacent tokens of the same kind, or a token after a whitespace-only token, into one (Lite XL's push_token).
// Being in the middle of a multi-line pattern is passed to the next line as the end-of-line state (the pattern number, 0 = none).
// The text has no newlines, so the newline at the end of a line is not made a token (Lite XL removes it when drawing).
using System;
using System.Collections.Generic;
using TinySystem.Text;

namespace LiderEditor;

public static class TokenType
{
    public const int Normal = 0;
    public const int Symbol = 1;
    public const int Comment = 2;
    public const int Keyword = 3;
    public const int Keyword2 = 4;
    public const int Number = 5;
    public const int Literal = 6;
    public const int String = 7;
    public const int Operator = 8;
    public const int Function = 9;
}

// Tokens of one line
public class LineTokens
{
    public string Text = "";
    public int InitState;
    public int State;
    public List<int> Types = new List<int>();
    public List<string> Texts = new List<string>();
}

// Rebuilds the tokens of each doc line as far as needed and remembers them.
// Lines from the edited one onward are marked for rebuilding, and when requested they are rebuilt in order from the previous line's state
public class Highlighter
{
    readonly Doc doc;
    readonly List<LineTokens?> lines = new List<LineTokens?>();
    public bool CSharp;
    // For lines from here on, the previous line's state may have changed
    int firstInvalid = 1;

    public Highlighter(Doc doc)
    {
        this.doc = doc;
    }

    public void SetCSharp(bool on)
    {
        if (on == CSharp)
            return;
        CSharp = on;
        Reset();
    }

    public void Reset()
    {
        lines.Clear();
        firstInvalid = 1;
    }

    public void InsertNotify(int line, int n)
    {
        firstInvalid = Math.Min(firstInvalid, line);
        for (var i = 0; i < n && line - 1 <= lines.Count; i++)
            lines.Insert(Math.Min(line - 1, lines.Count), null);
    }

    public void RemoveNotify(int line, int n)
    {
        firstInvalid = Math.Min(firstInvalid, line);
        for (var i = 0; i < n && line < lines.Count; i++)
            lines.RemoveAt(line);
    }

    // Tokens of line idx (from 1)
    public LineTokens GetLine(int idx)
    {
        while (lines.Count < doc.Lines.Count)
            lines.Add(null);
        while (lines.Count > doc.Lines.Count)
            lines.RemoveAt(lines.Count - 1);
        for (var i = Math.Max(1, firstInvalid); i <= idx; i++)
            Ensure(i);
        firstInvalid = Math.Max(firstInvalid, idx + 1);
        return lines[idx - 1]!;
    }

    void Ensure(int i)
    {
        var state = i > 1 ? lines[i - 2]!.State : 0;
        var text = doc.Lines[i - 1];
        var line = lines[i - 1];
        if (line != null && line.InitState == state && line.Text == text)
            return;
        var t = new LineTokens { Text = text, InitState = state };
        t.State = CSharp ? CSharpSyntax.Tokenize(text, state, t.Types, t.Texts) : Plain(text, t.Types, t.Texts);
        lines[i - 1] = t;
    }

    static int Plain(string text, List<int> types, List<string> texts)
    {
        if (text != "")
        {
            types.Add(TokenType.Normal);
            texts.Add(text);
        }
        return 0;
    }
}

public static class CSharpSyntax
{
    // Number of the multi-line pattern (state)
    const int BlockComment = 2;
    const int RawString = 3;
    const int MarkedString = 4;
    const int PlainString = 5;
    const int CharLiteral = 6;

    static Dictionary<string, int>? symbols;

    static Dictionary<string, int> Symbols()
    {
        if (symbols != null)
            return symbols;
        var s = new Dictionary<string, int>();
        var keywords = "abstract as async await base break case catch checked class const continue default delegate do else enum event explicit extern finally fixed for foreach get goto if implicit in init interface internal is lock namespace new operator out override params partial private protected public readonly record ref required return sealed set sizeof stackalloc static struct switch this throw try typeof unchecked unsafe using value virtual volatile when where while yield";
        foreach (var w in keywords.Split(' '))
            s[w] = TokenType.Keyword;
        var types = "bool byte char decimal double dynamic float int long nint nuint object sbyte short string uint ulong ushort var void";
        foreach (var w in types.Split(' '))
            s[w] = TokenType.Keyword2;
        s["true"] = TokenType.Literal;
        s["false"] = TokenType.Literal;
        s["null"] = TokenType.Literal;
        symbols = s;
        return s;
    }

    static bool IsWhitespace(string text)
    {
        var u = new Utf8Bytes(text);
        var i = 0;
        while (i < u.Length)
        {
            int b = u[i];
            if (Str.IsSpace(b))
            {
                i++;
                continue;
            }
            // Full-width space (U+3000)
            if (b == 0xE3 && i + 2 < u.Length && u[i + 1] == 0x80 && u[i + 2] == 0x80)
            {
                i += 3;
                continue;
            }
            return false;
        }
        return true;
    }

    static void Push(List<int> types, List<string> texts, int type, string text)
    {
        var n = types.Count;
        if (n > 0 && (types[n - 1] == type || IsWhitespace(texts[n - 1])))
        {
            types[n - 1] = type;
            texts[n - 1] = texts[n - 1] + text;
            return;
        }
        types.Add(type);
        texts.Add(text);
    }

    // The kind from the name table if present, otherwise type
    static void PushNamed(List<int> types, List<string> texts, int type, string text)
    {
        if (Symbols().TryGetValue(text, out var t))
            type = t;
        Push(types, texts, type, text);
    }

    static bool IsDigit(int b)
    {
        return b >= '0' && b <= '9';
    }

    static bool IsHex(int b)
    {
        return IsDigit(b) || (b >= 'a' && b <= 'f') || (b >= 'A' && b <= 'F');
    }

    // Lua's %a (outside ASCII, treated as a letter except for symbols and full-width punctuation)
    static bool IsAlphaAt(Utf8Bytes u, int at)
    {
        int b = u[at];
        if (b < 0x80)
            return (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z');
        var cp = Str.Decode(u, at);
        if (cp >= 0x2000 && cp <= 0x2BFF)
            return false;
        if (cp >= 0x3000 && cp <= 0x303F)
            return false;
        if (cp >= 0xFF00 && cp <= 0xFF20)
            return false;
        return cp >= 0xA0;
    }

    static bool IsIdentStart(Utf8Bytes u, int at)
    {
        return u[at] == '_' || IsAlphaAt(u, at);
    }

    static bool IsIdentPart(Utf8Bytes u, int at)
    {
        return u[at] == '_' || IsDigit(u[at]) || IsAlphaAt(u, at);
    }

    static int CharLen(Utf8Bytes u, int at)
    {
        var n = Math.Max(1, Str.RuneBytes(u[at]));
        return Math.Min(n, u.Length - at);
    }

    // End of the name continuing from at
    static int IdentEnd(Utf8Bytes u, int at)
    {
        var i = at;
        while (i < u.Length && IsIdentPart(u, i))
            i += CharLen(u, i);
        return i;
    }

    // Number of \ immediately before at
    static int Backslashes(Utf8Bytes u, int at)
    {
        var count = 0;
        for (var i = at - 1; i >= 0 && u[i] == '\\'; i--)
            count++;
        return count;
    }

    // Closing mark of the state's pattern, and whether \ escapes
    static string Closer(int state)
    {
        if (state == BlockComment)
            return "*/";
        if (state == RawString)
            return "\"\"\"";
        if (state == CharLiteral)
            return "'";
        return "\"";
    }

    static bool Escapes(int state)
    {
        return state == MarkedString || state == PlainString || state == CharLiteral;
    }

    static int TypeOf(int state)
    {
        return state == BlockComment ? TokenType.Comment : TokenType.String;
    }

    // Where the state's closing mark starts from from onward (skips escaped ones; -1 if none)
    static int FindClose(string text, Utf8Bytes u, int state, int from)
    {
        var close = Closer(state);
        var at = Str.Find(text, close, from);
        while (at >= 0 && Escapes(state) && Backslashes(u, at) % 2 == 1)
            at = Str.Find(text, close, at + 1);
        return at;
    }

    // End of the number starting at at (-1 if not a number)
    static int NumberEnd(Utf8Bytes u, int at)
    {
        var n = u.Length;
        int b = u[at];
        if (b == '0' && at + 1 < n && (u[at + 1] == 'x' || u[at + 1] == 'X'))
        {
            var i = at + 2;
            while (i < n && (IsHex(u[i]) || u[i] == '_'))
                i++;
            if (i > at + 2)
            {
                while (i < n && "uUlL".Contains(((char)u[i]).ToString()))
                    i++;
                return i;
            }
        }
        if (b == '0' && at + 1 < n && (u[at + 1] == 'b' || u[at + 1] == 'B'))
        {
            var i = at + 2;
            while (i < n && (u[i] == '0' || u[i] == '1' || u[i] == '_'))
                i++;
            if (i > at + 2)
            {
                while (i < n && "uUlL".Contains(((char)u[i]).ToString()))
                    i++;
                return i;
            }
        }
        if (IsDigit(b))
        {
            var i = at + 1;
            while (i < n && (IsDigit(u[i]) || u[i] == '_'))
                i++;
            if (i < n && u[i] == '.')
                i++;
            while (i < n && (IsDigit(u[i]) || u[i] == '_'))
                i++;
            var mantissa = i;
            // Form with an exponent
            if (i < n && (u[i] == 'e' || u[i] == 'E'))
            {
                var j = i + 1;
                if (j < n && (u[j] == '+' || u[j] == '-'))
                    j++;
                var digits = j;
                while (j < n && IsDigit(u[j]))
                    j++;
                if (j > digits)
                {
                    if (j < n && "fFdDmM".Contains(((char)u[j]).ToString()))
                        j++;
                    return j;
                }
            }
            i = mantissa;
            while (i < n && "fFdDmMuUlL".Contains(((char)u[i]).ToString()))
                i++;
            return i;
        }
        if (b == '.' && at + 1 < n && IsDigit(u[at + 1]))
        {
            var i = at + 2;
            while (i < n && (IsDigit(u[i]) || u[i] == '_'))
                i++;
            if (i < n && "fFdDmM".Contains(((char)u[i]).ToString()))
                i++;
            return i;
        }
        return -1;
    }

    static bool IsOperator(int b)
    {
        return b < 0x80 && "+-=/*^%<>!~|&?:".Contains(((char)b).ToString());
    }

    // Adds the tokens of text to types and texts and returns the end-of-line state
    public static int Tokenize(string text, int state, List<int> types, List<string> texts)
    {
        var u = new Utf8Bytes(text);
        var n = u.Length;
        var i = 0;
        while (i < n)
        {
            if (state != 0)
            {
                var close = Str.Len(Closer(state));
                var s = FindClose(text, u, state, i);
                if (s < 0)
                {
                    Push(types, texts, TypeOf(state), Str.From(text, i));
                    break;
                }
                if (s > i)
                    Push(types, texts, TypeOf(state), Str.Slice(text, i, s - i));
                PushNamed(types, texts, TypeOf(state), Str.Slice(text, s, close));
                state = 0;
                i = s + close;
                continue;
            }
            int b = u[i];
            // Comment
            if (b == '/' && i + 1 < n && u[i + 1] == '/')
            {
                PushNamed(types, texts, TokenType.Comment, Str.From(text, i));
                break;
            }
            if (b == '/' && i + 1 < n && u[i + 1] == '*')
            {
                PushNamed(types, texts, TokenType.Comment, "/*");
                state = BlockComment;
                i += 2;
                continue;
            }
            // String
            if (Str.At(text, i, "\"\"\""))
            {
                PushNamed(types, texts, TokenType.String, "\"\"\"");
                state = RawString;
                i += 3;
                continue;
            }
            if (b == '$' || b == '@')
            {
                var j = i;
                while (j < n && (u[j] == '$' || u[j] == '@'))
                    j++;
                if (j < n && u[j] == '"' && Backslashes(u, i) % 2 == 0)
                {
                    PushNamed(types, texts, TokenType.String, Str.Slice(text, i, j + 1 - i));
                    state = MarkedString;
                    i = j + 1;
                    continue;
                }
            }
            if ((b == '"' || b == '\'') && Backslashes(u, i) % 2 == 0)
            {
                PushNamed(types, texts, TokenType.String, b == '"' ? "\"" : "'");
                state = b == '"' ? PlainString : CharLiteral;
                i++;
                continue;
            }
            // #if and the like at the start of a line
            if (i == 0)
            {
                var j = 0;
                while (j < n && Str.IsSpace(u[j]))
                    j++;
                if (j + 1 < n && u[j] == '#' && IsIdentStart(u, j + 1))
                {
                    var end = IdentEnd(u, j + 1);
                    PushNamed(types, texts, TokenType.Keyword, Str.Slice(text, 0, end));
                    i = end;
                    continue;
                }
            }
            var num = NumberEnd(u, i);
            if (num > i)
            {
                PushNamed(types, texts, TokenType.Number, Str.Slice(text, i, num - i));
                i = num;
                continue;
            }
            if (IsOperator(b))
            {
                PushNamed(types, texts, TokenType.Operator, Str.Slice(text, i, 1));
                i++;
                continue;
            }
            if (IsIdentStart(u, i))
            {
                var end = IdentEnd(u, i);
                var type = end < n && u[end] == '(' ? TokenType.Function : TokenType.Symbol;
                PushNamed(types, texts, type, Str.Slice(text, i, end - i));
                i = end;
                continue;
            }
            if (b == '@' && i + 1 < n && IsIdentStart(u, i + 1))
            {
                var end = IdentEnd(u, i + 1);
                PushNamed(types, texts, TokenType.Symbol, Str.Slice(text, i, end - i));
                i = end;
                continue;
            }
            var len = CharLen(u, i);
            Push(types, texts, TokenType.Normal, Str.Slice(text, i, len));
            i += len;
        }
        return state;
    }
}
