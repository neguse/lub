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

    // 条件付きの compile (#define / #undef / #if / #elif / #else / #endif) の状態。
    // 段ごとに、その段が有効か (_condActive) と、既にどれかの枝を取ったか (_condTaken) を持つ
    private readonly HashSet<string> _defines = new HashSet<string>();
    private readonly List<bool> _condActive = new List<bool>();
    private readonly List<bool> _condTaken = new List<bool>();
    private readonly List<int> _condPos = new List<int>();

    // ファイルの最初の token を読んだか (#define / #undef はその前にだけ書ける)
    private bool _sawToken;

    private bool Active() => _condActive.Count == 0 || _condActive[_condActive.Count - 1];

    private bool ParentActive() => _condActive.Count < 2 || _condActive[_condActive.Count - 2];

    // 1 行の指令を処理する。無効な枝に入ったら、次の指令の行まで行ごと読み飛ばす
    private void Directive()
    {
        DirectiveLine();
        while (_pos < _end && !Active())
        {
            int p = _pos;
            while (p < _end && (_b[p] == ' ' || _b[p] == '\t' || _b[p] == '\r' || _b[p] == '\n')) p++;
            if (p < _end && _b[p] == '#')
            {
                _pos = p;
                DirectiveLine();
            }
            else
            {
                _pos = p;
                while (_pos < _end && _b[_pos] != '\n') _pos++;
            }
        }
    }

    // #region / #endregion / #nullable / #pragma は読み飛ばす。#error / #warning / #line などはサブセットに無い
    private void DirectiveLine()
    {
        int start = _pos;
        while (_pos < _end && _b[_pos] != '\n') _pos++;
        string line = _file.Slice(start, _pos).Trim();
        // 行末の // の注釈を除く
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
        // 無効な枝の中では条件の指令だけを数え、他の指令は読まない
        if (!Active() && name != "if" && name != "elif" && name != "else" && name != "endif") return;
        if ((name == "define" || name == "undef") && _sawToken)
        {
            _diags.Error("CS1032", "Cannot define/undefine preprocessor symbols after first token in file", _file, start);
            return;
        }
        switch (name)
        {
            case "nullable":
                // 注釈の文脈。C backend への型の表記 (var の参照型の local は `?` 付き) にだけ効く。ファイルの中の範囲は見ない
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

    // ファイルの終わりで閉じていない #if
    public void CheckDirectivesClosed()
    {
        if (_condPos.Count > 0) _diags.Error("CS1027", "#endif directive expected", _file, _condPos[_condPos.Count - 1]);
    }

    // #if の式: || && == != ! ( ) true false と記号
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
        // != は ! と区別する
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

    private static bool IsDigit(int c) => c >= '0' && c <= '9';

    private Token Next()
    {
        if (_pos >= _end) return new Token(TokenKind.End, _pos, _pos, "");
        int start = _pos;
        int c = _b[_pos];
        if (IsIdentStart(c) || IsUnicodeEscape(_pos)) return Ident(start, false);
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

    // \uXXXX か \UXXXXXXXX (識別子の中の Unicode escape)
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
        // escape を含む識別子は escape を解いた文字列にする (keyword にはならない)
        var parts = new List<string>();
        int run = _pos;
        bool escaped = false;
        while (_pos < _end)
        {
            if (IsIdentPart(_b[_pos])) _pos++;
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

    // 補間文字列は中身の範囲だけを覚え、穴の式は parser が同じファイルの範囲を読み直す
    private Token Interp(int start, int prefix, bool verbatim)
    {
        _pos += prefix + 1;
        int contentStart = _pos;
        int depth = 0;
        int parens = 0;
        // 穴の書式 (`{x:F2}` の `:` の後ろ) は文字列の字面と同じく escape を持つ
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
                if (c == '"' || !verbatim && c == '\n') break;
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
                if (!verbatim && c == '\n')
                {
                    _diags.Error("CS1010", "Newline in constant", _file, start);
                    break;
                }
                _pos++;
                continue;
            }
            // 穴の中 (ブロックのコメントの中の引用符と括弧は数えない)
            if (c == '/' && Peek(1) == '*')
            {
                _pos += 2;
                while (_pos < _end && !(_b[_pos] == '*' && Peek(1) == '/')) _pos++;
                if (_pos < _end) _pos += 2;
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

    // escape の code point の文字。対になっていない surrogate (UTF-8 にできない) と範囲の外は止める
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
