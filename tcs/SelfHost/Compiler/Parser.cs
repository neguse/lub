using System.Collections.Generic;
using TinySystem.Text;

namespace TcsSelf;

// 再帰下降の parser。誤りは診断に積み、読み進める (例外は無い)。
public sealed class Parser
{
    private readonly SourceFile _file;
    private readonly Diagnostics _diags;
    private readonly List<Token> _toks;
    private int _i;
    private int _errorsAtStart;

    public Parser(SourceFile file, Diagnostics diags, List<Token> tokens)
    {
        _file = file;
        _diags = diags;
        _toks = tokens;
    }

    public static Node ParseFile(SourceFile file, Diagnostics diags)
    {
        var lexer = new Lexer(file, diags, 0, file.Bytes.Length);
        var p = new Parser(file, diags, lexer.Tokenize());
        return p.CompilationUnit();
    }

    // ---- token の道具 ----

    private Token Cur => _toks[_i];

    private Token PeekTok(int n)
    {
        int j = _i + n;
        if (j >= _toks.Count) j = _toks.Count - 1;
        return _toks[j];
    }

    private bool IsP(string s) => Cur.Kind == TokenKind.Punct && Cur.Text == s;

    private bool IsPAt(int n, string s)
    {
        var t = PeekTok(n);
        return t.Kind == TokenKind.Punct && t.Text == s;
    }

    private bool IsKw(string s) => Cur.Kind == TokenKind.Ident && Cur.Text == s && Cur.IsKeyword;

    private bool IsKwAt(int n, string s)
    {
        var t = PeekTok(n);
        return t.Kind == TokenKind.Ident && t.Text == s && t.IsKeyword;
    }

    // 文脈キーワード (var, get, set, record など) も含む素の識別子
    private bool IsIdent() => Cur.Kind == TokenKind.Ident && !Cur.IsKeyword;

    private bool IsIdentAt(int n)
    {
        var t = PeekTok(n);
        return t.Kind == TokenKind.Ident && !t.IsKeyword;
    }

    private bool IsContextual(string s) => Cur.Kind == TokenKind.Ident && !Cur.IsKeyword && Cur.Text == s;

    private Token Advance()
    {
        var t = Cur;
        if (_i < _toks.Count - 1) _i++;
        return t;
    }

    private bool Accept(string s)
    {
        if (IsP(s))
        {
            Advance();
            return true;
        }
        return false;
    }

    private bool AcceptKw(string s)
    {
        if (IsKw(s))
        {
            Advance();
            return true;
        }
        return false;
    }

    private void Expect(string s)
    {
        if (IsP(s))
        {
            Advance();
            return;
        }
        Error("CS1003", "Syntax error, '" + s + "' expected");
    }

    private void ExpectKw(string s)
    {
        if (!AcceptKw(s)) Error("CS1003", "Syntax error, '" + s + "' expected");
    }

    private string ExpectIdent()
    {
        if (Cur.Kind == TokenKind.Ident && !Cur.IsKeyword) return Advance().Text;
        Error("CS1001", "Identifier expected");
        return "";
    }

    private void Error(string code, string message)
    {
        // 同じ位置で誤りを繰り返さない
        if (_diags.ErrorCount > _errorsAtStart && _lastErrorPos == Cur.Pos) return;
        _lastErrorPos = Cur.Pos;
        _diags.Error(code, message, _file, Cur.Pos);
    }

    private int _lastErrorPos = -1;

    private void Unsupported(string what, int pos)
    {
        _diags.Error("TCS1001", "Unsupported syntax: " + what, _file, pos);
    }

    private Node N(NodeKind kind) => new Node(kind, _file, Cur.Pos);

    private Node NAt(NodeKind kind, int pos) => new Node(kind, _file, pos);

    private Node Done(Node n)
    {
        n.End = _i > 0 ? _toks[_i - 1].End : n.Pos;
        return n;
    }

    // ---- compilation unit ----

    // CompilationUnit: List = using と member、List2 = top-level の文
    private Node CompilationUnit()
    {
        var unit = N(NodeKind.CompilationUnit);
        _errorsAtStart = _diags.ErrorCount;
        Usings(unit);
        while (Cur.Kind != TokenKind.End)
        {
            int before = _i;
            if (IsTopLevelStatementStart())
            {
                unit.Add2(Statement());
                if (_i == before) Advance();
                continue;
            }
            var m = NamespaceMember();
            if (m != null) unit.Add(m);
            if (_i == before)
            {
                Error("CS1022", "Type or namespace definition, or end-of-file expected");
                Advance();
            }
        }
        return Done(unit);
    }

    // Using: Text = 名前
    private void Usings(Node into)
    {
        while (IsKw("using") || IsContextual("global") && IsKwAt(1, "using"))
        {
            int pos = Cur.Pos;
            if (IsContextual("global"))
            {
                Advance();
                Unsupported("global using", pos);
            }
            Advance();
            if (IsKw("static"))
            {
                Unsupported("using static", pos);
                Advance();
            }
            if (IsIdent() && IsPAt(1, "="))
            {
                Unsupported("using alias", pos);
                Advance();
                Advance();
            }
            var u = NAt(NodeKind.Using, pos);
            u.Text = QualifiedName();
            Expect(";");
            into.Add(Done(u));
        }
    }

    private string QualifiedName()
    {
        var parts = new List<string>();
        parts.Add(ExpectIdent());
        while (IsP(".") && IsIdentAt(1))
        {
            Advance();
            parts.Add(ExpectIdent());
        }
        return string.Join(".", parts);
    }

    // Namespace: Text = 名前、List = member、Value = 1 なら file-scoped
    private Node? NamespaceMember()
    {
        if (IsKw("namespace"))
        {
            var ns = N(NodeKind.Namespace);
            Advance();
            ns.Text = QualifiedName();
            if (Accept(";"))
            {
                ns.Value = 1;
                Usings(ns);
                while (Cur.Kind != TokenKind.End)
                {
                    int before = _i;
                    var m = NamespaceMember();
                    if (m != null) ns.Add(m);
                    if (_i == before)
                    {
                        Error("CS1022", "Type or namespace definition, or end-of-file expected");
                        Advance();
                    }
                }
                return Done(ns);
            }
            Expect("{");
            Usings(ns);
            while (!IsP("}") && Cur.Kind != TokenKind.End)
            {
                int before = _i;
                var m = NamespaceMember();
                if (m != null) ns.Add(m);
                if (_i == before)
                {
                    Error("CS1022", "Type or namespace definition, or end-of-file expected");
                    Advance();
                }
            }
            Expect("}");
            return Done(ns);
        }
        var attrs = Attributes();
        int pos = Cur.Pos;
        int mods = Modifiers();
        if (IsTypeDeclStart()) return TypeDecl(pos, mods, attrs);
        // top-level の文はサブセットに無い
        if (Cur.Kind != TokenKind.End)
        {
            Unsupported("top-level statement", Cur.Pos);
            Statement();
        }
        return null;
    }

    // top-level の文か (namespace と型の宣言でない)
    private bool IsTopLevelStatementStart()
    {
        if (IsKw("namespace") || IsP("[")) return false;
        int save = _i;
        Modifiers();
        bool typeDecl = IsTypeDeclStart();
        _i = save;
        return !typeDecl;
    }

    private bool IsTypeDeclStart()
    {
        if (IsKw("class") || IsKw("struct") || IsKw("interface") || IsKw("enum")) return true;
        if (IsContextual("record")) return true;
        if (IsKw("delegate")) return true;
        return false;
    }

    // Attribute: Text = 名前、List = 引数の式
    private List<Node>? Attributes()
    {
        List<Node>? result = null;
        while (IsP("["))
        {
            Advance();
            if (IsIdent() && IsPAt(1, ":"))
            {
                Advance();
                Advance();
            }
            while (true)
            {
                var a = N(NodeKind.Attribute);
                a.Text = QualifiedName();
                if (Accept("("))
                {
                    while (!IsP(")") && Cur.Kind != TokenKind.End)
                    {
                        if (IsIdent() && (IsPAt(1, "=") || IsPAt(1, ":")))
                        {
                            Advance();
                            Advance();
                        }
                        a.Add(Expression());
                        if (!Accept(",")) break;
                    }
                    Expect(")");
                }
                if (result == null) result = new List<Node>();
                result.Add(Done(a));
                if (!Accept(",")) break;
            }
            Expect("]");
        }
        return result;
    }

