// C# のソースから宣言 (型、メソッド、コンストラクタ、プロパティ、フィールド) の名前と行を拾う。
// 構文だけを見るので、型は調べない。文字列とコメントの中は読まず、メソッドの本体と初期化子の中は飛ばす。
// enum の要素、演算子、indexer は拾わない。
using System.Collections.Generic;

namespace LiderCore;

public class Declaration
{
    // "Outer.Inner.Member" (namespace は含めない)
    public string Name = "";
    // 0 から
    public int Line;
    // const のフィールド
    public bool Const;
    // 型の宣言
    public bool IsType;
    // 文を持つ宣言 (メソッド、コンストラクタ、本体のあるプロパティ)
    public bool IsCode;
}

public static class Declarations
{
    static readonly List<string> typeWords = new List<string> { "class", "struct", "interface", "enum", "record" };
    static readonly List<string> skipWords = new List<string> { "using", "namespace", "extern", "global" };
    // List に null を入れられないので、型でない '{' の印にする
    const string Body = "{";

    public static List<Declaration> Scan(List<string> lines)
    {
        var found = new List<Declaration>();
        var s = string.Join("\n", lines);
        // 開いている '{' ごとの型の名前 (namespace は ""、本体や初期化子は Body)
        var stack = new List<string>();
        var head = "";
        // head がまだ空白だけか (毎回 Trim すると、長い file で打つたびに止まる)
        var headEmpty = true;
        var headLine = 0;
        var line = 0;
        var parens = 0;
        // '=' の後 (';' まで読み飛ばす)
        var skipping = false;
        for (var i = 0; i < Utf8.Bytes(s); i++)
        {
            var c = Utf8.At(s, i);
            if (c == '\n')
                line++;
            var next = i + 1 < Utf8.Bytes(s) ? Utf8.At(s, i + 1) : '\0';
            if (c == '/' && next == '/')
            {
                while (i + 1 < Utf8.Bytes(s) && Utf8.At(s, i + 1) != '\n')
                    i++;
                continue;
            }
            if (c == '/' && next == '*')
            {
                i += 2;
                while (i + 1 < Utf8.Bytes(s) && !(Utf8.At(s, i) == '*' && Utf8.At(s, i + 1) == '/'))
                {
                    if (Utf8.At(s, i) == '\n')
                        line++;
                    i++;
                }
                i++;
                continue;
            }
            if (c == '"')
            {
                var close = SkipString(s, i);
                for (var j = i; j < close && j < Utf8.Bytes(s); j++)
                    if (Utf8.At(s, j) == '\n')
                        line++;
                i = close;
                continue;
            }
            if (c == '\'')
            {
                // 閉じる ' まで ('\u0041' のように長いこともある)
                i++;
                while (i < Utf8.Bytes(s) && Utf8.At(s, i) != '\'' && Utf8.At(s, i) != '\n')
                    i += Utf8.At(s, i) == '\\' ? 2 : 1;
                continue;
            }
            if (c == '#' && headEmpty)
            {
                while (i + 1 < Utf8.Bytes(s) && Utf8.At(s, i + 1) != '\n')
                    i++;
                continue;
            }
            var inBody = stack.Count > 0 && stack[stack.Count - 1] == Body;
            if (c == '{')
            {
                if (inBody || skipping)
                {
                    stack.Add(Body);
                    continue;
                }
                var type = TypeName(head);
                if (type != null)
                {
                    Add(found, stack, type, headLine);
                    found[found.Count - 1].IsType = true;
                    stack.Add((" " + head + " ").Contains(" enum ") ? Body : type);
                }
                else if (FirstWord(head) == "namespace")
                    stack.Add("");
                else
                {
                    Member(found, stack, head, headLine, true);
                    stack.Add(Body);
                }
                head = "";
                headEmpty = true;
                parens = 0;
                continue;
            }
            if (c == '}')
            {
                if (stack.Count > 0)
                    stack.RemoveAt(stack.Count - 1);
                if (!skipping)
                {
                    head = "";
                    headEmpty = true;
                }
                continue;
            }
            if (inBody)
                continue;
            if (c == ';')
            {
                if (!skipping)
                {
                    var type = TypeName(head);
                    if (type != null)
                    {
                        Add(found, stack, type, headLine);
                        found[found.Count - 1].IsType = true;
                    }
                    else
                        Member(found, stack, head, headLine, false);
                }
                head = "";
                headEmpty = true;
                parens = 0;
                skipping = false;
                continue;
            }
            if (skipping)
                continue;
            if (c == '[' && parens == 0 && headEmpty)
            {
                var depth = 1;
                while (i + 1 < Utf8.Bytes(s) && depth > 0)
                {
                    i++;
                    if (Utf8.At(s, i) == '[')
                        depth++;
                    else if (Utf8.At(s, i) == ']')
                        depth--;
                    else if (Utf8.At(s, i) == '\n')
                        line++;
                }
                continue;
            }
            if (c == '(')
                parens++;
            else if (c == ')')
                parens--;
            else if (c == '=' && parens == 0 && next != '=')
            {
                Member(found, stack, head, headLine, false);
                head = "";
                headEmpty = true;
                skipping = true;
                continue;
            }
            if (headEmpty && c != ' ' && c != '\n' && c != '\r' && c != '\t')
            {
                headLine = line;
                headEmpty = false;
            }
            // byte ごとに足す (UTF-8 の文字は、続く byte を足し終わると元に戻る)
            head += c == '\n' ? " " : Utf8.Sub(s, i, 1);
        }
        return found;
    }

