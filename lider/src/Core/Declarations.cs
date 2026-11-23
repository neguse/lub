// Picks up the names and lines of declarations (types, methods, constructors, properties, fields) from C# source.
// It only looks at syntax, so it does not check types. It does not read inside strings and comments, and skips method bodies and initializers.
// Enum members, operators and indexers are not picked up.
using System.Collections.Generic;

namespace LiderCore;

public class Declaration
{
    // "Outer.Inner.Member" (namespace not included)
    public string Name = "";
    // From 0
    public int Line;
    // A const field
    public bool Const;
    // A type declaration
    public bool IsType;
    // A declaration with statements (method, constructor, property with a body)
    public bool IsCode;
}

public static class Declarations
{
    static readonly List<string> typeWords = new List<string> { "class", "struct", "interface", "enum", "record" };
    static readonly List<string> skipWords = new List<string> { "using", "namespace", "extern", "global" };
    // List cannot hold null, so this marks a '{' that is not a type
    const string Body = "{";

    public static List<Declaration> Scan(List<string> lines)
    {
        var found = new List<Declaration>();
        var s = string.Join("\n", lines);
        // Type name for each open '{' ("" for a namespace, Body for bodies and initializers)
        var stack = new List<string>();
        var head = "";
        // Whether head is still only whitespace (calling Trim every time stalls on every keystroke in long files)
        var headEmpty = true;
        var headLine = 0;
        var line = 0;
        var parens = 0;
        // After '=' (skip to ';')
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
                // Up to the closing ' (it can be long, like '\u0041')
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
            // Appends byte by byte (a UTF-8 character becomes whole again once its following bytes are appended)
            head += c == '\n' ? " " : Utf8.Sub(s, i, 1);
        }
        return found;
    }

    // Position of the closing '"' of the string that starts with the '"' at s[at] (handles @ strings and """)
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

    // If it is the header of a type declaration, the name of that type
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
            // record struct P, record class P
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

    // Reads the name from the header of a member inside a type (the name before '(' or the last name in the header)
    static void Member(List<Declaration> found, List<string> stack, string head, int line, bool body)
    {
        if (skipWords.Contains(FirstWord(head)) || stack.Count == 0)
            return;
        // The parameter parentheses come after a tuple return type and before a constructor initializer (: base(...)) and where
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
        // Strips the <T> of a generic method
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
