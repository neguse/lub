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

    // `<...>(` (generic な method の型引数)
    private bool IsGenericMethodStart()
    {
        int save = _i;
        SkipBalanced("<", ">");
        bool r = IsP("(");
        _i = save;
        return r;
    }

    private bool IsPredefTypeKeywordAt(int n)
    {
        var t = PeekTok(n);
        return t.Kind == TokenKind.Ident && t.IsKeyword && IsPredefTypeName(t.Text);
    }

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
        // --ref は host の宣言だけを使う (本文は束縛も出力もしないので、サブセットの外の構文も通す)
        if (_file.IsRef) return;
        _diags.Error("TCS1001", "unsupported syntax: " + what, _file, pos);
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
            bool isGlobal = false;
            if (IsContextual("global"))
            {
                Advance();
                Unsupported("global using", pos);
                isGlobal = true;
            }
            Advance();
            // using static T; (Value = 1): T の static な member と入れ子の型を名前で引ける
            bool isStatic = AcceptKw("static");
            Node? alias = null;
            if (IsIdent() && IsPAt(1, "="))
            {
                // using X = A.B;  (D = 別名)
                alias = N(NodeKind.Name);
                alias.Text = Advance().Text;
                Advance();
            }
            var u = NAt(NodeKind.Using, pos);
            u.D = alias;
            u.Text = QualifiedName();
            if (isStatic) u.Value = 1;
            // global using (Flags = 1) は全てのファイルに効く (--ref で .NET の暗黙の using を与える)
            if (isGlobal) u.Flags = 1;
            if (alias != null && IsP("<"))
            {
                Unsupported("generic using alias", Cur.Pos);
                SkipBalanced("<", ">");
            }
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
            // 属性の対象 (`[return: X]`、`[field: X]`。return は予約語)
            if (Cur.Kind == TokenKind.Ident && IsPAt(1, ":"))
            {
                Advance();
                Advance();
            }
            while (true)
            {
                var a = N(NodeKind.Attribute);
                // global:: は名前の照合に影響しない
                if (IsContextual("global") && IsPAt(1, "::"))
                {
                    Advance();
                    Advance();
                }
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
                        case "ref":
                            // ref struct (寿命を検査する struct) の宣言
                            if (IsKwAt(1, "struct") || PeekTok(1).Text == "partial" && IsKwAt(2, "struct"))
                            {
                                Unsupported("RefStructDeclaration", Cur.Pos);
                                Advance();
                                continue;
                            }
                            break;
                    }
                }
                else if (t == "partial" && (IsKwAt(1, "class") || IsKwAt(1, "struct") || IsKwAt(1, "interface") || IsIdentAt(1) && PeekTok(1).Text == "record" || IsKwAt(1, "void")
                    // partial method (`partial bool M();`、`partial Task<int> M();`)
                    || IsPredefTypeKeywordAt(1) || IsIdentAt(1) && PeekTok(1).Text != "record" && (IsIdentAt(2) || IsPAt(2, "<"))))
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
            Unsupported("DelegateDeclaration", Cur.Pos);
            Advance();
            d.Value = DeclKinds.Class;
            d.Text = "";
            // 戻りの型、名前、parameter (out の parameter の診断と、型の検査の診断のために名前を残す)
            int save = _i;
            var ret = TryType(true);
            if (ret != null && IsIdent())
            {
                d.Text = ExpectIdent();
                d.Flags |= Mods.Delegate;
                // C = 戻りの型、List2 = 型引数の名前 (宣言の外では delegate の型として扱う)
                d.C = ret;
                if (Accept("<"))
                {
                    while (true)
                    {
                        AcceptKw("in");
                        AcceptKw("out");
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
                    d.B = plist;
                }
            }
            else _i = save;
            while (!IsP(";") && Cur.Kind != TokenKind.End) Advance();
            Accept(";");
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
                // variance (in / out) は interface の型引数だけに書ける。Lua は型を消すので出力には影響しない
                int vpos = Cur.Pos;
                if (AcceptKw("in") || AcceptKw("out"))
                {
                    if (d.Value != DeclKinds.Interface) _diags.Error("CS1960", "Invalid variance modifier. Only interface and delegate type parameters can be specified as variant", _file, vpos);
                }
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
                // record の基底の引数は継承 (ClassInheritance) の診断に含める
                if (IsP("(")) SkipBalanced("(", ")");
                d.Add3(bt);
                if (!Accept(",")) break;
            }
        }
        Constraints(d.List2);
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

    // where T : A, B, class, struct, new()。制約の型は型引数の Name の List に、
    // class / struct / new() は Value の bit (1 / 2 / 4) に入れる
    private void Constraints(List<Node>? typeParams)
    {
        while (IsContextual("where"))
        {
            Advance();
            string name = ExpectIdent();
            Expect(":");
            Node? target = null;
            if (typeParams != null)
            {
                foreach (var tp in typeParams)
                {
                    if (tp.Text == name) target = tp;
                }
            }
            if (target == null) Error("CS0699", "'" + name + "' does not define type parameter");
            while (true)
            {
                if (IsKw("class"))
                {
                    Advance();
                    Accept("?");
                    if (target != null) target.Value |= 1;
                }
                else if (IsKw("struct"))
                {
                    Advance();
                    if (target != null) target.Value |= 2;
                }
                else if (IsKw("new") && IsPAt(1, "("))
                {
                    Advance();
                    Advance();
                    Expect(")");
                    if (target != null) target.Value |= 4;
                }
                else if (IsContextual("notnull") || IsContextual("unmanaged") || IsKw("default"))
                {
                    Advance();
                }
                else
                {
                    var ct = Type();
                    if (target != null) target.Add(ct);
                }
                if (!Accept(",")) break;
            }
        }
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

    private static string ExpressionSyntaxName(string keyword)
    {
        switch (keyword)
        {
            case "sizeof": return "SizeOfExpression";
            case "stackalloc": return "StackAllocArrayCreationExpression";
            case "delegate": return "AnonymousMethodExpression";
            case "throw": return "ThrowExpression";
            case "ref": return "RefExpression";
        }
        return keyword;
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
            if (IsP(":"))
            {
                // 初期化子の位置は `:` (Roslyn の ConstructorInitializer と同じ)
                var init = N(NodeKind.Call);
                Advance();
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
            Unsupported("DestructorDeclaration", Cur.Pos);
            Advance();
            ExpectIdent();
            var dummy = NAt(NodeKind.Method, pos);
            ParamList(dummy);
            MethodBody(dummy);
            return null;
        }
        if ((mods & (Mods.Implicit | Mods.Explicit)) != 0 && IsKw("operator"))
        {
            Unsupported("ConversionOperatorDeclaration", pos);
            Advance();
            Type();
            var dummy = NAt(NodeKind.OperatorDecl, pos);
            ParamList(dummy);
            MethodBody(dummy);
            return null;
        }
        if (mods != 0 && (mods & Mods.Event) != 0)
        {
            Unsupported("EventDeclaration", pos);
            // field の形 (`event D E;`、`event D E = x;`) か、add / remove の block を持つ形
            while (!IsP(";") && !IsP("}") && Cur.Kind != TokenKind.End)
            {
                if (IsP("{"))
                {
                    SkipBalanced("{", "}");
                    return null;
                }
                if (IsP("(")) SkipBalanced("(", ")");
                else Advance();
            }
            Accept(";");
            return null;
        }
        // ref の戻り値 (サブセットの外。型は ref を外して読む)
        if (IsKw("ref"))
        {
            Unsupported("RefType", Cur.Pos);
            Advance();
            AcceptKw("readonly");
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
            bool structOwner = owner.Value == DeclKinds.Struct || owner.Value == DeclKinds.RecordStruct;
            Unsupported(structOwner ? "StructMember(IndexerDeclaration)" : "member: IndexerDeclaration", Cur.Pos);
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
        // interface の名前は修飾と型引数を持ちうる (`I<U>.F`、`A.I.M`、`IDictionary<int, T>.this[...]`)
        int ifaceSave = _i;
        if (IsP("<"))
        {
            SkipBalanced("<", ">");
            if (!IsP(".")) _i = ifaceSave;
        }
        if (IsP(".") && (IsIdentAt(1) || IsKwAt(1, "this")))
        {
            Unsupported("ExplicitInterfaceImplementation", namePos);
            string iface = name;
            while (IsP(".") && (IsIdentAt(1) || IsKwAt(1, "this")))
            {
                Advance();
                if (IsKw("this"))
                {
                    // 明示的な実装の indexer: 読み飛ばす
                    Advance();
                    if (IsP("[")) SkipBalanced("[", "]");
                    if (IsP("{")) SkipBalanced("{", "}");
                    else
                    {
                        while (!IsP(";") && Cur.Kind != TokenKind.End) Advance();
                        Accept(";");
                    }
                    return null;
                }
                name = ExpectIdent();
                if (IsP("<") && !IsGenericMethodStart())
                {
                    SkipBalanced("<", ">");
                }
                if (IsP(".")) iface = iface + "." + name;
            }
            // 同じ名前の普通の member と重ならない名前にする (呼び出しには使えない。宣言はサブセットの外)
            name = iface + "." + name;
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
            Constraints(m.List2);
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
                // Value = 1: 式本体の property (accessor の並びを書かない形)
                p.Value = 1;
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
            // 式の本文の分解の代入 (`=> (X, Y) = (x, y);`) は文の本文と同じに読む
            if (IsP("(") && IsDeconstructionStart())
            {
                var block = N(NodeKind.Block);
                var da = DeconstructTargets();
                Expect("=");
                da.A = Expression();
                block.Add(Done(da));
                Expect(";");
                m.B = Done(block);
                return;
            }
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
            var pattrs = Attributes();
            var p = N(NodeKind.Param);
            // 属性は D に置く (準拠検査が caller info の属性を見る)
            if (pattrs != null)
            {
                var holder = NAt(NodeKind.Attribute, p.Pos);
                holder.List = pattrs;
                p.D = holder;
            }
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
            // global::X は global namespace から引く (一番内側の TypeName の Value = 1)
            bool rooted = false;
            if (IsContextual("global") && IsPAt(1, "::") && IsIdentAt(2))
            {
                Advance();
                Advance();
                rooted = true;
            }
            Node? alias = null;
            if (!rooted && IsPAt(1, "::") && IsIdentAt(2))
            {
                // using の別名で修飾した名前 (`S::Socket`、サブセットの外)。`S.Socket` と同じに読む
                if (!speculative) Unsupported("AliasQualifiedName", Cur.Pos);
                alias = N(NodeKind.TypeName);
                alias.Text = Advance().Text;
                Done(alias);
                Advance();
            }
            t = SimpleTypeName(speculative);
            if (t == null)
            {
                _i = save;
                return null;
            }
            if (rooted) t.Value = 1;
            if (alias != null) t.A = alias;
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
                // 多次元配列の型 (Value = 2)。型を解決するときに TCS1001 にする (型引数の中は先読みで読むので、ここでは報告しない)
                var a = NAt(NodeKind.ArrayType, t.Pos);
                SkipBalanced("[", "]");
                a.A = t;
                a.Value = 2;
                t = Done(a);
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
        // 型引数を書かない generic の型 (`typeof(X<>)`、`typeof(D<,>)`)。typeof の中だけに書け、typeof はサブセットの外
        if (IsP("<") && (IsPAt(1, ">") || IsPAt(1, ",")))
        {
            Advance();
            while (IsP(",")) Advance();
            Expect(">");
            return Done(n);
        }
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
        // label の付いた文 (`done: ...`、goto の飛び先): label を報告して文を読む
        if (Cur.Kind == TokenKind.Ident && !Cur.IsKeyword && IsPAt(1, ":"))
        {
            Unsupported("LabeledStatement", pos);
            Advance();
            Advance();
            if (IsP("}")) return Done(N(NodeKind.Empty));
            return Statement();
        }
        // scoped の local (ref struct の寿命の注釈)
        if (IsContextual("scoped") && (IsIdentAt(1) || IsKwAt(1, "ref")))
        {
            Unsupported("ScopedType", pos);
            Advance();
            return Statement();
        }
        if (IsContextual("yield") && (IsKwAt(1, "return") || IsKwAt(1, "break"))) return UnsupportedStatement("yield");
        if (IsContextual("await")) return UnsupportedStatement("await");
        // ref local (サブセットの外。型は ref を外して読む)
        if (IsKw("ref") && !IsPAt(1, "("))
        {
            int save = _i;
            int refPos = Cur.Pos;
            Advance();
            AcceptKw("readonly");
            if (IsLocalDeclarationStart())
            {
                Unsupported("RefType", refPos);
                var decl = LocalDeclaration(false);
                Expect(";");
                return decl;
            }
            _i = save;
        }
        // static / async の local function (修飾子を読み飛ばして、宣言と同じく報告する)
        if ((IsKw("static") || IsContextual("async")) && !IsPAt(1, "."))
        {
            int save = _i;
            while (IsKw("static") || IsContextual("async")) Advance();
            if (IsLocalDeclarationStart()) return LocalDeclaration(false);
            _i = save;
        }
        // local function / 宣言 / 式
        if (IsContextual("var") && IsPAt(1, "("))
        {
            // var (a, (b, c)) = ...: 全ての名前を var の宣言にした分解の代入
            var varTok = Cur;
            Advance();
            var da = VarDeconstructTargets(varTok);
            Expect("=");
            da.A = Expression();
            Expect(";");
            return Done(da);
        }
        if (IsP("(") && IsDeconstructionStart())
        {
            var da = DeconstructTargets();
            Expect("=");
            da.A = Expression();
            Expect(";");
            return Done(da);
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

    // ( ... , ... ) = の形 (分解の代入)
    private bool IsDeconstructionStart()
    {
        int depth = 0;
        int j = _i;
        bool comma = false;
        while (j < _toks.Count)
        {
            var t = _toks[j];
            if (t.Kind == TokenKind.End) return false;
            if (t.Kind == TokenKind.Punct)
            {
                if (t.Text == "(") depth++;
                else if (t.Text == "," && depth == 1) comma = true;
                else if (t.Text == ")")
                {
                    depth--;
                    if (depth == 0)
                    {
                        var nx = _toks[j + 1];
                        return comma && nx.Kind == TokenKind.Punct && nx.Text == "=";
                    }
                }
                else if (t.Text == ";" || t.Text == "{" || t.Text == "}") return false;
            }
            j++;
        }
        return false;
    }

    // 分解の先の並び: 式、型付きの宣言 (int x / var x)、_、入れ子の ( ... )
    private Node DeconstructTargets()
    {
        var n = N(NodeKind.DeconstructAssign);
        Expect("(");
        while (!IsP(")") && Cur.Kind != TokenKind.End)
        {
            if (IsP("(") && IsNestedTargetList())
            {
                n.Add(DeconstructTargets());
            }
            else if (IsIdent() && Cur.Text == "_" && (IsPAt(1, ",") || IsPAt(1, ")")))
            {
                var d = N(NodeKind.PatDiscard);
                Advance();
                n.Add(Done(d));
            }
            else if (IsOutDeclaration())
            {
                var d = N(NodeKind.PatDecl);
                d.A = Type();
                d.Text = ExpectIdent();
                n.Add(Done(d));
            }
            else n.Add(Expression());
            if (!Accept(",")) break;
        }
        Expect(")");
        return Done(n);
    }

    // var (a, b) の形 (Roslyn の宣言の式。タプルの構文ではない) は Text = "var"
    private Node VarDeconstructTargets(Token varTok)
    {
        var n = N(NodeKind.DeconstructAssign);
        n.Text = "var";
        Expect("(");
        while (!IsP(")") && Cur.Kind != TokenKind.End)
        {
            if (IsP("(")) n.Add(VarDeconstructTargets(varTok));
            else if (IsIdent() && Cur.Text == "_" && (IsPAt(1, ",") || IsPAt(1, ")")))
            {
                var d = N(NodeKind.PatDiscard);
                Advance();
                n.Add(Done(d));
            }
            else
            {
                var d = N(NodeKind.PatDecl);
                var vt = N(NodeKind.TypeName);
                vt.Text = "var";
                d.A = vt;
                d.Text = ExpectIdent();
                n.Add(Done(d));
            }
            if (!Accept(",")) break;
        }
        Expect(")");
        return Done(n);
    }

    // 入れ子の分解の先 ((a, b), c) か (括弧で囲んだ式でなく)
    private bool IsNestedTargetList()
    {
        int depth = 0;
        int j = _i;
        bool comma = false;
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
                    if (depth == 0) return comma;
                }
                else if (t.Text == "," && depth == 1) comma = true;
            }
            j++;
        }
        return false;
    }

    // サブセットの外の文の名前 (Roslyn の SyntaxKind の名前に揃える)
    private string StatementSyntaxName(string what)
    {
        switch (what)
        {
            case "using": return IsPAt(1, "(") ? "UsingStatement" : "UsingDeclaration";
            case "yield": return IsKwAt(1, "break") ? "YieldBreakStatement" : "YieldReturnStatement";
            case "await": return "AwaitExpression";
            case "try": return "TryStatement";
            case "throw": return "ThrowStatement";
            case "goto": return "GotoStatement";
            case "lock": return "LockStatement";
            case "fixed": return "FixedStatement";
            case "unsafe": return "UnsafeStatement";
            case "checked": return "CheckedStatement";
            case "unchecked": return "UncheckedStatement";
        }
        return what;
    }

    private Node UnsupportedStatement(string what)
    {
        var u = N(NodeKind.Unsupported);
        u.Text = what;
        Unsupported(StatementSyntaxName(what), Cur.Pos);
        // try の block は読む (中のサブセットの外の構文も報告する)。木には残さない
        if (what == "try" && IsPAt(1, "{"))
        {
            Advance();
            Block();
            while (IsKw("catch"))
            {
                Advance();
                if (IsP("(")) SkipBalanced("(", ")");
                if (IsContextual("when"))
                {
                    Advance();
                    if (IsP("(")) SkipBalanced("(", ")");
                }
                if (!IsP("{")) break;
                Block();
            }
            if (IsKw("finally"))
            {
                Advance();
                if (IsP("{")) Block();
            }
            return Done(u);
        }
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
            // generic な local function (`T M<T>(T a) => a;`)
            if (!result && nx.Kind == TokenKind.Punct && nx.Text == "<")
            {
                Advance();
                SkipBalanced("<", ">");
                result = IsP("(");
            }
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
        List<Node>? localTypeParams = null;
        if (IsP("<"))
        {
            // generic な local function の型引数 (List2 に Name)
            localTypeParams = new List<Node>();
            Advance();
            while (true)
            {
                var tp = N(NodeKind.Name);
                tp.Text = ExpectIdent();
                localTypeParams.Add(Done(tp));
                if (!Accept(",")) break;
            }
            Expect(">");
        }
        if (IsP("("))
        {
            // local function
            Unsupported("LocalFunctionStatement", namePos);
            var dummy = N(NodeKind.Method);
            dummy.A = d.A;
            dummy.List2 = localTypeParams;
            ParamList(dummy);
            Constraints(dummy.List2);
            MethodBody(dummy);
            var u = NAt(NodeKind.Unsupported, namePos);
            u.Text = "local function";
            // 本文はサブセットの検査のためだけに束縛する (出力はしない)
            u.A = dummy;
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
        if (IsContextual("var") && IsPAt(1, "(") || IsP("(") && IsNestedTargetList())
        {
            // foreach (var (a, b) in ...) / foreach ((int a, int b) in ...): Name の A に型 (var なら null)。var の形は Text = "(var)"
            if (IsContextual("var"))
            {
                Advance();
                n.Text = "(var)";
            }
            Advance();
            while (true)
            {
                var nm = N(NodeKind.Name);
                if (IsIdent() && (IsPAt(1, ",") || IsPAt(1, ")"))) nm.Text = Advance().Text;
                else
                {
                    nm.A = Type();
                    nm.Text = ExpectIdent();
                }
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

    // switch 式の arm の when の中 (`when b => 1` の => は arm の区切りで、lambda ではない)
    private bool _inArmGuard;

    private bool IsLambdaStart()
    {
        if (IsContextual("async") && (IsIdentAt(1) || IsPAt(1, "("))) return false;
        if (IsIdent() && IsPAt(1, "=>")) return !_inArmGuard;
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
            Unsupported("AwaitExpression", Cur.Pos);
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
        // query 式 (`from x in xs ...`) は Select / Where の呼び出しに下ろす糖衣で、tcs は下ろさない。式の終わりまで読み飛ばす
        if (IsContextual("from") && IsIdentAt(1) && (IsKwAt(2, "in") || IsIdentAt(2) && IsKwAt(3, "in")))
        {
            Unsupported("QueryExpression", Cur.Pos);
            var q = N(NodeKind.NullLit);
            int depth = 0;
            while (Cur.Kind != TokenKind.End)
            {
                if (IsP("(") || IsP("[") || IsP("{")) depth++;
                else if (IsP(")") || IsP("]") || IsP("}"))
                {
                    if (depth == 0) break;
                    depth--;
                }
                else if (depth == 0 && (IsP(";") || IsP(","))) break;
                Advance();
            }
            return Done(q);
        }
        // 式の global::X.Y は X.Y と同じに読む (user の型と同じ名前の namespace を隠すことは扱わない)
        if (IsContextual("global") && IsPAt(1, "::") && IsIdentAt(2))
        {
            Advance();
            Advance();
        }
        else if (IsIdent() && IsPAt(1, "::") && IsIdentAt(2))
        {
            // using の別名で修飾した名前 (`S::Socket.Create()`、サブセットの外)
            Unsupported("AliasQualifiedName", Cur.Pos);
            Advance();
            Advance();
        }
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
                    Unsupported("BaseMemberAccess", Cur.Pos);
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
                    Unsupported("TypeOfExpression", Cur.Pos);
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
                    // ref の式 (`ref x`、`ref a[0]`) は ref の型 (RefType) の巻き添えなので、ref の戻り値と ref local の診断に任せる
                    if (k != "ref") Unsupported("expression: " + ExpressionSyntaxName(k), Cur.Pos);
                    var n = N(NodeKind.NullLit);
                    Advance();
                    if (k == "ref")
                    {
                        AcceptKw("readonly");
                        UnaryExpression();
                        return Done(n);
                    }
                    // stackalloc T[n] { ... } の要素の型と長さ
                    if (k == "stackalloc")
                    {
                        if (!IsP("[")) TryType(true);
                        if (IsP("[")) SkipBalanced("[", "]");
                    }
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
            // new (int, string)[2]: tuple の要素型の配列
            int saveTuple = _i;
            var tupleType = TryType(true);
            if (tupleType != null && tupleType.Kind == NodeKind.TupleType && IsP("["))
            {
                _i = saveTuple;
            }
            else
            {
                _i = saveTuple;
                Arguments(n);
                n.Value = 1;
                if (IsP("{")) n.B = ObjectOrCollectionInitializer();
                return Done(n);
            }
        }
        if (IsP("{"))
        {
            Unsupported("AnonymousObjectCreationExpression", Cur.Pos);
            SkipBalanced("{", "}");
            // Value = 3: 匿名型 (束縛は型を調べない)
            n.Value = 3;
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
                Unsupported("MultiDimensionalArray", Cur.Pos);
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
            // 長さは最初の [] にだけ書ける (new int[3][1] は C# の誤り)
            if (IsP("[") && !IsPAt(1, ",")) Error("CS0178", "Invalid rank specifier: expected ',' or ']'");
            if (IsP("{")) na.C = ArrayInitializer();
            return Done(na);
        }
        if (type.Kind == NodeKind.ArrayType)
        {
            var na = NAt(NodeKind.NewArray, n.Pos);
            // new T[,] { ... } (多次元の配列)
            if (type.Value == 2) Unsupported("MultiDimensionalArray", type.Pos);
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
                Unsupported("SpreadElement", Cur.Pos);
                Advance();
            }
            n.Add(Expression());
            if (!Accept(",")) break;
        }
        Expect("]");
        return Done(n);
    }

    // Paren: A。Tuple: List = 要素、List2 = 要素の名前 (Name。名前の無い要素は Text が空)
    private Node ParenOrTuple()
    {
        var pos = Cur.Pos;
        Advance();
        string firstName = TupleElementName();
        var first = Expression();
        if (IsP(",") || firstName != "")
        {
            var t = NAt(NodeKind.Tuple, pos);
            t.Add(first);
            var nm = NAt(NodeKind.Name, pos);
            nm.Text = firstName;
            t.Add2(nm);
            while (Accept(","))
            {
                var nm2 = N(NodeKind.Name);
                nm2.Text = TupleElementName();
                t.Add2(nm2);
                t.Add(Expression());
            }
            Expect(")");
            return Done(t);
        }
        Expect(")");
        var p = NAt(NodeKind.Paren, pos);
        p.A = first;
        return Done(p);
    }

    // tuple の要素の名前 (name: expr)
    private string TupleElementName()
    {
        if (IsIdent() && IsPAt(1, ":") && !IsPAt(2, ":"))
        {
            string name = Advance().Text;
            Advance();
            return name;
        }
        return "";
    }

    // Arg: A = 式、Flags = out / ref / in。out var x は A = DeclarationExpr として PatDecl (A = 型、Text = 名前)
    private void Arguments(Node into)
    {
        // 引数の中は arm の when の外 (`when xs.Any(x => x > 0) => 1` の lambda)
        bool saveGuard = _inArmGuard;
        _inArmGuard = false;
        ArgumentList(into);
        _inArmGuard = saveGuard;
    }

    private void ArgumentList(Node into)
    {
        Expect("(");
        while (!IsP(")") && Cur.Kind != TokenKind.End)
        {
            var a = N(NodeKind.Arg);
            if (IsIdent() && IsPAt(1, ":") && !IsPAt(2, ":"))
            {
                Unsupported("NamedArgument", Cur.Pos);
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
                        Unsupported("IndexExpression", Cur.Pos);
                        Advance();
                    }
                    ix.Add(Expression());
                    if (IsP(".."))
                    {
                        Unsupported("RangeExpression", Cur.Pos);
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
                _inArmGuard = true;
                arm.B = Expression();
                _inArmGuard = false;
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
        if (t.RawDollars > 0) return RawInterpolatedString(t);
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
                    // 書式の中は `}` までが字面 (escape を読み飛ばす)
                    if (formatAt >= 0)
                    {
                        if (d == '}') break;
                        if (!t.Verbatim && d == '\\' || t.Verbatim && d == '"') j++;
                        j++;
                        continue;
                    }
                    // ブロックのコメントの中は数えない
                    if (d == '/' && j + 1 < end && b[j + 1] == '*')
                    {
                        j += 2;
                        while (j + 1 < end && !(b[j] == '*' && b[j + 1] == '/')) j++;
                        j += 2;
                        continue;
                    }
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

    // raw な補間文字列: escape は無く、$ と同じ数の { で穴が始まり、同じ数の } で終わる。
    // それより多い { は文字の { として残す。複数行は各行の字下げを除く
    private Node RawInterpolatedString(Token t)
    {
        var n = NAt(NodeKind.InterpString, t.Pos);
        var b = _file.Bytes;
        int i = t.ContentStart;
        int end = t.ContentEnd;
        int dollars = t.RawDollars;
        var text = new List<string>();
        int run = i;
        while (i < end)
        {
            int c = b[i];
            if (c == '\n' && t.RawIndent > 0)
            {
                text.Add(_file.Slice(run, i + 1));
                i++;
                int skip = 0;
                while (skip < t.RawIndent && i < end && (b[i] == ' ' || b[i] == '\t'))
                {
                    i++;
                    skip++;
                }
                run = i;
                continue;
            }
            if (c == '{')
            {
                int braces = 0;
                while (i + braces < end && b[i + braces] == '{') braces++;
                if (braces < dollars)
                {
                    i += braces;
                    continue;
                }
                // 余分な { は文字
                text.Add(_file.Slice(run, i + braces - dollars));
                FlushText(n, text, t.Pos);
                int holeStart = i + braces;
                int j = holeStart;
                int depth = 0;
                int formatAt = -1;
                while (j < end)
                {
                    int d = b[j];
                    if (d == '(' || d == '[') depth++;
                    else if (d == ')' || d == ']') depth--;
                    else if (d == ':' && depth == 0 && formatAt < 0) formatAt = j;
                    else if (d == '}')
                    {
                        int closes = 0;
                        while (j + closes < end && b[j + closes] == '}') closes++;
                        if (closes >= dollars) break;
                        j += closes;
                        continue;
                    }
                    j++;
                }
                int exprEnd = formatAt >= 0 ? formatAt : j;
                var hole = NAt(NodeKind.InterpHole, holeStart);
                var lexer = new Lexer(_file, _diags, holeStart, exprEnd);
                var sub = new Parser(_file, _diags, lexer.Tokenize());
                hole.A = sub.Expression();
                if (formatAt >= 0) hole.Text = _file.Slice(formatAt + 1, j);
                n.Add(Done(hole));
                i = j + dollars;
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
            // tuple の pattern (`(1, 2)`、`(Sum: var s, Count: var c)`)。読み飛ばす (中の pattern の変数は宣言しない)
            Unsupported("TuplePattern", Cur.Pos);
            var c = N(NodeKind.PatDiscard);
            SkipBalanced("(", ")");
            return Done(c);
        }
        if (IsP("["))
        {
            Unsupported("ListPattern", Cur.Pos);
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