    private int Modifiers()
    {
        int mods = 0;
        while (true)
        {
            int m = 0;
            if (Cur.Kind == TokenKind.Ident)
            {
                string t = Cur.Text;
                if (Cur.IsKeyword)
                {
                    switch (t)
                    {
                        case "public": m = Mods.Public; break;
                        case "private": m = Mods.Private; break;
                        case "protected": m = Mods.Protected; break;
                        case "internal": m = Mods.Internal; break;
                        case "static": m = Mods.Static; break;
                        case "readonly": m = Mods.Readonly; break;
                        case "const": m = Mods.Const; break;
                        case "sealed": m = Mods.Sealed; break;
                        case "abstract": m = Mods.Abstract; break;
                        case "override": m = Mods.Override; break;
                        case "virtual": m = Mods.Virtual; break;
                        case "new": m = IsPAt(1, "(") ? 0 : Mods.New; break;
                        case "extern": m = Mods.Extern; break;
                        case "unsafe": m = Mods.Unsafe; break;
                        case "volatile": m = Mods.Volatile; break;
                        case "implicit": m = Mods.Implicit; break;
                        case "explicit": m = Mods.Explicit; break;
                        case "event": m = Mods.Event; break;
                    }
                }
                else if (t == "partial" && (IsKwAt(1, "class") || IsKwAt(1, "struct") || IsKwAt(1, "interface") || IsIdentAt(1) && PeekTok(1).Text == "record" || IsKwAt(1, "void")))
                {
                    m = Mods.Partial;
                }
                else if (t == "async" && !IsPAt(1, "=") && !IsPAt(1, ";") && !IsPAt(1, "("))
                {
                    m = Mods.Async;
                }
                else if (t == "required" && IsIdentAt(1))
                {
                    m = Mods.Required;
                }
                else if (t == "file" && (IsKwAt(1, "class") || IsKwAt(1, "struct")))
                {
                    m = Mods.Private;
                }
            }
            if (m == 0) break;
            mods |= m;
            Advance();
        }
        return mods;
    }

    // TypeDecl: Value = DeclKinds、Text = 名前、Flags = 修飾子、List = member、List2 = 型引数の名前 (Name)、
    // List3 = 基底の型、A = record の positional parameter (Param の list を List に持つ Node)、D = 属性 (List)
    private Node TypeDecl(int pos, int mods, List<Node>? attrs)
    {
        var d = NAt(NodeKind.TypeDecl, pos);
        d.Flags = mods;
        if (attrs != null)
        {
            var holder = NAt(NodeKind.Attribute, pos);
            holder.List = attrs;
            d.D = holder;
        }
        if (IsKw("delegate"))
        {
            Unsupported("delegate declaration", Cur.Pos);
            while (!IsP(";") && Cur.Kind != TokenKind.End) Advance();
            Accept(";");
            d.Value = DeclKinds.Class;
            d.Text = "";
            return Done(d);
        }
        if (IsContextual("record"))
        {
            Advance();
            if (AcceptKw("struct")) d.Value = DeclKinds.RecordStruct;
            else
            {
                AcceptKw("class");
                d.Value = DeclKinds.RecordClass;
            }
        }
        else if (AcceptKw("class")) d.Value = DeclKinds.Class;
        else if (AcceptKw("struct")) d.Value = DeclKinds.Struct;
        else if (AcceptKw("interface")) d.Value = DeclKinds.Interface;
        else if (AcceptKw("enum")) d.Value = DeclKinds.Enum;
        d.Text = ExpectIdent();
        if (IsP("<"))
        {
            Advance();
            while (true)
            {
                var tp = N(NodeKind.Name);
                tp.Text = ExpectIdent();
                d.Add2(Done(tp));
                if (!Accept(",")) break;
            }
            Expect(">");
        }
        if (IsP("("))
        {
            var plist = N(NodeKind.Param);
            ParamList(plist);
            d.A = plist;
        }
        if (Accept(":"))
        {
            while (true)
            {
                var bt = Type();
                if (IsP("("))
                {
                    Unsupported("record base arguments", Cur.Pos);
                    SkipBalanced("(", ")");
                }
                d.Add3(bt);
                if (!Accept(",")) break;
            }
        }
        while (IsContextual("where"))
        {
            // 制約は読み飛ばす (型検査は Roslyn が正)
            while (!IsP("{") && !IsP(";") && Cur.Kind != TokenKind.End) Advance();
        }
        if (d.Value == DeclKinds.Enum)
        {
            Expect("{");
            while (!IsP("}") && Cur.Kind != TokenKind.End)
            {
                Attributes();
                var em = N(NodeKind.EnumMember);
                em.Text = ExpectIdent();
                if (Accept("=")) em.A = Expression();
                d.Add(Done(em));
                if (!Accept(",")) break;
            }
            Expect("}");
            Accept(";");
            return Done(d);
        }
        if (Accept(";")) return Done(d);
        Expect("{");
        while (!IsP("}") && Cur.Kind != TokenKind.End)
        {
            int before = _i;
            var m = Member(d);
            if (m != null) d.Add(m);
            if (_i == before)
            {
                Error("CS1519", "Invalid token '" + Cur.Text + "' in class, record, struct, or interface member declaration");
                Advance();
            }
        }
        Expect("}");
        Accept(";");
        return Done(d);
    }

    private void SkipBalanced(string open, string close)
    {
        int depth = 0;
        while (Cur.Kind != TokenKind.End)
        {
            if (IsP(open)) depth++;
            else if (IsP(close))
            {
                depth--;
                if (depth == 0)
                {
                    Advance();
                    return;
                }
            }
            Advance();
        }
    }

    private Node? Member(Node owner)
    {
        var attrs = Attributes();
        int pos = Cur.Pos;
        int mods = Modifiers();
        if (IsTypeDeclStart() && !(IsKw("delegate") && IsPAt(1, "(")))
        {
            return TypeDecl(pos, mods, attrs);
        }
        // constructor
        if (IsIdent() && Cur.Text == owner.Text && IsPAt(1, "("))
        {
            var c = NAt(NodeKind.Ctor, pos);
            c.Flags = mods;
            c.Text = Advance().Text;
            ParamList(c);
            if (Accept(":"))
            {
                var init = N(NodeKind.Call);
                if (AcceptKw("this")) init.Text = "this";
                else if (AcceptKw("base")) init.Text = "base";
                else Error("CS1018", "Keyword 'this' or 'base' expected");
                Arguments(init);
                c.D = Done(init);
            }
            MethodBody(c);
            return Done(c);
        }
        if (IsP("~"))
        {
            Unsupported("finalizer", Cur.Pos);
            Advance();
            ExpectIdent();
            var dummy = NAt(NodeKind.Method, pos);
            ParamList(dummy);
            MethodBody(dummy);
            return null;
        }
        if ((mods & (Mods.Implicit | Mods.Explicit)) != 0 && IsKw("operator"))
        {
            Unsupported("conversion operator", pos);
            Advance();
            Type();
            var dummy = NAt(NodeKind.OperatorDecl, pos);
            ParamList(dummy);
            MethodBody(dummy);
            return null;
        }
        if (mods != 0 && (mods & Mods.Event) != 0)
        {
            Unsupported("event", pos);
            while (!IsP(";") && !IsP("}") && Cur.Kind != TokenKind.End) Advance();
            Accept(";");
            return null;
        }
        var type = Type();
        if (IsKw("operator"))
        {
            var op = NAt(NodeKind.OperatorDecl, pos);
            op.Flags = mods;
            op.A = type;
            Advance();
            var ot = Advance();
            op.Text = ot.Text;
            if (ot.Text == ">" && IsP(">") && !Cur.SpaceBefore)
            {
                Advance();
                op.Text = ">>";
            }
            else if (ot.Kind == TokenKind.Ident)
            {
                op.Text = ot.Text;
            }
            ParamList(op);
            MethodBody(op);
            return Done(op);
        }
        if (IsKw("this") && IsPAt(1, "["))
        {
            Unsupported("indexer", Cur.Pos);
            Advance();
            SkipBalanced("[", "]");
            if (IsP("{")) SkipBalanced("{", "}");
            else
            {
                while (!IsP(";") && Cur.Kind != TokenKind.End) Advance();
                Accept(";");
            }
            return null;
        }
        // 明示的な interface の実装 (I.M)
        int namePos = Cur.Pos;
        string name = ExpectIdent();
        if (IsP(".") && IsIdentAt(1))
        {
            Unsupported("explicit interface implementation", namePos);
            Advance();
            name = ExpectIdent();
        }
        if (IsP("(") || IsP("<"))
        {
            var m = NAt(NodeKind.Method, pos);
            m.Flags = mods;
            m.A = type;
            m.Text = name;
            if (attrs != null)
            {
                var holder = NAt(NodeKind.Attribute, pos);
                holder.List = attrs;
                m.D = holder;
            }
            if (Accept("<"))
            {
                while (true)
                {
                    var tp = N(NodeKind.Name);
                    tp.Text = ExpectIdent();
                    m.Add2(Done(tp));
                    if (!Accept(",")) break;
                }
                Expect(">");
            }
            ParamList(m);
            while (IsContextual("where"))
            {
                while (!IsP("{") && !IsP(";") && !IsP("=>") && Cur.Kind != TokenKind.End) Advance();
            }
            MethodBody(m);
            return Done(m);
        }
        if (IsP("{") || IsP("=>") && !(mods != 0 && false))
        {
            if (IsP("=>"))
            {
                // 式本体の property
                var p = NAt(NodeKind.Property, pos);
                p.Flags = mods;
                p.A = type;
                p.Text = name;
                Advance();
                var get = N(NodeKind.Accessor);
                get.Text = "get";
                get.B = Expression();
                p.B = Done(get);
                Expect(";");
                return Done(p);
            }
            var prop = NAt(NodeKind.Property, pos);
            prop.Flags = mods;
            prop.A = type;
            prop.Text = name;
            Advance();
            while (!IsP("}") && Cur.Kind != TokenKind.End)
            {
                Attributes();
                int amods = Modifiers();
                var acc = N(NodeKind.Accessor);
                acc.Flags = amods;
                acc.Text = Cur.Text;
                if (IsContextual("get")) prop.B = acc;
                else if (IsContextual("set") || IsContextual("init")) prop.C = acc;
                else
                {
                    Error("CS1014", "A get or set accessor expected");
                    Advance();
                    continue;
                }
                Advance();
                if (IsP("{")) acc.A = Block();
                else if (Accept("=>"))
                {
                    acc.B = Expression();
                    Expect(";");
                }
                else Expect(";");
                Done(acc);
            }
            Expect("}");
            if (Accept("="))
            {
                prop.D = Expression();
                Expect(";");
            }
            return Done(prop);
        }
        // field
        var f = NAt(NodeKind.Field, pos);
        f.Flags = mods;
        f.A = type;
        if (attrs != null)
        {
            var holder = NAt(NodeKind.Attribute, pos);
            holder.List = attrs;
            f.D = holder;
        }
        var first = NAt(NodeKind.VarDeclarator, namePos);
        first.Text = name;
        if (Accept("=")) first.A = VariableInitializer();
        f.Add(Done(first));
        while (Accept(","))
        {
            var v = N(NodeKind.VarDeclarator);
            v.Text = ExpectIdent();
            if (Accept("=")) v.A = VariableInitializer();
            f.Add(Done(v));
        }
        Expect(";");
        return Done(f);
    }

