using System.Collections.Generic;
using TinySystem.Text;

namespace TcsSelf;

public enum TokenKind
{
    End,
    Ident,
    IntLit,
    FloatLit,
    StringLit,
    CharLit,
    InterpString,
    Punct,
}

public sealed class Token
{
    public TokenKind Kind;
    public int Pos;
    public int End;
    // Ident: 名前 (verbatim の @ は除く)。Punct: 演算子の文字列。StringLit: 復号した値。
    // IntLit / FloatLit: '_' を除いた字面 (suffix を含む)。InterpString: 中身の開始と終了 (ContentStart / ContentEnd)
    public string Text;
    public bool IsKeyword;
    // CharLit の code unit
    public int Value;
    // 直前に空白か改行があるか (`>` `>` を `>>` にまとめるときに見る)
    public bool SpaceBefore;
    public int ContentStart;
    public int ContentEnd;
    public bool Verbatim;
    // raw な補間文字列 ($$ と 3 つ以上の " で囲む) の $ の数 (穴の { } の数)。0 なら raw でない
    public int RawDollars;
    // 複数行の raw な文字列の各行から除く字下げ
    public int RawIndent;

    public Token(TokenKind kind, int pos, int end, string text)
    {
        Kind = kind;
        Pos = pos;
        End = end;
        Text = text;
    }
}

public sealed class Lexer
{
    private readonly SourceFile _file;
    private readonly Utf8Bytes _b;
    private readonly Diagnostics _diags;
    private int _pos;
    private readonly int _end;
    private static HashSet<string>? _keywords;

    public Lexer(SourceFile file, Diagnostics diags, int start, int end)
    {
        _file = file;
        _b = file.Bytes;
        _diags = diags;
        _pos = start;
        _end = end;
    }

    public static bool IsKeyword(string s)
    {
        if (_keywords == null)
        {
            var k = new HashSet<string>();
            string all = "abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while";
            foreach (var w in all.Split(' ')) k.Add(w);
            _keywords = k;
        }
        return _keywords.Contains(s);
    }

    public int Position => _pos;

    // 補間文字列の中の escape を読む (parser が使う)
    public string EscapeAt(int pos)
    {
        _pos = pos;
        return Escape(pos);
    }