    // s[at] の '"' で始まる文字列の閉じる '"' の位置 (@ の付いた文字列と """ に対応する)
    static int SkipString(string s, int at)
    {
        if (at + 2 < Utf8.Bytes(s) && Utf8.At(s, at + 1) == '"' && Utf8.At(s, at + 2) == '"')
        {
            var i = at + 3;
            while (i + 2 < Utf8.Bytes(s) && !(Utf8.At(s, i) == '"' && Utf8.At(s, i + 1) == '"' && Utf8.At(s, i + 2) == '"'))
                i++;
            return i + 2;
        }
        var verbatim = (at > 0 && Utf8.At(s, at - 1) == '@') || (at > 1 && Utf8.At(s, at - 2) == '@');
        for (var i = at + 1; i < Utf8.Bytes(s); i++)
        {
            var c = Utf8.At(s, i);
            if (c == '\n' && !verbatim)
                return i - 1;
            if (c == '\\' && !verbatim)
                i++;
            else if (c == '"')
            {
                if (verbatim && i + 1 < Utf8.Bytes(s) && Utf8.At(s, i + 1) == '"')
                    i++;
                else
                    return i;
            }
        }
        return Utf8.Bytes(s);
    }

    static bool IsWordChar(int c)
    {
        return char.IsLetterOrDigit((char)c) || c == '_';
    }

    static string FirstWord(string head)
    {
        var t = head.Trim();
        var end = 0;
        while (end < Utf8.Bytes(t) && IsWordChar(Utf8.At(t, end)))
            end++;
        return Utf8.Sub(t, 0, end);
    }

    // 型の宣言の見出しなら、その型の名前
    static string? TypeName(string head)
    {
        var i = 0;
        while (i < Utf8.Bytes(head))
        {
            if (Utf8.At(head, i) == '(' || Utf8.At(head, i) == '<')
                return null;
            if (!IsWordChar(Utf8.At(head, i)))
            {
                i++;
                continue;
            }
            var start = i;
            while (i < Utf8.Bytes(head) && IsWordChar(Utf8.At(head, i)))
                i++;
            var word = Utf8.Sub(head, start, i - start);
            if (!typeWords.Contains(word))
                continue;
            while (i < Utf8.Bytes(head) && !IsWordChar(Utf8.At(head, i)))
                i++;
            start = i;
            while (i < Utf8.Bytes(head) && IsWordChar(Utf8.At(head, i)))
                i++;
            // record struct P、record class P
            if (word == "record" && (Utf8.Sub(head, start, i - start) == "struct" || Utf8.Sub(head, start, i - start) == "class"))
            {
                while (i < Utf8.Bytes(head) && !IsWordChar(Utf8.At(head, i)))
                    i++;
                start = i;
                while (i < Utf8.Bytes(head) && IsWordChar(Utf8.At(head, i)))
                    i++;
            }
            return i > start ? Utf8.Sub(head, start, i - start) : null;
        }
        return null;
    }

    // 型の中のメンバーの見出しから名前を読む ('(' の前の名前か、見出しの最後の名前)
    static void Member(List<Declaration> found, List<string> stack, string head, int line, bool body)
    {
        if (skipWords.Contains(FirstWord(head)) || stack.Count == 0)
            return;
        // 引数の括弧は、戻り値の組の型より後ろで、コンストラクタの初期化子 (: base(...)) と where より前
        var open = -1;
        var depth = 0;
        for (var i = 0; i < Utf8.Bytes(head); i++)
        {
            if (Utf8.At(head, i) == ':' && depth == 0 && open >= 0)
                break;
            if (depth == 0 && i + 7 <= Utf8.Bytes(head) && Utf8.Sub(head, i, 7) == " where ")
                break;
            if (Utf8.At(head, i) == '(' && depth++ == 0)
                open = i;
            else if (Utf8.At(head, i) == ')')
                depth--;
        }
        var end = open >= 0 ? open : Utf8.Bytes(head);
        while (end > 0 && (Utf8.At(head, end - 1) == ' ' || Utf8.At(head, end - 1) == '\t' || Utf8.At(head, end - 1) == '\r'))
            end--;
        // generic のメソッドの <T> を除く
        if (open >= 0 && end > 0 && Utf8.At(head, end - 1) == '>')
        {
            while (end > 0 && Utf8.At(head, end - 1) != '<')
                end--;
            end--;
            while (end > 0 && Utf8.At(head, end - 1) == ' ')
                end--;
        }
        var start = end;
        while (start > 0 && IsWordChar(Utf8.At(head, start - 1)))
            start--;
        var name = Utf8.Sub(head, start, end - start);
        if (name == "" || char.IsDigit((char)Utf8.At(name, 0)) || name == "this" || name == "base" || name == "new" || Utf8.Sub(head, 0, start).Contains("operator"))
            return;
        Add(found, stack, name, line);
        found[found.Count - 1].Const = (" " + head + " ").Contains(" const ");
        found[found.Count - 1].IsCode = open >= 0 || body;
    }

    static void Add(List<Declaration> found, List<string> stack, string name, int line)
    {
        var full = name;
        for (var i = stack.Count - 1; i >= 0; i--)
            if (stack[i] != Body && stack[i] != "")
                full = stack[i] + "." + full;
        found.Add(new Declaration { Name = full, Line = line });
    }
}