    private Node VariableInitializer()
    {
        if (IsP("{"))
        {
            // 配列の初期化子 `int[] a = { 1, 2 }`
            var init = N(NodeKind.ArrayInit);
            Advance();
            while (!IsP("}") && Cur.Kind != TokenKind.End)
            {
                init.Add(VariableInitializer());
                if (!Accept(",")) break;
            }
            Expect("}");
            return Done(init);
        }
        return Expression();
    }

    // method / ctor の本体: B = Block、C = 式本体
    private void MethodBody(Node m)
    {
        if (IsP("{")) m.B = Block();
        else if (Accept("=>"))
        {
            m.C = Expression();
            Expect(";");
        }
        else Expect(";");
    }

    // Param: Text = 名前、A = 型 (lambda では null もある)、B = 既定値、Flags = out / ref / in / this / params
    private void ParamList(Node into)
    {
        Expect("(");
        while (!IsP(")") && Cur.Kind != TokenKind.End)
        {
            Attributes();
            var p = N(NodeKind.Param);
            while (true)
            {
                if (AcceptKw("out")) p.Flags |= Mods.Out;
                else if (AcceptKw("ref")) p.Flags |= Mods.Ref;
                else if (AcceptKw("in")) p.Flags |= Mods.In;
                else if (AcceptKw("this")) p.Flags |= Mods.This;
                else if (AcceptKw("params")) p.Flags |= Mods.Params;
                else if (AcceptKw("readonly")) p.Flags |= Mods.Readonly;
                else if (IsContextual("scoped") && IsIdentAt(1)) Advance();
                else break;
            }
            p.A = Type();
            p.Text = ExpectIdent();
            if (Accept("=")) p.B = Expression();
            into.Add(Done(p));
            if (!Accept(",")) break;
        }
        Expect(")");
    }

    // ---- 型 ----

    private static bool IsPredefTypeName(string s)
    {
        switch (s)
        {
            case "int":
            case "float":
            case "bool":
            case "string":
            case "char":
            case "byte":
            case "void":
            case "object":
            case "double":
            case "long":
            case "uint":
            case "short":
            case "ushort":
            case "sbyte":
            case "ulong":
            case "decimal":
                return true;
        }
        return false;
    }

    public Node Type()
    {
        var t = TryType(false);
        if (t != null) return t;
        Error("CS1031", "Type expected");
        var err = N(NodeKind.PredefType);
        err.Text = "?";
        return err;
    }

    // speculative = true のときは診断を出さず、型として読めなければ null を返して位置を戻す
    private Node? TryType(bool speculative)
    {
        int save = _i;
        Node? t = null;
        if (IsP("("))
        {
            // tuple 型
            var tt = N(NodeKind.TupleType);
            Advance();
            while (true)
            {
                var et = TryType(speculative);
                if (et == null)
                {
                    _i = save;
                    return null;
                }
                var elem = NAt(NodeKind.Param, et.Pos);
                elem.A = et;
                if (IsIdent()) elem.Text = Advance().Text;
                tt.Add(elem);
                if (!Accept(",")) break;
            }
            if (!IsP(")") || tt.Count < 2)
            {
                _i = save;
                return null;
            }
            Advance();
            t = Done(tt);
        }
        else if (Cur.Kind == TokenKind.Ident && Cur.IsKeyword && IsPredefTypeName(Cur.Text))
        {
            t = N(NodeKind.PredefType);
            t.Text = Advance().Text;
            Done(t);
        }
        else if (IsIdent())
        {
            t = SimpleTypeName(speculative);
            if (t == null)
            {
                _i = save;
                return null;
            }
            while (IsP(".") && IsIdentAt(1))
            {
                Advance();
                var q = SimpleTypeName(speculative);
                if (q == null)
                {
                    _i = save;
                    return null;
                }
                q.A = t;
                t = q;
            }
        }
        else
        {
            return null;
        }
        // suffix: ? と []
        while (true)
        {
            if (IsP("?") && !speculative)
            {
                var n = NAt(NodeKind.NullableType, t.Pos);
                Advance();
                n.A = t;
                t = Done(n);
            }
            else if (IsP("?") && speculative)
            {
                // 式の `a ? b : c` と区別する: 型のあとに識別子か > , ) ] が続くときだけ nullable
                var nx = PeekTok(1);
                bool ok = nx.Kind == TokenKind.Ident && !nx.IsKeyword || nx.Kind == TokenKind.Punct && (nx.Text == ">" || nx.Text == "," || nx.Text == ")" || nx.Text == "[" || nx.Text == "]");
                if (!ok) break;
                var n = NAt(NodeKind.NullableType, t.Pos);
                Advance();
                n.A = t;
                t = Done(n);
            }
            else if (IsP("[") && IsPAt(1, "]"))
            {
                var a = NAt(NodeKind.ArrayType, t.Pos);
                Advance();
                Advance();
                a.A = t;
                t = Done(a);
            }
            else if (IsP("[") && IsPAt(1, ","))
            {
                if (!speculative) Unsupported("multi-dimensional array", Cur.Pos);
                SkipBalanced("[", "]");
            }
            else break;
        }
        return t;
    }

    // TypeName: Text = 名前、List = 型引数、A = 修飾 (外側の名前)
    private Node? SimpleTypeName(bool speculative)
    {
        var n = N(NodeKind.TypeName);
        n.Text = Advance().Text;
        if (IsP("<"))
        {
            int save = _i;
            Advance();
            bool ok = true;
            while (true)
            {
                var arg = TryType(true);
                if (arg == null)
                {
                    ok = false;
                    break;
                }
                n.Add(arg);
                if (!Accept(",")) break;
            }
            if (ok && IsP(">")) Advance();
            else if (ok && IsP(">=") )
            {
                ok = false;
            }
            else ok = false;
            if (!ok)
            {
                if (speculative) return null;
                _i = save;
                n.List = null;
                Error("CS1003", "Syntax error, '>' expected");
            }
        }
        return Done(n);
    }

    // ---- 文 ----

