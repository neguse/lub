using System.Collections.Generic;
using TinySystem.Text;

namespace Tcs;

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
    // Ident: the name (without the verbatim @). Punct: the operator string. StringLit: the decoded value.
    // IntLit / FloatLit: the text without '_' (including the suffix). InterpString: the start and end of the contents (ContentStart / ContentEnd)
    public string Text;
    public bool IsKeyword;
    // The code unit of a CharLit
    public int Value;
    // Whether whitespace or a newline precedes it (checked when merging `>` `>` into `>>`)
    public bool SpaceBefore;
    public int ContentStart;
    public int ContentEnd;
    public bool Verbatim;
    // The number of $ in a raw interpolated string (enclosed by $$ and three or more "), which is the number of { } around a hole. 0 if not raw
    public int RawDollars;
    // Indentation removed from each line of a multi-line raw string
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

    // Reads escapes inside an interpolated string (used by the parser)
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

    private int NewLine(int p) => SourceFile.NewLineLength(_b, p, _end);

    // Inside the expression of an interpolation hole, the position after the comment, string, or character starting at p (p when it is none of them).
    // Brackets, quotes, and : inside them are not counted as hole delimiters. A // comment ends before the newline
    public static int SkipInHole(Utf8Bytes b, int p, int end)
    {
        int c = b[p];
        if (c == '/' && p + 1 < end && b[p + 1] == '/')
        {
            p += 2;
            while (p < end && SourceFile.NewLineLength(b, p, end) == 0) p++;
            return p;
        }
        if (c == '/' && p + 1 < end && b[p + 1] == '*')
        {
            p += 2;
            while (p + 1 < end && !(b[p] == '*' && b[p + 1] == '/')) p++;
            return p + 1 < end ? p + 2 : end;
        }
        if (c == '"' || c == '\'')
        {
            p++;
            while (p < end && b[p] != c)
            {
                if (b[p] == '\\') p++;
                p++;
            }
            return p < end ? p + 1 : end;
        }
        return p;
    }

    // The position after removing the closing line's indentation (indent bytes) from the line start p of a multi-line raw string.
    // A whitespace-only line shorter than the indentation stops before the newline
    public static int SkipRawIndent(Utf8Bytes b, int p, int end, int indent)
    {
        int stop = p + indent;
        while (p < stop && p < end && (b[p] == ' ' || b[p] == '\t')) p++;
        return p;
    }

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            bool space = SkipTrivia();
            var t = Next();
            _sawToken = true;
            t.SpaceBefore = space;
            tokens.Add(t);
            if (t.Kind == TokenKind.End) break;
        }
        CheckDirectivesClosed();
        return tokens;
    }

    private bool SkipTrivia()
    {
        bool any = false;
        bool lineStart = _pos == 0;
        while (_pos < _end)
        {
            int c = _b[_pos];
            int nl = NewLine(_pos);
            if (c == ' ' || c == '\t')
            {
                _pos++;
                any = true;
            }
            else if (nl > 0)
            {
                _pos += nl;
                any = true;
                lineStart = true;
            }
            else if (c == 0xEF && Peek(1) == 0xBB && Peek(2) == 0xBF)
            {
                _pos += 3;
            }
            else if (c == '/' && Peek(1) == '/')
            {
                while (_pos < _end && NewLine(_pos) == 0) _pos++;
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

    // State of conditional compilation (#define / #undef / #if / #elif / #else / #endif).
    // For each level, holds whether that level is active (_condActive) and whether a branch has already been taken (_condTaken)
    private readonly HashSet<string> _defines = new HashSet<string>();
    private readonly List<bool> _condActive = new List<bool>();
    private readonly List<bool> _condTaken = new List<bool>();
    private readonly List<int> _condPos = new List<int>();

    // Whether the first token of the file has been read (#define / #undef can only be written before it)
    private bool _sawToken;

    private bool Active() => _condActive.Count == 0 || _condActive[_condActive.Count - 1];

    private bool ParentActive() => _condActive.Count < 2 || _condActive[_condActive.Count - 2];

    // Processes a one-line directive. On entering an inactive branch, skips whole lines until the next directive line
    private void Directive()
    {
        DirectiveLine();
        while (_pos < _end && !Active())
        {
            int p = _pos;
            while (p < _end)
            {
                int n = _b[p] == ' ' || _b[p] == '\t' ? 1 : NewLine(p);
                if (n == 0) break;
                p += n;
            }
            if (p < _end && _b[p] == '#')
            {
                _pos = p;
                DirectiveLine();
            }
            else
            {
                _pos = p;
                while (_pos < _end && NewLine(_pos) == 0) _pos++;
            }
        }
    }

    // #region / #endregion / #nullable / #pragma are skipped. #error / #warning / #line etc. are not in the subset
    private void DirectiveLine()
    {
        int start = _pos;
        while (_pos < _end && NewLine(_pos) == 0) _pos++;
        string line = _file.Slice(start, _pos).Trim();
        // Strip a trailing // comment
        var lb = new Utf8Bytes(line);
        int cut = lb.Length;
        for (int i = 0; i + 1 < lb.Length; i++)
        {
            if (lb[i] == '/' && lb[i + 1] == '/')
            {
                cut = i;
                break;
            }
        }
        int k = 1;
        while (k < cut && (lb[k] == ' ' || lb[k] == '\t')) k++;
        int nameStart = k;
        while (k < cut && IsIdentPart(lb[k])) k++;
        string name = lb.Slice(nameStart, k - nameStart).ToString();
        string rest = lb.Slice(k, cut - k).ToString().Trim();
        // Inside an inactive branch, only conditional directives are counted; other directives are not read
        if (!Active() && name != "if" && name != "elif" && name != "else" && name != "endif") return;
        if ((name == "define" || name == "undef") && _sawToken)
        {
            _diags.Error("CS1032", "Cannot define/undefine preprocessor symbols after first token in file", _file, start);
            return;
        }
        switch (name)
        {
            case "nullable":
                // Annotation context. It affects only the type notation for the C backend (a reference-type var local gets `?`). Ranges within the file are not considered
                if (rest == "enable") _file.NullableEnabled = true;
                return;
            case "region":
            case "endregion":
            case "pragma":
                return;
            case "define":
                if (Active()) _defines.Add(rest);
                return;
            case "undef":
                if (Active()) _defines.Remove(rest);
                return;
            case "if":
                {
                    bool v = Active() && EvalCondition(rest, start);
                    _condActive.Add(v);
                    _condTaken.Add(v);
                    _condPos.Add(start);
                    return;
                }
            case "elif":
            case "else":
                {
                    int top = _condActive.Count - 1;
                    if (top < 0)
                    {
                        _diags.Error("CS1028", "Unexpected preprocessor directive", _file, start);
                        return;
                    }
                    bool v = ParentActive() && !_condTaken[top] && (name == "else" || EvalCondition(rest, start));
                    _condActive[top] = v;
                    if (v) _condTaken[top] = true;
                    return;
                }
            case "endif":
                if (_condActive.Count == 0)
                {
                    _diags.Error("CS1028", "Unexpected preprocessor directive", _file, start);
                    return;
                }
                _condActive.RemoveAt(_condActive.Count - 1);
                _condTaken.RemoveAt(_condTaken.Count - 1);
                _condPos.RemoveAt(_condPos.Count - 1);
                return;
        }
        _diags.Error("TCS1001", "unsupported syntax: preprocessor directive '" + line + "'", _file, start);
    }

    // #if not closed at end of file
    public void CheckDirectivesClosed()
    {
        if (_condPos.Count > 0) _diags.Error("CS1027", "#endif directive expected", _file, _condPos[_condPos.Count - 1]);
    }

    // #if expressions: || && == != ! ( ) true false and symbols
    private Utf8Bytes _pp = new Utf8Bytes("");
    private int _ppPos;
    private bool _ppError;

    private bool EvalCondition(string text, int at)
    {
        _pp = new Utf8Bytes(text);
        _ppPos = 0;
        _ppError = false;
        bool v = PpOr();
        PpSpace();
        if (_ppError || _ppPos < _pp.Length)
        {
            _diags.Error("CS1517", "Invalid preprocessor expression", _file, at);
            return false;
        }
        return v;
    }

    private void PpSpace()
    {
        while (_ppPos < _pp.Length && (_pp[_ppPos] == ' ' || _pp[_ppPos] == '\t' || _pp[_ppPos] == '\r')) _ppPos++;
    }

    private bool PpAccept(string op)
    {
        PpSpace();
        var ob = new Utf8Bytes(op);
        if (_ppPos + ob.Length > _pp.Length) return false;
        for (int i = 0; i < ob.Length; i++)
        {
            if (_pp[_ppPos + i] != ob[i]) return false;
        }
        // Distinguish != from !
        if (op == "!" && _ppPos + 1 < _pp.Length && _pp[_ppPos + 1] == '=') return false;
        _ppPos += ob.Length;
        return true;
    }

    private bool PpOr()
    {
        bool v = PpAnd();
        while (PpAccept("||"))
        {
            bool r = PpAnd();
            v = v || r;
        }
        return v;
    }

    private bool PpAnd()
    {
        bool v = PpEq();
        while (PpAccept("&&"))
        {
            bool r = PpEq();
            v = v && r;
        }
        return v;
    }

    private bool PpEq()
    {
        bool v = PpUnary();
        while (true)
        {
            if (PpAccept("=="))
            {
                bool r = PpUnary();
                v = v == r;
            }
            else if (PpAccept("!="))
            {
                bool r = PpUnary();
                v = v != r;
            }
            else return v;
        }
    }

    private bool PpUnary()
    {
        if (PpAccept("!")) return !PpUnary();
        if (PpAccept("("))
        {
            bool v = PpOr();
            if (!PpAccept(")")) _ppError = true;
            return v;
        }
        PpSpace();
        int s = _ppPos;
        while (_ppPos < _pp.Length && IsIdentPart(_pp[_ppPos])) _ppPos++;
        if (_ppPos == s)
        {
            _ppError = true;
            return false;
        }
        string id = _pp.Slice(s, _ppPos - s).ToString();
        if (id == "true") return true;
        if (id == "false") return false;
        return _defines.Contains(id);
    }

    private static bool IsIdentStart(int c) => c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c == '_' || c >= 0x80;

    private static bool IsIdentPart(int c) => IsIdentStart(c) || c >= '0' && c <= '9';

    // Whether the byte at p starts / continues an identifier. Among bytes 0x80 and above, the newlines U+0085 / U+2028 / U+2029 are not identifiers
    private bool IdentStartAt(int p) => p < _end && IsIdentStart(_b[p]) && (_b[p] < 0x80 || NewLine(p) == 0);

    private bool IdentPartAt(int p) => p < _end && IsIdentPart(_b[p]) && (_b[p] < 0x80 || NewLine(p) == 0);

    private static bool IsDigit(int c) => c >= '0' && c <= '9';

    private Token Next()
    {
        if (_pos >= _end) return new Token(TokenKind.End, _pos, _pos, "");
        int start = _pos;
        int c = _b[_pos];
        if (IdentStartAt(_pos) || IsUnicodeEscape(_pos)) return Ident(start, false);
        if (c == '@' && IdentStartAt(_pos + 1))
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

    // \uXXXX or \UXXXXXXXX (Unicode escape in an identifier)
    private bool IsUnicodeEscape(int p)
    {
        if (p + 1 >= _end || _b[p] != '\\') return false;
        int n = _b[p + 1] == 'u' ? 4 : _b[p + 1] == 'U' ? 8 : 0;
        if (n == 0 || p + 2 + n > _end) return false;
        for (int i = 0; i < n; i++)
        {
            if (HexValue(_b[p + 2 + i]) < 0) return false;
        }
        return true;
    }

    private Token Ident(int start, bool verbatim)
    {
        // An identifier containing escapes becomes the unescaped string (it never becomes a keyword)
        var parts = new List<string>();
        int run = _pos;
        bool escaped = false;
        while (_pos < _end)
        {
            if (IdentPartAt(_pos)) _pos++;
            else if (IsUnicodeEscape(_pos))
            {
                if (_pos > run) parts.Add(_file.Slice(run, _pos));
                parts.Add(Escape(start));
                run = _pos;
                escaped = true;
            }
            else break;
        }
        if (_pos > run) parts.Add(_file.Slice(run, _pos));
        string text = string.Join("", parts);
        var t = new Token(TokenKind.Ident, start, _pos, text);
        t.IsKeyword = !verbatim && !escaped && IsKeyword(text);
        _file.Identifiers.Add(text);
        var bytes = new Utf8Bytes(text);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] >= 0x80)
            {
                _diags.Error("TCS1001", "unsupported syntax: NonAsciiIdentifier(" + text + ")", _file, start);
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
            while (_pos < _end && IdentPartAt(_pos)) _pos++;
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
        while (_pos < _end && IdentPartAt(_pos))
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
            if (!verbatim && NewLine(_pos) > 0)
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

    // Raw interpolated string. Holes are enclosed by as many { } as there are $. The parser reads the holes and the indentation of each line
    private Token RawInterp(int start, int dollars)
    {
        _pos += dollars;
        var t = RawLiteral(TokenKind.InterpString, start);
        t.RawDollars = dollars;
        t.Verbatim = true;
        return t;
    }

    // raw string literal ("""...""")
    private Token RawString(int start)
    {
        var t = RawLiteral(TokenKind.StringLit, start);
        var parts = new List<string>();
        int i = SkipRawIndent(_b, t.ContentStart, t.ContentEnd, t.RawIndent);
        int run = i;
        while (i < t.ContentEnd)
        {
            int nl = SourceFile.NewLineLength(_b, i, t.ContentEnd);
            if (nl == 0)
            {
                i++;
                continue;
            }
            i += nl;
            parts.Add(_file.Slice(run, i));
            i = SkipRawIndent(_b, i, t.ContentEnd, t.RawIndent);
            run = i;
        }
        parts.Add(_file.Slice(run, t.ContentEnd));
        t.Text = string.Join("", parts);
        return t;
    }

    // Reads the delimiters of a raw string and advances _pos past the closing delimiter. The content range goes into ContentStart / ContentEnd.
    // When whitespace and a newline follow the opening delimiter it is the multi-line form: everything up to that newline, the newline before the closing, and the closing line are removed from the content, and the byte count of the closing line's indentation goes into RawIndent.
    // The form is decided only by what follows the opening delimiter, and a newline inside an interpolation hole does not change it. Newlines in the content are kept as the bytes written
    private Token RawLiteral(TokenKind kind, int start)
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
        var t = new Token(kind, start, _pos, "");
        t.ContentStart = contentStart;
        t.ContentEnd = contentEnd;
        int p = contentStart;
        while (p < contentEnd && (_b[p] == ' ' || _b[p] == '\t')) p++;
        int openNl = SourceFile.NewLineLength(_b, p, contentEnd);
        if (openNl == 0) return t;
        int first = p + openNl;
        int lastNl = -1;
        int lastLine = first;
        int i = first;
        while (i < contentEnd)
        {
            int nl = SourceFile.NewLineLength(_b, i, contentEnd);
            if (nl == 0)
            {
                i++;
                continue;
            }
            lastNl = i;
            i += nl;
            lastLine = i;
        }
        t.ContentStart = first;
        t.ContentEnd = lastNl < 0 ? first : lastNl;
        t.RawIndent = contentEnd - lastLine;
        return t;
    }

    // _pos points at '\'. Returns the string value of the escape read and advances _pos
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
                return CodePoint(HexDigits(4, litStart), litStart);
            case 'U':
                return CodePoint(HexDigits(8, litStart), litStart);
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
        if (value < 0) _diags.Error("TCS1001", "unsupported syntax: NonAsciiCharLiteral", _file, start);
        var t = new Token(TokenKind.CharLit, start, _pos, "");
        t.Value = value < 0 ? 0 : value;
        return t;
    }

    // For an interpolated string only the range of the contents is recorded; the parser re-reads that range of the same file for the hole expressions
    private Token Interp(int start, int prefix, bool verbatim)
    {
        _pos += prefix + 1;
        int contentStart = _pos;
        int depth = 0;
        int parens = 0;
        // A hole's format (after the `:` in `{x:F2}`) has escapes, like string literal text
        bool inFormat = false;
        while (true)
        {
            if (_pos >= _end)
            {
                _diags.Error("CS1010", "Newline in constant", _file, start);
                break;
            }
            int c = _b[_pos];
            if (inFormat)
            {
                if (c == '}')
                {
                    inFormat = false;
                    depth = 0;
                    _pos++;
                    continue;
                }
                if (!verbatim && c == '\\' || verbatim && c == '"' && Peek(1) == '"')
                {
                    _pos += 2;
                    continue;
                }
                if (c == '"' || !verbatim && NewLine(_pos) > 0) break;
                _pos++;
                continue;
            }
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
                    parens = 0;
                    _pos++;
                    continue;
                }
                if (!verbatim && NewLine(_pos) > 0)
                {
                    _diags.Error("CS1010", "Newline in constant", _file, start);
                    break;
                }
                _pos++;
                continue;
            }
            // Inside a hole (quotes and brackets inside comments, strings, and characters are not counted)
            int skipped = SkipInHole(_b, _pos, _end);
            if (skipped > _pos)
            {
                _pos = skipped;
                continue;
            }
            if (c == '{') depth++;
            else if (c == '}') depth--;
            else if (c == '(' || c == '[') parens++;
            else if (c == ')' || c == ']') parens--;
            else if (c == ':' && depth == 1 && parens == 0)
            {
                inFormat = true;
                _pos++;
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

    // The character of an escape's code point. Unpaired surrogates (which cannot be UTF-8) and out-of-range values are rejected
    private string CodePoint(int v, int litStart)
    {
        if (v >= 0xD800 && v <= 0xDFFF)
        {
            _diags.Error("TCS1001", "unsupported syntax: LoneSurrogateLiteral", _file, litStart);
            return "";
        }
        if (v < 0 || v > 0x10FFFF)
        {
            _diags.Error("CS1009", "Unrecognized escape sequence", _file, litStart);
            return "";
        }
        return char.ConvertFromUtf32(v);
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
                // `>>` is formed by the parser from two adjacent `>` (to distinguish it from generic closing brackets)
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