    private int Peek(int ahead)
    {
        int p = _pos + ahead;
        if (p >= _end) return -1;
        return _b[p];
    }

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            bool space = SkipTrivia();
            var t = Next();
            t.SpaceBefore = space;
            tokens.Add(t);
            if (t.Kind == TokenKind.End) break;
        }
        return tokens;
    }

    private bool SkipTrivia()
    {
        bool any = false;
        bool lineStart = _pos == 0;
        while (_pos < _end)
        {
            int c = _b[_pos];
            if (c == ' ' || c == '\t' || c == '\r')
            {
                _pos++;
                any = true;
            }
            else if (c == '\n')
            {
                _pos++;
                any = true;
                lineStart = true;
            }
            else if (c == 0xEF && Peek(1) == 0xBB && Peek(2) == 0xBF)
            {
                _pos += 3;
            }
            else if (c == '/' && Peek(1) == '/')
            {
                while (_pos < _end && _b[_pos] != '\n') _pos++;
                any = true;
            }
            else if (c == '/' && Peek(1) == '*')
            {
                int start = _pos;
                _pos += 2;
                while (_pos < _end && !(_b[_pos] == '*' && Peek(1) == '/')) _pos++;
                if (_pos >= _end) _diags.Error("CS1035", "End-of-file found, '*/' expected", _file, start);
                else _pos += 2;
                any = true;
            }
            else if (c == '#' && lineStart)
            {
                Directive();
                any = true;
            }
            else
            {
                break;
            }
        }
        return any;
    }

    // #region / #endregion / #nullable / #pragma は読み飛ばす。他の指令はサブセットに無い
    private void Directive()
    {
        int start = _pos;
        while (_pos < _end && _b[_pos] != '\n') _pos++;
        string line = _file.Slice(start, _pos).Trim();
        if (line.StartsWith("#region") || line.StartsWith("#endregion") || line.StartsWith("#nullable") || line.StartsWith("#pragma"))
            return;
        _diags.Error("TCS1001", "Unsupported syntax: preprocessor directive '" + line + "'", _file, start);
    }

    private static bool IsIdentStart(int c) => c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c == '_' || c >= 0x80;

    private static bool IsIdentPart(int c) => IsIdentStart(c) || c >= '0' && c <= '9';

    private static bool IsDigit(int c) => c >= '0' && c <= '9';

    private Token Next()
    {
        if (_pos >= _end) return new Token(TokenKind.End, _pos, _pos, "");
        int start = _pos;
        int c = _b[_pos];
        if (IsIdentStart(c)) return Ident(start, false);
        if (c == '@' && Peek(1) >= 0 && IsIdentStart(Peek(1)))
        {
            _pos++;
            return Ident(start, true);
        }
        if (IsDigit(c) || c == '.' && Peek(1) >= 0 && IsDigit(Peek(1))) return Number(start);
        if (c == '"' && Peek(1) == '"' && Peek(2) == '"') return RawString(start);
        if (c == '"') return StringLiteral(start, false);
        if (c == '@' && Peek(1) == '"')
        {
            _pos++;
            return StringLiteral(start, true);
        }
        if (c == '$')
        {
            int dollars = 0;
            while (Peek(dollars) == '$') dollars++;
            if (Peek(dollars) == '"' && Peek(dollars + 1) == '"' && Peek(dollars + 2) == '"') return RawInterp(start, dollars);
        }
        if (c == '$' && Peek(1) == '"') return Interp(start, 1, false);
        if (c == '$' && Peek(1) == '@' && Peek(2) == '"') return Interp(start, 2, true);
        if (c == '@' && Peek(1) == '$' && Peek(2) == '"') return Interp(start, 2, true);
        if (c == '\'') return CharLiteral(start);
        return Punct(start);
    }

    private Token Ident(int start, bool verbatim)
    {
        int s = _pos;
        while (_pos < _end && IsIdentPart(_b[_pos])) _pos++;
        string text = _file.Slice(s, _pos);
        var t = new Token(TokenKind.Ident, start, _pos, text);
        t.IsKeyword = !verbatim && IsKeyword(text);
        _file.Identifiers.Add(text);
        for (int i = s; i < _pos; i++)
        {
            if (_b[i] >= 0x80)
            {
                _diags.Error("TCS1001", "Unsupported syntax: non-ASCII identifier '" + text + "'", _file, start);
                break;
            }
        }
        return t;
    }

    private Token Number(int start)
    {
        bool isFloat = false;
        int c = _b[_pos];
        if (c == '0' && (Peek(1) == 'x' || Peek(1) == 'X' || Peek(1) == 'b' || Peek(1) == 'B'))
        {
            _pos += 2;
            while (_pos < _end && (IsIdentPart(_b[_pos]))) _pos++;
            return new Token(TokenKind.IntLit, start, _pos, StripUnderscore(_file.Slice(start, _pos)));
        }
        while (_pos < _end && (IsDigit(_b[_pos]) || _b[_pos] == '_')) _pos++;
        if (_pos < _end && _b[_pos] == '.' && Peek(1) >= 0 && IsDigit(Peek(1)))
        {
            isFloat = true;
            _pos++;
            while (_pos < _end && (IsDigit(_b[_pos]) || _b[_pos] == '_')) _pos++;
        }
        if (_pos < _end && (_b[_pos] == 'e' || _b[_pos] == 'E'))
        {
            int save = _pos;
            _pos++;
            if (_pos < _end && (_b[_pos] == '+' || _b[_pos] == '-')) _pos++;
            if (_pos < _end && IsDigit(_b[_pos]))
            {
                isFloat = true;
                while (_pos < _end && IsDigit(_b[_pos])) _pos++;
            }
            else
            {
                _pos = save;
            }
        }
        while (_pos < _end && IsIdentPart(_b[_pos]))
        {
            int s = _b[_pos];
            if (s == 'f' || s == 'F' || s == 'd' || s == 'D' || s == 'm' || s == 'M') isFloat = true;
            _pos++;
        }
        var kind = isFloat ? TokenKind.FloatLit : TokenKind.IntLit;
        return new Token(kind, start, _pos, StripUnderscore(_file.Slice(start, _pos)));
    }

    private static string StripUnderscore(string s)
    {
        if (!s.Contains("_")) return s;
        return s.Replace("_", "");
    }

    private Token StringLiteral(int start, bool verbatim)
    {
        _pos++;
        var parts = new List<string>();
        int run = _pos;
        while (true)
        {
            if (_pos >= _end)
            {
                _diags.Error("CS1010", "Newline in constant", _file, start);
                break;
            }
            int c = _b[_pos];
            if (c == '"')
            {
                if (verbatim && Peek(1) == '"')
                {
                    parts.Add(_file.Slice(run, _pos + 1));
                    _pos += 2;
                    run = _pos;
                    continue;
                }
                parts.Add(_file.Slice(run, _pos));
                _pos++;
                break;
            }
            if (!verbatim && c == '\n')
            {
                _diags.Error("CS1010", "Newline in constant", _file, start);
                break;
            }
            if (!verbatim && c == '\\')
            {
                parts.Add(_file.Slice(run, _pos));
                parts.Add(Escape(start));
                run = _pos;
                continue;
            }
            _pos++;
        }
        var t = new Token(TokenKind.StringLit, start, _pos, string.Join("", parts));
        t.Verbatim = verbatim;
        return t;
    }

    // raw な補間文字列。穴は $ と同じ数の { } で囲む
    private Token RawInterp(int start, int dollars)
    {
        _pos += dollars;
        int quotes = 0;
        while (_pos < _end && _b[_pos] == '"')
        {
            quotes++;
            _pos++;
        }
        int contentStart = _pos;
        int contentEnd = -1;
        while (_pos < _end)
        {
            if (_b[_pos] == '"')
            {
                int run = 0;
                while (_pos + run < _end && _b[_pos + run] == '"') run++;
                if (run >= quotes)
                {
                    contentEnd = _pos;
                    _pos += run;
                    break;
                }
                _pos += run;
                continue;
            }
            _pos++;
        }
        if (contentEnd < 0)
        {
            _diags.Error("CS8997", "Unterminated raw string literal", _file, start);
            contentEnd = _end;
        }
        var t = new Token(TokenKind.InterpString, start, _pos, "");
        t.RawDollars = dollars;
        t.Verbatim = true;
        bool multiLine = false;
        for (int i = contentStart; i < contentEnd; i++)
        {
            if (_b[i] == '\n') multiLine = true;
        }
        if (!multiLine)
        {
            t.ContentStart = contentStart;
            t.ContentEnd = contentEnd;
            return t;
        }
        int first = contentStart;
        while (first < contentEnd && _b[first] != '\n') first++;
        first++;
        int lastNl = contentEnd - 1;
        while (lastNl > first && _b[lastNl] != '\n') lastNl--;
        t.ContentStart = first + (contentEnd - lastNl - 1);
        t.ContentEnd = lastNl;
        t.RawIndent = contentEnd - lastNl - 1;
        if (t.ContentEnd > 0 && _b[t.ContentEnd - 1] == '\r') t.ContentEnd--;
        return t;
    }

    // raw string literal ("""...""")。複数行のときは閉じの行の字下げを各行から除き、最初と最後の改行を除く
    private Token RawString(int start)
    {
        int quotes = 0;
        while (_pos < _end && _b[_pos] == '"')
        {
            quotes++;
            _pos++;
        }
        int contentStart = _pos;
        int contentEnd = -1;
        while (_pos < _end)
        {
            if (_b[_pos] == '"')
            {
                int run = 0;
                while (_pos + run < _end && _b[_pos + run] == '"') run++;
                if (run >= quotes)
                {
                    contentEnd = _pos;
                    _pos += run;
                    break;
                }
                _pos += run;
                continue;
            }
            _pos++;
        }
        if (contentEnd < 0)
        {
            _diags.Error("CS8997", "Unterminated raw string literal", _file, start);
            contentEnd = _end;
        }
        bool multiLine = false;
        for (int i = contentStart; i < contentEnd; i++)
        {
            if (_b[i] == '\n') multiLine = true;
        }
        if (!multiLine) return new Token(TokenKind.StringLit, start, _pos, _file.Slice(contentStart, contentEnd));
        // 開きの後の改行までを除く
        int first = contentStart;
        while (first < contentEnd && _b[first] != '\n') first++;
        first++;
        // 閉じの行の字下げ
        int lastNl = contentEnd - 1;
        while (lastNl > first && _b[lastNl] != '\n') lastNl--;
        int indent = contentEnd - lastNl - 1;
        var lines = new List<string>();
        int ls = first;
        while (ls <= lastNl)
        {
            int le = ls;
            while (le < lastNl && _b[le] != '\n') le++;
            int from = ls + indent;
            if (from > le) from = le;
            int lineEnd = le;
            if (lineEnd > from && _b[lineEnd - 1] == '\r') lineEnd--;
            lines.Add(_file.Slice(from, lineEnd));
            ls = le + 1;
        }
        return new Token(TokenKind.StringLit, start, _pos, string.Join("\n", lines));
    }

    // _pos は '\' を指す。読んだ escape の値の文字列を返し、_pos を進める
    private string Escape(int litStart)
    {
        int c = Peek(1);
        _pos += 2;
        switch (c)
        {
            case 'n': return "\n";
            case 't': return "\t";
            case 'r': return "\r";
            case '0': return char.ConvertFromUtf32(0);
            case '\\': return "\\";
            case '"': return "\"";
            case '\'': return "'";
            case 'a': return char.ConvertFromUtf32(7);
            case 'b': return char.ConvertFromUtf32(8);
            case 'f': return char.ConvertFromUtf32(12);
            case 'v': return char.ConvertFromUtf32(11);
            case 'e': return char.ConvertFromUtf32(27);
            case 'u':
                return char.ConvertFromUtf32(HexDigits(4, litStart));
            case 'U':
                return char.ConvertFromUtf32(HexDigits(8, litStart));
            case 'x':
                {
                    int v = 0;
                    int n = 0;
                    while (n < 4 && _pos < _end && HexValue(_b[_pos]) >= 0)
                    {
                        v = v * 16 + HexValue(_b[_pos]);
                        _pos++;
                        n++;
                    }
                    return char.ConvertFromUtf32(v);
                }
        }
        _diags.Error("CS1009", "Unrecognized escape sequence", _file, _pos - 2);
        return "";
    }

    private int HexDigits(int count, int litStart)
    {
        int v = 0;
        for (int i = 0; i < count; i++)
        {
            int h = _pos < _end ? HexValue(_b[_pos]) : -1;
            if (h < 0)
            {
                _diags.Error("CS1009", "Unrecognized escape sequence", _file, litStart);
                return 0;
            }
            v = v * 16 + h;
            _pos++;
        }
        return v;
    }

    public static int HexValue(int c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }

    private Token CharLiteral(int start)
    {
        _pos++;
        int value = 0;
        if (_pos < _end && _b[_pos] == '\\')
        {
            string s = Escape(start);
            var u = new Utf8Bytes(s);
            if (u.Length == 1) value = u[0];
            else value = -1;
        }
        else if (_pos < _end)
        {
            value = _b[_pos];
            if (value >= 0x80) value = -1;
            _pos++;
            while (_pos < _end && _b[_pos] >= 0x80 && _b[_pos] < 0xC0) _pos++;
        }
        if (_pos < _end && _b[_pos] == '\'') _pos++;
        else _diags.Error("CS1012", "Too many characters in character literal", _file, start);
        if (value < 0) _diags.Error("TCS1001", "Unsupported syntax: non-ASCII char literal (write a string literal)", _file, start);
        var t = new Token(TokenKind.CharLit, start, _pos, "");
        t.Value = value < 0 ? 0 : value;
        return t;
    }

    // 補間文字列は中身の範囲だけを覚え、穴の式は parser が同じファイルの範囲を読み直す
    private Token Interp(int start, int prefix, bool verbatim)
    {
        _pos += prefix + 1;
        int contentStart = _pos;
        int depth = 0;
        while (true)
        {
            if (_pos >= _end)
            {
                _diags.Error("CS1010", "Newline in constant", _file, start);
                break;
            }
            int c = _b[_pos];
            if (depth == 0)
            {
                if (c == '"')
                {
                    if (verbatim && Peek(1) == '"')
                    {
                        _pos += 2;
                        continue;
                    }
                    break;
                }
                if (!verbatim && c == '\\')
                {
                    _pos += 2;
                    continue;
                }
                if (c == '{')
                {
                    if (Peek(1) == '{')
                    {
                        _pos += 2;
                        continue;
                    }
                    depth = 1;
                    _pos++;
                    continue;
                }
                if (!verbatim && c == '\n')
                {
                    _diags.Error("CS1010", "Newline in constant", _file, start);
                    break;
                }
                _pos++;
                continue;
            }
            // 穴の中
            if (c == '{') depth++;
            else if (c == '}') depth--;
            else if (c == '"' || c == '\'')
            {
                SkipQuoted(c);
                continue;
            }
            else if (c == '$' && Peek(1) == '"')
            {
                _pos++;
                SkipQuoted('"');
                continue;
            }
            _pos++;
        }
        int contentEnd = _pos;
        if (_pos < _end) _pos++;
        var t = new Token(TokenKind.InterpString, start, _pos, "");
        t.ContentStart = contentStart;
        t.ContentEnd = contentEnd;
        t.Verbatim = verbatim;
        return t;
    }

    private void SkipQuoted(int q)
    {
        _pos++;
        while (_pos < _end && _b[_pos] != q)
        {
            if (_b[_pos] == '\\') _pos++;
            _pos++;
        }
        _pos++;
    }

    private Token Punct(int start)
    {
        int c = _b[_pos];
        int c1 = Peek(1);
        int c2 = Peek(2);
        int len = 1;
        switch (c)
        {
            case '+':
                if (c1 == '+' || c1 == '=') len = 2;
                break;
            case '-':
                if (c1 == '-' || c1 == '=' || c1 == '>') len = 2;
                break;
            case '*':
            case '/':
            case '%':
            case '^':
            case '!':
                if (c1 == '=') len = 2;
                break;
            case '=':
                if (c1 == '=' || c1 == '>') len = 2;
                break;
            case '<':
                if (c1 == '<') len = c2 == '=' ? 3 : 2;
                else if (c1 == '=') len = 2;
                break;
            case '>':
                // `>>` は parser が隣接する `>` 2 つから作る (generic の閉じ括弧と区別するため)
                if (c1 == '=') len = 2;
                break;
            case '&':
                if (c1 == '&' || c1 == '=') len = 2;
                break;
            case '|':
                if (c1 == '|' || c1 == '=') len = 2;
                break;
            case '?':
                if (c1 == '?') len = c2 == '=' ? 3 : 2;
                else if (c1 == '.' && !(c2 >= '0' && c2 <= '9')) len = 2;
                break;
            case ':':
                if (c1 == ':') len = 2;
                break;
            case '.':
                if (c1 == '.') len = 2;
                break;
            case '(':
            case ')':
            case '{':
            case '}':
            case '[':
            case ']':
            case ';':
            case ',':
            case '~':
                break;
            default:
                _diags.Error("CS1056", "Unexpected character", _file, start);
                break;
        }
        _pos += len;
        return new Token(TokenKind.Punct, start, _pos, _file.Slice(start, _pos));
    }
}