    // Block: List = 文
    private Node Block()
    {
        var b = N(NodeKind.Block);
        Expect("{");
        while (!IsP("}") && Cur.Kind != TokenKind.End)
        {
            int before = _i;
            b.Add(Statement());
            if (_i == before)
            {
                Error("CS1513", "} expected");
                Advance();
            }
        }
        Expect("}");
        return Done(b);
    }

    private Node Statement()
    {
        int pos = Cur.Pos;
        if (IsP("{")) return Block();
        if (IsP(";"))
        {
            var e = N(NodeKind.Empty);
            Advance();
            return Done(e);
        }
        if (Cur.Kind == TokenKind.Ident && Cur.IsKeyword)
        {
            switch (Cur.Text)
            {
                case "if": return IfStatement();
                case "while":
                    {
                        // While: A = 条件、B = 本体
                        var w = N(NodeKind.While);
                        Advance();
                        Expect("(");
                        w.A = Expression();
                        Expect(")");
                        w.B = EmbeddedStatement();
                        return Done(w);
                    }
                case "do":
                    {
                        // DoWhile: A = 条件、B = 本体
                        var d = N(NodeKind.DoWhile);
                        Advance();
                        d.B = EmbeddedStatement();
                        ExpectKw("while");
                        Expect("(");
                        d.A = Expression();
                        Expect(")");
                        Expect(";");
                        return Done(d);
                    }
                case "for": return ForStatement();
                case "foreach": return ForeachStatement();
                case "break":
                    {
                        var b = N(NodeKind.Break);
                        Advance();
                        Expect(";");
                        return Done(b);
                    }
                case "continue":
                    {
                        var c = N(NodeKind.Continue);
                        Advance();
                        Expect(";");
                        return Done(c);
                    }
                case "return":
                    {
                        // Return: A = 値
                        var r = N(NodeKind.Return);
                        Advance();
                        if (!IsP(";")) r.A = Expression();
                        Expect(";");
                        return Done(r);
                    }
                case "switch":
                    if (!IsSwitchExpressionStatement()) return SwitchStatement();
                    break;
                case "throw":
                case "try":
                case "goto":
                case "lock":
                case "using":
                case "fixed":
                case "unsafe":
                case "checked":
                case "unchecked":
                case "yield":
                    if (Cur.Text == "checked" || Cur.Text == "unchecked")
                    {
                        if (IsPAt(1, "(")) break;
                    }
                    return UnsupportedStatement(Cur.Text);
                case "const":
                    {
                        Advance();
                        var decl = LocalDeclaration(true);
                        Expect(";");
                        return decl;
                    }
            }
        }
        if (IsContextual("yield") && (IsKwAt(1, "return") || IsKwAt(1, "break"))) return UnsupportedStatement("yield");
        if (IsContextual("await")) return UnsupportedStatement("await");
        // local function / 宣言 / 式
        if (IsContextual("var") && IsPAt(1, "("))
        {
            // var (a, b) = ...
            var dd = N(NodeKind.DeconstructDecl);
            Advance();
            Advance();
            while (true)
            {
                var nm = N(NodeKind.Name);
                nm.Text = Cur.Kind == TokenKind.Ident ? Advance().Text : "";
                dd.Add(Done(nm));
                if (!Accept(",")) break;
            }
            Expect(")");
            Expect("=");
            dd.A = Expression();
            Expect(";");
            return Done(dd);
        }
        if (IsLocalDeclarationStart())
        {
            var decl = LocalDeclaration(false);
            if (decl.Kind == NodeKind.Unsupported) return decl;
            Expect(";");
            return decl;
        }
        var es = N(NodeKind.ExprStmt);
        es.A = Expression();
        Expect(";");
        return Done(es);
    }

    private bool IsSwitchExpressionStatement() => false;

    private Node UnsupportedStatement(string what)
    {
        var u = N(NodeKind.Unsupported);
        u.Text = what;
        Unsupported("'" + what + "' statement", Cur.Pos);
        // 文の終わりまで読み飛ばす
        int depth = 0;
        while (Cur.Kind != TokenKind.End)
        {
            if (IsP("{")) depth++;
            else if (IsP("}"))
            {
                if (depth == 0) break;
                depth--;
                if (depth == 0)
                {
                    Advance();
                    if (IsKw("catch") || IsKw("finally"))
                    {
                        while (!IsP("{") && Cur.Kind != TokenKind.End) Advance();
                        continue;
                    }
                    break;
                }
            }
            else if (IsP(";") && depth == 0)
            {
                Advance();
                break;
            }
            Advance();
        }
        return Done(u);
    }

    private bool IsLocalDeclarationStart()
    {
        if (!(Cur.Kind == TokenKind.Ident || IsP("("))) return false;
        if (Cur.Kind == TokenKind.Ident && Cur.IsKeyword && !IsPredefTypeName(Cur.Text)) return false;
        int save = _i;
        var t = TryType(true);
        bool result = false;
        if (t != null && IsIdent())
        {
            var nx = PeekTok(1);
            result = nx.Kind == TokenKind.Punct && (nx.Text == "=" || nx.Text == ";" || nx.Text == "," || nx.Text == "(");
            if (nx.Kind == TokenKind.Ident && nx.Text == "in" && nx.IsKeyword) result = true;
        }
        _i = save;
        return result;
    }

    // LocalDecl: A = 型、List = VarDeclarator、Flags = const
    private Node LocalDeclaration(bool isConst)
    {
        var d = N(NodeKind.LocalDecl);
        if (isConst) d.Flags = Mods.Const;
        d.A = Type();
        int namePos = Cur.Pos;
        string name = ExpectIdent();
        if (IsP("("))
        {
            // local function
            Unsupported("local function", namePos);
            var dummy = N(NodeKind.Method);
            ParamList(dummy);
            MethodBody(dummy);
            var u = NAt(NodeKind.Unsupported, namePos);
            u.Text = "local function";
            return Done(u);
        }
        var first = NAt(NodeKind.VarDeclarator, namePos);
        first.Text = name;
        if (Accept("=")) first.A = VariableInitializer();
        d.Add(Done(first));
        while (Accept(","))
        {
            var v = N(NodeKind.VarDeclarator);
            v.Text = ExpectIdent();
            if (Accept("=")) v.A = VariableInitializer();
            d.Add(Done(v));
        }
        return Done(d);
    }

    private Node EmbeddedStatement() => Statement();

    // If: A = 条件、B = then、C = else
    private Node IfStatement()
    {
        var n = N(NodeKind.If);
        Advance();
        Expect("(");
        n.A = Expression();
        Expect(")");
        n.B = EmbeddedStatement();
        if (AcceptKw("else")) n.C = EmbeddedStatement();
        return Done(n);
    }

    // For: List = 初期化 (LocalDecl か ExprStmt)、A = 条件、List2 = 更新の式、B = 本体
    private Node ForStatement()
    {
        var n = N(NodeKind.For);
        Advance();
        Expect("(");
        if (!IsP(";"))
        {
            if (IsLocalDeclarationStart()) n.Add(LocalDeclaration(false));
            else
            {
                while (true)
                {
                    var es = N(NodeKind.ExprStmt);
                    es.A = Expression();
                    n.Add(Done(es));
                    if (!Accept(",")) break;
                }
            }
        }
        Expect(";");
        if (!IsP(";")) n.A = Expression();
        Expect(";");
        if (!IsP(")"))
        {
            while (true)
            {
                n.Add2(Expression());
                if (!Accept(",")) break;
            }
        }
        Expect(")");
        n.B = EmbeddedStatement();
        return Done(n);
    }

    // Foreach: A = 型、Text = 変数名、B = collection、C = 本体。List = 分解 (var (k, v)) の名前
    private Node ForeachStatement()
    {
        var n = N(NodeKind.Foreach);
        Advance();
        Expect("(");
        if (IsContextual("var") && IsPAt(1, "("))
        {
            Advance();
            Advance();
            while (true)
            {
                var nm = N(NodeKind.Name);
                nm.Text = Cur.Kind == TokenKind.Ident ? Advance().Text : "";
                n.Add(Done(nm));
                if (!Accept(",")) break;
            }
            Expect(")");
        }
        else
        {
            n.A = Type();
            n.Text = ExpectIdent();
        }
        ExpectKw("in");
        n.B = Expression();
        Expect(")");
        n.C = EmbeddedStatement();
        return Done(n);
    }

    // Switch: A = 式、List = SwitchSection。SwitchSection: List = label、List2 = 文。
    // CaseLabel: A = pattern、B = when。DefaultLabel
    private Node SwitchStatement()
    {
        var n = N(NodeKind.Switch);
        Advance();
        Expect("(");
        n.A = Expression();
        Expect(")");
        Expect("{");
        while (!IsP("}") && Cur.Kind != TokenKind.End)
        {
            var sec = N(NodeKind.SwitchSection);
            while (IsKw("case") || IsKw("default") && IsPAt(1, ":"))
            {
                if (AcceptKw("default"))
                {
                    var dl = N(NodeKind.DefaultLabel);
                    Expect(":");
                    sec.Add(Done(dl));
                    continue;
                }
                var cl = N(NodeKind.CaseLabel);
                Advance();
                cl.A = Pattern();
                if (IsContextual("when"))
                {
                    Advance();
                    cl.B = Expression();
                }
                Expect(":");
                sec.Add(Done(cl));
            }
            if (sec.Count == 0)
            {
                Error("CS1513", "case or default expected");
                Advance();
                continue;
            }
            while (!IsKw("case") && !(IsKw("default") && IsPAt(1, ":")) && !IsP("}") && Cur.Kind != TokenKind.End)
            {
                int before = _i;
                sec.Add2(Statement());
                if (_i == before) Advance();
            }
            n.Add(Done(sec));
        }
        Expect("}");
        return Done(n);
    }

    // ---- 式 ----

    public Node Expression()
    {
        return AssignmentExpression();
    }

    private static bool IsAssignOp(string s)
    {
        switch (s)
        {
            case "=":
            case "+=":
            case "-=":
            case "*=":
            case "/=":
            case "%=":
            case "&=":
            case "|=":
            case "^=":
            case "<<=":
            case "??=":
                return true;
        }
        return false;
    }

    // Assign: Text = 演算子、A = 左辺、B = 右辺
    private Node AssignmentExpression()
    {
        if (IsLambdaStart()) return Lambda();
        var left = ConditionalExpression();
        if (Cur.Kind == TokenKind.Punct)
        {
            string op = Cur.Text;
            // `>>=` は `>` と `>=` の隣接
            if (op == ">" && IsPAt(1, ">=") && !PeekTok(1).SpaceBefore)
            {
                var a = NAt(NodeKind.Assign, left.Pos);
                Advance();
                Advance();
                a.Text = ">>=";
                a.A = left;
                a.B = AssignmentExpression();
                return Done(a);
            }
            if (IsAssignOp(op))
            {
                var a = NAt(NodeKind.Assign, left.Pos);
                Advance();
                a.Text = op;
                a.A = left;
                if (op == "=" && IsKw("ref")) Unsupported("ref assignment", Cur.Pos);
                a.B = AssignmentExpression();
                return Done(a);
            }
        }
        return left;
    }

    private bool IsLambdaStart()
    {
        if (IsContextual("async") && (IsIdentAt(1) || IsPAt(1, "("))) return false;
        if (IsIdent() && IsPAt(1, "=>")) return true;
        if (IsKw("static") && (IsPAt(1, "(") || IsIdentAt(1))) return true;
        if (!IsP("(")) return false;
        // 対応する ) の後が =>
        int depth = 0;
        int j = _i;
        while (j < _toks.Count)
        {
            var t = _toks[j];
            if (t.Kind == TokenKind.End) return false;
            if (t.Kind == TokenKind.Punct)
            {
                if (t.Text == "(") depth++;
                else if (t.Text == ")")
                {
                    depth--;
                    if (depth == 0)
                    {
                        var nx = _toks[j + 1];
                        return nx.Kind == TokenKind.Punct && nx.Text == "=>";
                    }
                }
                else if (t.Text == ";" || t.Text == "{" || t.Text == "}") return false;
            }
            j++;
        }
        return false;
    }

    // Lambda: List = Param、A = 式本体、B = Block
    private Node Lambda()
    {
        var l = N(NodeKind.Lambda);
        if (IsKw("static")) Advance();
        if (IsIdent())
        {
            var p = N(NodeKind.Param);
            p.Text = Advance().Text;
            l.Add(Done(p));
        }
        else
        {
            Expect("(");
            while (!IsP(")") && Cur.Kind != TokenKind.End)
            {
                var p = N(NodeKind.Param);
                if (AcceptKw("out")) p.Flags |= Mods.Out;
                if (AcceptKw("ref")) p.Flags |= Mods.Ref;
                if (IsIdent() && (IsPAt(1, ",") || IsPAt(1, ")")))
                {
                    p.Text = Advance().Text;
                }
                else
                {
                    p.A = Type();
                    p.Text = ExpectIdent();
                }
                l.Add(Done(p));
                if (!Accept(",")) break;
            }
            Expect(")");
        }
        Expect("=>");
        if (IsP("{")) l.B = Block();
        else l.A = Expression();
        return Done(l);
    }

    // Conditional: A = 条件、B = 真、C = 偽
    private Node ConditionalExpression()
    {
        var cond = NullCoalescing();
        if (IsP("?"))
        {
            var c = NAt(NodeKind.Conditional, cond.Pos);
            Advance();
            c.A = cond;
            c.B = Expression();
            Expect(":");
            c.C = Expression();
            return Done(c);
        }
        return cond;
    }

    private Node NullCoalescing()
    {
        var left = BinaryExpression(0);
        if (IsP("??"))
        {
            var b = NAt(NodeKind.Binary, left.Pos);
            Advance();
            b.Text = "??";
            b.A = left;
            b.B = NullCoalescing();
            return Done(b);
        }
        return left;
    }

    // 二項演算子の優先順位 (大きいほど強い)。0 は二項演算子でない
    private int BinaryPrecedence(out string op)
    {
        op = "";
        if (Cur.Kind == TokenKind.Ident && Cur.IsKeyword)
        {
            if (Cur.Text == "is" || Cur.Text == "as")
            {
                op = Cur.Text;
                return 7;
            }
            return 0;
        }
        if (Cur.Kind != TokenKind.Punct) return 0;
        op = Cur.Text;
        switch (op)
        {
            case "||": return 1;
            case "&&": return 2;
            case "|": return 3;
            case "^": return 4;
            case "&": return 5;
            case "==":
            case "!=":
                return 6;
            case "<":
            case "<=":
            case ">=":
                return 7;
            case ">":
                if (IsPAt(1, ">") && !PeekTok(1).SpaceBefore)
                {
                    if (IsPAt(2, "=") && !PeekTok(2).SpaceBefore) return 0;
                    op = ">>";
                    return 8;
                }
                if (IsPAt(1, ">=") && !PeekTok(1).SpaceBefore) return 0;
                return 7;
            case "<<": return 8;
            case "+":
            case "-":
                return 9;
            case "*":
            case "/":
            case "%":
                return 10;
        }
        return 0;
    }

    // Binary: Text = 演算子、A、B。Is: A = 式、B = pattern。As: A = 式、B = 型
    private Node BinaryExpression(int minPrec)
    {
        var left = UnaryExpression();
        while (true)
        {
            int prec = BinaryPrecedence(out var op);
            if (prec == 0 || prec <= minPrec) break;
            if (op == "is")
            {
                var isn = NAt(NodeKind.Is, left.Pos);
                Advance();
                isn.A = left;
                isn.B = Pattern();
                left = Done(isn);
                continue;
            }
            if (op == "as")
            {
                var asn = NAt(NodeKind.As, left.Pos);
                Advance();
                asn.A = left;
                asn.B = Type();
                left = Done(asn);
                continue;
            }
            var b = NAt(NodeKind.Binary, left.Pos);
            if (op == ">>")
            {
                Advance();
                Advance();
            }
            else Advance();
            b.Text = op;
            b.A = left;
            b.B = BinaryExpression(prec);
            left = Done(b);
        }
        return left;
    }

    // Unary: Text = 演算子、A。PreIncDec: Text、A。Cast: A = 型、B = 式
    private Node UnaryExpression()
    {
        if (Cur.Kind == TokenKind.Punct)
        {
            string op = Cur.Text;
            if (op == "+" || op == "-" || op == "!" || op == "~")
            {
                var u = N(NodeKind.Unary);
                Advance();
                u.Text = op;
                u.A = UnaryExpression();
                return Done(u);
            }
            if (op == "++" || op == "--")
            {
                var u = N(NodeKind.PreIncDec);
                Advance();
                u.Text = op;
                u.A = UnaryExpression();
                return Done(u);
            }
            if (op == "(" && IsCast())
            {
                var c = N(NodeKind.Cast);
                Advance();
                c.A = Type();
                Expect(")");
                c.B = UnaryExpression();
                return Done(c);
            }
            if (op == "^" || op == "&" || op == "*")
            {
                Unsupported("unary '" + op + "'", Cur.Pos);
                Advance();
                return UnaryExpression();
            }
        }
        if (IsContextual("await"))
        {
            Unsupported("await", Cur.Pos);
            Advance();
            return UnaryExpression();
        }
        return PostfixExpression(PrimaryExpression());
    }

    private bool IsCast()
    {
        int save = _i;
        Advance();
        var t = TryType(true);
        bool result = false;
        if (t != null && IsP(")"))
        {
            var nx = PeekTok(1);
            if (t.Kind == NodeKind.PredefType || t.Kind == NodeKind.NullableType && t.A!.Kind == NodeKind.PredefType || t.Kind == NodeKind.ArrayType)
            {
                result = true;
            }
            else if (nx.Kind == TokenKind.Ident)
            {
                result = !(nx.IsKeyword && (nx.Text == "is" || nx.Text == "as"));
            }
            else if (nx.Kind == TokenKind.IntLit || nx.Kind == TokenKind.FloatLit || nx.Kind == TokenKind.StringLit || nx.Kind == TokenKind.CharLit || nx.Kind == TokenKind.InterpString)
            {
                result = true;
            }
            else if (nx.Kind == TokenKind.Punct && (nx.Text == "(" || nx.Text == "!" || nx.Text == "~"))
            {
                result = true;
            }
        }
        _i = save;
        return result;
    }

    private Node PrimaryExpression()
    {
        var t = Cur;
        switch (t.Kind)
        {
            case TokenKind.IntLit:
                {
                    var n = N(NodeKind.IntLit);
                    n.Text = Advance().Text;
                    return Done(n);
                }
            case TokenKind.FloatLit:
                {
                    var n = N(NodeKind.FloatLit);
                    n.Text = Advance().Text;
                    return Done(n);
                }
            case TokenKind.StringLit:
                {
                    var n = N(NodeKind.StringLit);
                    n.Text = Advance().Text;
                    return Done(n);
                }
            case TokenKind.CharLit:
                {
                    var n = N(NodeKind.CharLit);
                    n.Value = Advance().Value;
                    return Done(n);
                }
            case TokenKind.InterpString:
                return InterpolatedString(Advance());
            case TokenKind.Punct:
                if (t.Text == "(") return ParenOrTuple();
                if (t.Text == "[") return CollectionExpression();
                break;
            case TokenKind.Ident:
                if (t.IsKeyword) return KeywordPrimary();
                {
                    // Name: Text = 名前、List = 型引数
                    var n = N(NodeKind.Name);
                    n.Text = Advance().Text;
                    TypeArgumentsInExpression(n);
                    return Done(n);
                }
        }
        Error("CS1525", "Invalid expression term '" + t.Text + "'");
        var err = N(NodeKind.NullLit);
        if (Cur.Kind != TokenKind.End && !IsP(";") && !IsP(")") && !IsP("}")) Advance();
        return Done(err);
    }

    // 式の中の `<`: 型引数として読めて、その後が C# の規則の token なら型引数
    private void TypeArgumentsInExpression(Node n)
    {
        if (!IsP("<")) return;
        int save = _i;
        Advance();
        var args = new List<Node>();
        while (true)
        {
            var a = TryType(true);
            if (a == null)
            {
                _i = save;
                return;
            }
            args.Add(a);
            if (!Accept(",")) break;
        }
        if (!IsP(">"))
        {
            _i = save;
            return;
        }
        var nx = PeekTok(1);
        bool ok = false;
        if (nx.Kind == TokenKind.Punct)
        {
            switch (nx.Text)
            {
                case "(":
                case ")":
                case "]":
                case "}":
                case ":":
                case ";":
                case ",":
                case ".":
                case "?":
                case "==":
                case "!=":
                case "|":
                case "^":
                case "&&":
                case "||":
                case "&":
                case "[":
                    ok = true;
                    break;
            }
        }
        else if (nx.Kind == TokenKind.End) ok = true;
        if (!ok)
        {
            _i = save;
            return;
        }
        Advance();
        n.List = args;
    }

    private Node KeywordPrimary()
    {
        string k = Cur.Text;
        switch (k)
        {
            case "true":
            case "false":
                {
                    var n = N(NodeKind.BoolLit);
                    n.Value = k == "true" ? 1 : 0;
                    Advance();
                    return Done(n);
                }
            case "null":
                {
                    var n = N(NodeKind.NullLit);
                    Advance();
                    return Done(n);
                }
            case "this":
                {
                    var n = N(NodeKind.This);
                    Advance();
                    return Done(n);
                }
            case "base":
                {
                    Unsupported("base access", Cur.Pos);
                    var n = N(NodeKind.This);
                    Advance();
                    return Done(n);
                }
            case "default":
                {
                    // DefaultLit: A = 型 (default(T))、null なら target-typed
                    var n = N(NodeKind.DefaultLit);
                    Advance();
                    if (Accept("("))
                    {
                        n.A = Type();
                        Expect(")");
                    }
                    return Done(n);
                }
            case "new": return NewExpression();
            case "typeof":
                {
                    var n = N(NodeKind.Typeof);
                    Unsupported("typeof", Cur.Pos);
                    Advance();
                    Expect("(");
                    n.A = Type();
                    Expect(")");
                    return Done(n);
                }
            case "checked":
            case "unchecked":
                {
                    // Checked: Value = 1 なら checked、A = 式
                    var n = N(NodeKind.Checked);
                    n.Value = k == "checked" ? 1 : 0;
                    Advance();
                    Expect("(");
                    n.A = Expression();
                    Expect(")");
                    return Done(n);
                }
            case "sizeof":
            case "stackalloc":
            case "delegate":
            case "throw":
            case "ref":
                {
                    Unsupported("'" + k + "' expression", Cur.Pos);
                    var n = N(NodeKind.NullLit);
                    Advance();
                    if (IsP("(")) SkipBalanced("(", ")");
                    if (IsP("{")) SkipBalanced("{", "}");
                    if (k == "throw") UnaryExpression();
                    return Done(n);
                }
        }
        if (IsPredefTypeName(k))
        {
            // int.Parse など: Name として扱う
            var n = N(NodeKind.Name);
            n.Text = Advance().Text;
            n.Value = 1;
            return Done(n);
        }
        Error("CS1525", "Invalid expression term '" + k + "'");
        var err = N(NodeKind.NullLit);
        Advance();
        return Done(err);
    }

    // New: A = 型 (null なら target-typed)、List = 引数 (Arg)、B = 初期化子。Value = 1 なら引数の括弧があった
    // NewArray: A = 要素型 (null なら new[])、B = 長さ、C = ArrayInit
    private Node NewExpression()
    {
        var n = N(NodeKind.New);
        Advance();
        if (IsP("[") && IsPAt(1, "]"))
        {
            var na = N(NodeKind.NewArray);
            na.Pos = n.Pos;
            Advance();
            Advance();
            na.C = ArrayInitializer();
            return Done(na);
        }
        if (IsP("("))
        {
            Arguments(n);
            n.Value = 1;
            if (IsP("{")) n.B = ObjectOrCollectionInitializer();
            return Done(n);
        }
        if (IsP("{"))
        {
            Unsupported("anonymous type", Cur.Pos);
            SkipBalanced("{", "}");
            return Done(n);
        }
        // 配列: new T[n] / new T[] { ... }
        var type = TryType(true);
        if (type == null)
        {
            Error("CS1031", "Type expected");
            return Done(n);
        }
        if (IsP("[") && !IsPAt(1, "]"))
        {
            var na = NAt(NodeKind.NewArray, n.Pos);
            na.A = type;
            Advance();
            na.B = Expression();
            if (IsP(","))
            {
                Unsupported("multi-dimensional array", Cur.Pos);
                while (!IsP("]") && Cur.Kind != TokenKind.End) Advance();
            }
            Expect("]");
            while (IsP("[") && IsPAt(1, "]"))
            {
                var at = NAt(NodeKind.ArrayType, na.A.Pos);
                at.A = na.A;
                na.A = at;
                Advance();
                Advance();
            }
            if (IsP("{")) na.C = ArrayInitializer();
            return Done(na);
        }
        if (type.Kind == NodeKind.ArrayType)
        {
            var na = NAt(NodeKind.NewArray, n.Pos);
            na.A = type.A;
            na.C = ArrayInitializer();
            return Done(na);
        }
        n.A = type;
        if (IsP("("))
        {
            Arguments(n);
            n.Value = 1;
        }
        if (IsP("{")) n.B = ObjectOrCollectionInitializer();
        else if (n.Value == 0) Error("CS1526", "A new expression requires an argument list or an initializer");
        return Done(n);
    }

    // ArrayInit: List = 要素
    private Node ArrayInitializer()
    {
        var init = N(NodeKind.ArrayInit);
        Expect("{");
        while (!IsP("}") && Cur.Kind != TokenKind.End)
        {
            init.Add(VariableInitializer());
            if (!Accept(",")) break;
        }
        Expect("}");
        return Done(init);
    }

    // ObjectInit: List = MemberInit (Text = 名前、A = 値) か IndexInit (A = key、B = 値)
    // CollectionInit: List = 要素の式か、{k, v} の ArrayInit
    private Node ObjectOrCollectionInitializer()
    {
        int pos = Cur.Pos;
        Expect("{");
        bool isObject = IsP("}") || IsIdent() && IsPAt(1, "=") || IsP("[");
        var init = NAt(isObject ? NodeKind.ObjectInit : NodeKind.CollectionInit, pos);
        while (!IsP("}") && Cur.Kind != TokenKind.End)
        {
            if (isObject)
            {
                if (IsP("["))
                {
                    var ii = N(NodeKind.IndexInit);
                    Advance();
                    ii.A = Expression();
                    Expect("]");
                    Expect("=");
                    ii.B = IsP("{") ? ObjectOrCollectionInitializer() : Expression();
                    init.Add(Done(ii));
                }
                else
                {
                    var mi = N(NodeKind.MemberInit);
                    mi.Text = ExpectIdent();
                    Expect("=");
                    mi.A = IsP("{") ? ObjectOrCollectionInitializer() : Expression();
                    init.Add(Done(mi));
                }
            }
            else
            {
                if (IsP("{"))
                {
                    var pair = N(NodeKind.ArrayInit);
                    Advance();
                    while (!IsP("}") && Cur.Kind != TokenKind.End)
                    {
                        pair.Add(Expression());
                        if (!Accept(",")) break;
                    }
                    Expect("}");
                    init.Add(Done(pair));
                }
                else init.Add(Expression());
            }
            if (!Accept(",")) break;
        }
        Expect("}");
        return Done(init);
    }

    // CollectionExpr: List = 要素
    private Node CollectionExpression()
    {
        var n = N(NodeKind.CollectionExpr);
        Advance();
        while (!IsP("]") && Cur.Kind != TokenKind.End)
        {
            if (IsP(".."))
            {
                Unsupported("spread element", Cur.Pos);
                Advance();
            }
            n.Add(Expression());
            if (!Accept(",")) break;
        }
        Expect("]");
        return Done(n);
    }

    // Paren: A。Tuple: List = 要素 (Arg。Text = 名前)
    private Node ParenOrTuple()
    {
        var pos = Cur.Pos;
        Advance();
        var first = Expression();
        if (IsP(","))
        {
            var t = NAt(NodeKind.Tuple, pos);
            t.Add(first);
            while (Accept(",")) t.Add(Expression());
            Expect(")");
            return Done(t);
        }
        Expect(")");
        var p = NAt(NodeKind.Paren, pos);
        p.A = first;
        return Done(p);
    }

    // Arg: A = 式、Flags = out / ref / in。out var x は A = DeclarationExpr として PatDecl (A = 型、Text = 名前)
    private void Arguments(Node into)
    {
        Expect("(");
        while (!IsP(")") && Cur.Kind != TokenKind.End)
        {
            var a = N(NodeKind.Arg);
            if (IsIdent() && IsPAt(1, ":") && !IsPAt(2, ":"))
            {
                Unsupported("named argument", Cur.Pos);
                Advance();
                Advance();
            }
            if (AcceptKw("out"))
            {
                a.Flags = Mods.Out;
                if (IsIdent() && Cur.Text == "_" && (IsPAt(1, ",") || IsPAt(1, ")")))
                {
                    var d = N(NodeKind.PatDiscard);
                    Advance();
                    a.A = Done(d);
                }
                else if (IsOutDeclaration())
                {
                    var d = N(NodeKind.PatDecl);
                    d.A = Type();
                    d.Text = ExpectIdent();
                    a.A = Done(d);
                }
                else a.A = Expression();
            }
            else
            {
                if (AcceptKw("ref")) a.Flags = Mods.Ref;
                else if (AcceptKw("in")) a.Flags = Mods.In;
                a.A = Expression();
            }
            into.Add(Done(a));
            if (!Accept(",")) break;
        }
        Expect(")");
    }

    private bool IsOutDeclaration()
    {
        int save = _i;
        var t = TryType(true);
        bool result = t != null && IsIdent() && (IsPAt(1, ",") || IsPAt(1, ")"));
        _i = save;
        return result;
    }

    // Member: A = 式、Text = 名前、List = 型引数、Value = 1 なら ?.
    // Call: A = callee、List = Arg。Index: A = 式、List = 添字、Value = 1 なら ?[
    // PostIncDec: Text、A
    private Node PostfixExpression(Node e)
    {
        while (true)
        {
            if (IsP("."))
            {
                Advance();
                var m = NAt(NodeKind.Member, e.Pos);
                m.A = e;
                m.Text = Cur.Kind == TokenKind.Ident ? Advance().Text : "";
                if (m.Text == "") Error("CS1001", "Identifier expected");
                TypeArgumentsInExpression(m);
                e = Done(m);
            }
            else if (IsP("?") && IsPAt(1, "[") && !PeekTok(1).SpaceBefore)
            {
                var ix = NAt(NodeKind.Index, e.Pos);
                ix.Value = 1;
                ix.A = e;
                Advance();
                Advance();
                while (true)
                {
                    ix.Add(Expression());
                    if (!Accept(",")) break;
                }
                Expect("]");
                e = Done(ix);
            }
            else if (IsP("?."))
            {
                Advance();
                if (IsP("["))
                {
                    var ix = NAt(NodeKind.Index, e.Pos);
                    ix.Value = 1;
                    ix.A = e;
                    Advance();
                    while (true)
                    {
                        ix.Add(Expression());
                        if (!Accept(",")) break;
                    }
                    Expect("]");
                    e = Done(ix);
                    continue;
                }
                var m = NAt(NodeKind.Member, e.Pos);
                m.A = e;
                m.Value = 1;
                m.Text = ExpectIdent();
                TypeArgumentsInExpression(m);
                e = Done(m);
            }
            else if (IsP("("))
            {
                var c = NAt(NodeKind.Call, e.Pos);
                c.A = e;
                Arguments(c);
                e = Done(c);
            }
            else if (IsP("["))
            {
                var ix = NAt(NodeKind.Index, e.Pos);
                ix.A = e;
                Advance();
                while (true)
                {
                    if (IsP("^"))
                    {
                        Unsupported("index from end", Cur.Pos);
                        Advance();
                    }
                    ix.Add(Expression());
                    if (IsP(".."))
                    {
                        Unsupported("range", Cur.Pos);
                        Advance();
                        if (!IsP("]")) Expression();
                    }
                    if (!Accept(",")) break;
                }
                Expect("]");
                e = Done(ix);
            }
            else if (IsP("++") || IsP("--"))
            {
                var p = NAt(NodeKind.PostIncDec, e.Pos);
                p.Text = Advance().Text;
                p.A = e;
                e = Done(p);
            }
            else if (IsP("!") && !IsPAt(1, "=") && IsPostfixBangContext())
            {
                // null 免除 `x!` は型にだけ効く
                Advance();
            }
            else if (IsKw("switch"))
            {
                e = SwitchExpression(e);
            }
            else if (IsKw("with"))
            {
                break;
            }
            else if (IsContextual("with") && IsPAt(1, "{"))
            {
                // With: Text = "with" の Call として A = 式、B = ObjectInit
                var w = NAt(NodeKind.Call, e.Pos);
                w.Text = "with";
                w.A = e;
                Advance();
                w.B = ObjectOrCollectionInitializer();
                e = Done(w);
            }
            else break;
        }
        return e;
    }

    private bool IsPostfixBangContext()
    {
        var nx = PeekTok(1);
        if (nx.Kind == TokenKind.Punct)
        {
            switch (nx.Text)
            {
                case ".":
                case ")":
                case ";":
                case ",":
                case "]":
                case "[":
                case "?.":
                case "}":
                case "(":
                case "==":
                case "??":
                case ":":
                    return true;
            }
            return false;
        }
        return nx.Kind == TokenKind.End;
    }

    // SwitchExpr: A = 式、List = SwitchArm (A = pattern、B = when、C = 値)
    private Node SwitchExpression(Node governing)
    {
        var s = NAt(NodeKind.SwitchExpr, governing.Pos);
        s.A = governing;
        Advance();
        Expect("{");
        while (!IsP("}") && Cur.Kind != TokenKind.End)
        {
            var arm = N(NodeKind.SwitchArm);
            arm.A = Pattern();
            if (IsContextual("when"))
            {
                Advance();
                arm.B = Expression();
            }
            Expect("=>");
            arm.C = Expression();
            s.Add(Done(arm));
            if (!Accept(",")) break;
        }
        Expect("}");
        return Done(s);
    }

    // InterpString: List = StringLit (Text = 値) と InterpHole (A = 式、Text = 書式、B = 揃え)
    private Node InterpolatedString(Token t)
    {
        var n = NAt(NodeKind.InterpString, t.Pos);
        var b = _file.Bytes;
        int i = t.ContentStart;
        int end = t.ContentEnd;
        var text = new List<string>();
        int run = i;
        while (i < end)
        {
            int c = b[i];
            if (c == '{' && i + 1 < end && b[i + 1] == '{')
            {
                text.Add(_file.Slice(run, i + 1));
                i += 2;
                run = i;
                continue;
            }
            if (c == '}' && i + 1 < end && b[i + 1] == '}')
            {
                text.Add(_file.Slice(run, i + 1));
                i += 2;
                run = i;
                continue;
            }
            if (!t.Verbatim && c == '\\')
            {
                text.Add(_file.Slice(run, i));
                var sub = new Lexer(_file, _diags, i, end);
                text.Add(sub.EscapeAt(i));
                i = sub.Position;
                run = i;
                continue;
            }
            if (t.Verbatim && c == '"' && i + 1 < end && b[i + 1] == '"')
            {
                text.Add(_file.Slice(run, i + 1));
                i += 2;
                run = i;
                continue;
            }
            if (c == '{')
            {
                text.Add(_file.Slice(run, i));
                FlushText(n, text, t.Pos);
                int holeStart = i + 1;
                int depth = 1;
                int j = holeStart;
                int formatAt = -1;
                int alignAt = -1;
                int parens = 0;
                while (j < end)
                {
                    int d = b[j];
                    if (d == '"' || d == '\'')
                    {
                        j++;
                        while (j < end && b[j] != d)
                        {
                            if (b[j] == '\\') j++;
                            j++;
                        }
                        j++;
                        continue;
                    }
                    if (d == '(' || d == '[') parens++;
                    else if (d == ')' || d == ']') parens--;
                    else if (d == '{') depth++;
                    else if (d == '}')
                    {
                        depth--;
                        if (depth == 0) break;
                    }
                    else if (d == ':' && depth == 1 && parens == 0 && formatAt < 0) formatAt = j;
                    else if (d == ',' && depth == 1 && parens == 0 && formatAt < 0 && alignAt < 0) alignAt = j;
                    j++;
                }
                int exprEnd = alignAt >= 0 ? alignAt : formatAt >= 0 ? formatAt : j;
                var hole = NAt(NodeKind.InterpHole, holeStart);
                var lexer = new Lexer(_file, _diags, holeStart, exprEnd);
                var sub = new Parser(_file, _diags, lexer.Tokenize());
                hole.A = sub.Expression();
                if (sub.Cur.Kind != TokenKind.End) _diags.Error("CS1003", "Syntax error in interpolation hole", _file, sub.Cur.Pos);
                if (alignAt >= 0)
                {
                    int alignEnd = formatAt >= 0 ? formatAt : j;
                    var alexer = new Lexer(_file, _diags, alignAt + 1, alignEnd);
                    var asub = new Parser(_file, _diags, alexer.Tokenize());
                    hole.B = asub.Expression();
                }
                if (formatAt >= 0) hole.Text = _file.Slice(formatAt + 1, j);
                n.Add(Done(hole));
                i = j + 1;
                run = i;
                continue;
            }
            i++;
        }
        text.Add(_file.Slice(run, end));
        FlushText(n, text, t.Pos);
        n.End = t.End;
        return n;
    }

    private void FlushText(Node n, List<string> text, int pos)
    {
        string s = string.Join("", text);
        text.Clear();
        if (s == "") return;
        var lit = NAt(NodeKind.StringLit, pos);
        lit.Text = s;
        n.Add(lit);
    }

    // ---- pattern ----

    // PatOr / PatAnd: A、B。PatNot: A。PatRel: Text = 演算子、A = 式。
    // PatConst: A = 式。PatType: A = 型。PatDecl: A = 型、Text = 名前。PatVar: Text。PatDiscard。
    // PatProp: A = 型 (null 可)、List = PropSub (Text = 名前、A = pattern)、Text = 名前 (指定時)
    public Node Pattern() => OrPattern();

    private Node OrPattern()
    {
        var left = AndPattern();
        while (IsContextual("or"))
        {
            var o = NAt(NodeKind.PatOr, left.Pos);
            Advance();
            o.A = left;
            o.B = AndPattern();
            left = Done(o);
        }
        return left;
    }

    private Node AndPattern()
    {
        var left = NotPattern();
        while (IsContextual("and"))
        {
            var a = NAt(NodeKind.PatAnd, left.Pos);
            Advance();
            a.A = left;
            a.B = NotPattern();
            left = Done(a);
        }
        return left;
    }

    private Node NotPattern()
    {
        if (IsContextual("not"))
        {
            var n = N(NodeKind.PatNot);
            Advance();
            n.A = NotPattern();
            return Done(n);
        }
        return PrimaryPattern();
    }

    private Node PrimaryPattern()
    {
        if (IsP("<") || IsP("<=") || IsP(">") || IsP(">="))
        {
            var r = N(NodeKind.PatRel);
            r.Text = Advance().Text;
            r.A = BinaryExpression(8);
            return Done(r);
        }
        if (IsP("("))
        {
            // 括弧の pattern か tuple の定数
            int save = _i;
            Advance();
            var inner = Pattern();
            if (Accept(")")) return inner;
            _i = save;
            Unsupported("positional pattern", Cur.Pos);
            var c = N(NodeKind.PatConst);
            c.A = UnaryExpression();
            return Done(c);
        }
        if (IsP("["))
        {
            Unsupported("list pattern", Cur.Pos);
            SkipBalanced("[", "]");
            return Done(N(NodeKind.PatDiscard));
        }
        if (IsP("{")) return PropertyPattern(null);
        if (IsIdent() && Cur.Text == "_" )
        {
            var d = N(NodeKind.PatDiscard);
            Advance();
            return Done(d);
        }
        if (IsContextual("var") && IsIdentAt(1))
        {
            var v = N(NodeKind.PatVar);
            Advance();
            v.Text = Advance().Text;
            return Done(v);
        }
        // 型か定数の式か
        if (Cur.Kind == TokenKind.Ident && (!Cur.IsKeyword || IsPredefTypeName(Cur.Text)))
        {
            int save = _i;
            var type = TryType(true);
            if (type != null)
            {
                if (IsIdent() && !IsContextual("and") && !IsContextual("or") && !IsContextual("when"))
                {
                    var d = NAt(NodeKind.PatDecl, type.Pos);
                    d.A = type;
                    d.Text = Advance().Text;
                    return Done(d);
                }
                if (IsP("{")) return PropertyPattern(type);
                if (IsTypePatternEnd() && LooksLikeType(type))
                {
                    var tp = NAt(NodeKind.PatType, type.Pos);
                    tp.A = type;
                    return Done(tp);
                }
            }
            _i = save;
        }
        var cp = N(NodeKind.PatConst);
        cp.A = BinaryExpression(7);
        return Done(cp);
    }

    // 型 pattern として読むかの判定: 型の名前の形 (定数の Member access と同じ形は Binder が決める)
    private bool LooksLikeType(Node t) => true;

    private bool IsTypePatternEnd()
    {
        if (Cur.Kind == TokenKind.End) return true;
        if (Cur.Kind == TokenKind.Punct)
        {
            switch (Cur.Text)
            {
                case ")":
                case ":":
                case "=>":
                case ",":
                case "}":
                case "&&":
                case "||":
                case "?":
                case ";":
                    return true;
            }
            return false;
        }
        if (IsContextual("and") || IsContextual("or") || IsContextual("when")) return true;
        return false;
    }

    private Node PropertyPattern(Node? type)
    {
        var p = N(NodeKind.PatProp);
        if (type != null) p.Pos = type.Pos;
        p.A = type;
        Expect("{");
        while (!IsP("}") && Cur.Kind != TokenKind.End)
        {
            var sub = N(NodeKind.PropSub);
            sub.Text = ExpectIdent();
            while (IsP(".") && IsIdentAt(1))
            {
                Advance();
                sub.Text = sub.Text + "." + Advance().Text;
            }
            Expect(":");
            sub.A = Pattern();
            p.Add(Done(sub));
            if (!Accept(",")) break;
        }
        Expect("}");
        if (IsIdent() && !IsContextual("and") && !IsContextual("or") && !IsContextual("when")) p.Text = Advance().Text;
        return Done(p);
    }
}
