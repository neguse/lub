using System.Collections.Generic;

namespace TcsSelf;

// 名前と member の参照の種類 (Name / Member の Info)
public static class RefKind
{
    public const int None = 0;
    public const int Local = 1;
    public const int Field = 2;
    public const int Property = 3;
    public const int MethodGroup = 4;
    public const int Type = 5;
    public const int Namespace = 6;
    public const int EnumMember = 7;
    public const int Error = 8;
}

// 呼び出しの種類 (Call の Info)
public static class CallKind
{
    public const int Method = 1;
    public const int Builtin = 2;
    public const int Delegate = 3;
    public const int Extension = 4;
    public const int Nameof = 5;
}

// new の種類 (New の Info)
public static class NewKind
{
    public const int Class = 1;
    public const int List = 2;
    public const int ListCopy = 3;
    public const int Dict = 4;
    public const int HashSet = 5;
    public const int Queue = 6;
    public const int Utf8Bytes = 7;
    public const int Struct = 8;
    public const int Random = 9;
    public const int TypeParam = 10;
}

// 変換の種類 (Conv の Info)
public static class ConvKind
{
    public const int Identity = 0;
    public const int IntToFloat = 1;
    public const int FloatToInt = 2;
    public const int IntToChar = 3;
    public const int CastClass = 4;
    public const int CastInterface = 5;
    public const int NullableValue = 6;
    public const int Unbox = 7;
    public const int MethodGroup = 8;
    public const int UnboxFloat = 9;
    public const int UnboxBool = 10;
    public const int UnboxString = 11;
    // int? → float? (null はそのまま)
    public const int NullableIntToFloat = 12;
}

// foreach の種類 (Foreach の Info)
public static class ForeachKind
{
    public const int List = 1;
    public const int Array = 2;
    public const int Dict = 3;
    public const int HashSet = 4;
    public const int Queue = 5;
    public const int Runes = 6;
    public const int ListView = 7;
}

sealed class LocalScope
{
    public Dictionary<string, LocalSym> Vars = new Dictionary<string, LocalSym>();
    public LocalScope? Parent;
    // lambda の境界
    public bool IsLambda;

    public LocalScope(LocalScope? parent)
    {
        Parent = parent;
    }
}

// lambda の本体を束縛する間の文脈
sealed class LambdaFrame
{
    public TypeSym? Ret;
    public List<Node> Returns = new List<Node>();
    public bool Infer;
}

public sealed class Binder
{
    private readonly Compilation _c;
    private readonly Declarer _d;
    private TypeDeclSym? _type;
    private TypeScope? _scope;
    private bool _static;
    private bool _inInitAccessor;
    // 代入の左辺 / out の引数 / 分解の先を束縛している (local を読んだことにしない)
    private bool _writeTarget;
    // 到達できない文を束縛している深さ (C# は到達できない場所の変数を代入済みとみなす)
    private int _unreachable;
    // instance の field / auto property の初期化子の中か (instance の member と this は使えない)
    private bool _instanceFieldInit;
    // unchecked(...) の中か (定数式の桁あふれの CS0220 を出さない)
    private bool _unchecked;
    private Symbol? _member;
    private LocalScope? _locals;
    private TypeSym _returnType = TypeSym.VoidType;
    private LambdaFrame? _lambda;
    private Node? _stmt;
    private int _depth;

    public Binder(Compilation c, Declarer d)
    {
        _c = c;
        _d = d;
    }

    private void Error(string code, string message, Node at) => _c.Error(code, message, at);

    private void Unsupported(string what, Node at) => _c.Error("TCS1001", "Unsupported syntax: " + what, at);

    // 参照実装は受け付けるが、セルフホストがまだ扱わない機能
    private void SelfHostUnsupported(string what, Node at) => _c.Error("TCS1900", "SelfHostUnsupported(" + what + ")", at);

    // ---- 全体 ----

    public void BindAll()
    {
        foreach (var t in _c.Types)
        {
            if (t.IsRef) continue;
            if (t.IsEnum) BindEnum(t);
        }
        foreach (var t in _c.Types)
        {
            if (t.IsRef || t.IsEnum) continue;
            BindType(t);
        }
        foreach (var unit in _c.Units)
        {
            if (unit.List2 != null) BindTopLevel(unit);
        }
    }

    // top-level の文 (static な文脈で、型の外)
    private void BindTopLevel(Node unit)
    {
        var usings = new List<string>();
        if (unit.List != null)
        {
            foreach (var n in unit.List)
            {
                if (n.Kind == NodeKind.Using && n.D == null) usings.Add(n.Text);
            }
        }
        _type = null;
        _scope = new TypeScope(unit.File, "", usings);
        _scope.UsingLevels = new List<string>();
        foreach (var u in usings) _scope.UsingLevels.Add("");
        _scope.Aliases = _d.CollectAliases(unit, null);
        _member = null;
        _static = true;
        _locals = new LocalScope(null);
        _lambda = null;
        _depth = 0;
        // top-level の return が値を返すなら、入口の戻り値は int (C# の規則)
        _returnType = HasValueReturn(unit.List2!) ? TypeSym.IntType : TypeSym.VoidType;
        foreach (var st in unit.List2!) BindStatement(st);
    }

    // 文の列のどこか (lambda の中を除く) に値を返す return があるか
    private static bool HasValueReturn(List<Node> stmts)
    {
        foreach (var st in stmts)
        {
            if (HasValueReturnIn(st)) return true;
        }
        return false;
    }

    private static bool HasValueReturnIn(Node? n)
    {
        if (n == null || n.Kind == NodeKind.Lambda) return false;
        if (n.Kind == NodeKind.Return) return n.A != null;
        if (HasValueReturnIn(n.A) || HasValueReturnIn(n.B) || HasValueReturnIn(n.C) || HasValueReturnIn(n.D)) return true;
        if (n.List != null && HasValueReturn(n.List)) return true;
        if (n.List2 != null && HasValueReturn(n.List2)) return true;
        return n.List3 != null && HasValueReturn(n.List3);
    }

    private void EnterType(TypeDeclSym t, Symbol? member, bool isStatic)
    {
        _type = t;
        _scope = _d.ScopeOf(t);
        _member = member;
        _static = isStatic;
        _locals = new LocalScope(null);
        _lambda = null;
        _depth = 0;
        if (member != null && member.TypeParams != null)
        {
            List<bool>? flags = null;
            if (member.Decl != null && member.Decl.List2 != null)
            {
                flags = new List<bool>();
                foreach (var tpn in member.Decl.List2) flags.Add((tpn.Value & 2) != 0);
            }
            _scope = _scope.WithMethodTypeParams(member.TypeParams, flags);
        }
    }

    private void BindEnum(TypeDeclSym t)
    {
        EnterType(t, null, true);
        int next = 0;
        foreach (var m in t.Members)
        {
            if (m.Kind != SymKind.EnumMember) continue;
            next = EnsureEnumValue(m, next);
        }
    }

    // m の値を決め、次の member の既定値を返す
    private int EnsureEnumValue(Symbol m, int next)
    {
        if (m.ConstVal != null) return m.ConstVal.I + 1;
        var em = m.Decl!;
        if (em.A != null)
        {
            m.ConstState = 1;
            em.A = BindValue(em.A, m.Owner!.EnumBase!);
            if (em.A.HasConst && em.A.Const!.Kind == ConstKind.Int)
            {
                m.ConstVal = ConstValue.OfInt(em.A.Const.I);
            }
            else
            {
                Error("CS0133", "The expression being assigned to '" + m.Name + "' must be constant", em);
                m.ConstVal = ConstValue.OfInt(0);
            }
        }
        else
        {
            m.ConstVal = ConstValue.OfInt(next);
        }
        m.ConstState = 2;
        return m.ConstVal.I + 1;
    }

    // enum member の値 (前の member の値に依存するので、その enum の先頭から順に決める)
    private ConstValue EnumValue(Symbol m)
    {
        if (m.ConstVal != null) return m.ConstVal;
        var saveType = _type;
        var saveScope = _scope;
        var saveStatic = _static;
        var saveMember = _member;
        var saveLocals = _locals;
        var saveLambda = _lambda;
        var saveStmt = _stmt;
        int next = 0;
        _type = m.Owner;
        _scope = _d.ScopeOf(m.Owner!);
        _locals = new LocalScope(null);
        _lambda = null;
        foreach (var o in m.Owner!.Members)
        {
            if (o.Kind != SymKind.EnumMember) continue;
            if (o.ConstState == 1 && o.ConstVal == null)
            {
                Error("CS0110", "The evaluation of the constant value for '" + o.Name + "' involves a circular definition", o.Decl!);
                o.ConstVal = ConstValue.OfInt(0);
            }
            next = EnsureEnumValue(o, next);
            if (o == m) break;
        }
        _type = saveType;
        _scope = saveScope;
        _static = saveStatic;
        _member = saveMember;
        _locals = saveLocals;
        _lambda = saveLambda;
        _stmt = saveStmt;
        return m.ConstVal!;
    }

    private void BindType(TypeDeclSym t)
    {
        foreach (var m in t.Members)
        {
            var decl = m.Decl;
            if (decl == null) continue;
            switch (m.Kind)
            {
                case SymKind.Field:
                    if (decl.A != null && !m.IsConst)
                    {
                        EnterType(t, m, m.IsStatic);
                        _instanceFieldInit = !m.IsStatic && !t.IsRecord;
                        decl.A = BindInitializer(decl.A, m.Type);
                        _instanceFieldInit = false;
                    }
                    else if (m.IsConst) ConstOfField(m);
                    break;
                case SymKind.Property:
                    if (decl.Kind != NodeKind.Property) break;
                    EnterType(t, m, m.IsStatic);
                    if (decl.B != null) BindAccessor(decl.B, m.Type, null);
                    if (decl.C != null)
                    {
                        var value = new LocalSym("value", m.Type);
                        value.IsParam = true;
                        BindAccessor(decl.C, TypeSym.VoidType, value);
                    }
                    if (decl.D != null)
                    {
                        EnterType(t, m, m.IsStatic);
                        _instanceFieldInit = !m.IsStatic && !t.IsRecord;
                        decl.D = BindInitializer(decl.D, m.Type);
                        _instanceFieldInit = false;
                    }
                    break;
                case SymKind.Method:
                    EnterType(t, m, m.IsStatic);
                    BindMethodBody(decl, m, m.Type);
                    break;
            }
        }
        if (t.RecordParams != null && t.Decl.A != null)
        {
            // record の positional parameter の既定値
            EnterType(t, null, true);
            for (int i = 0; i < t.RecordParams.Count; i++)
            {
                var pn = t.Decl.A.Item(i);
                if (pn.B != null)
                {
                    pn.B = BindValue(pn.B, t.RecordParams[i].Type);
                    t.RecordParams[i].Default = pn.B;
                }
            }
        }
        foreach (var ctor in t.Ctors)
        {
            var decl = ctor.Decl!;
            if (decl.Kind != NodeKind.Ctor) continue;
            EnterType(t, ctor, false);
            DeclareParams(decl, ctor);
            if (decl.D != null)
            {
                if (decl.D.Text == "base") Unsupported("base constructor call", decl.D);
                else
                {
                    var target = ResolveCall(t.Ctors, decl.D, null, decl.D);
                    decl.D.Sym = target;
                }
            }
            if (decl.B != null)
            {
                _returnType = TypeSym.VoidType;
                BindBlock(decl.B);
            }
            else if (decl.C != null)
            {
                _returnType = TypeSym.VoidType;
                decl.C = BindExprStatementExpr(decl.C);
            }
        }
    }

    private Node BindInitializer(Node init, TypeSym type)
    {
        if (init.Kind == NodeKind.ArrayInit) return BindArrayInit(init, type);
        return BindValue(init, type);
    }

    private void BindAccessor(Node acc, TypeSym ret, LocalSym? value)
    {
        _returnType = ret;
        // init accessor の中は constructor と同じく readonly の field と init の property に代入できる
        _inInitAccessor = acc.Text == "init";
        PushScope();
        if (value != null) DeclareLocal(value, acc);
        if (acc.A != null)
        {
            BindBlock(acc.A);
            if (ret.Kind != TypeKind.Void && !ret.IsError && CanComplete(acc.A))
                Error("CS0161", "'" + (_member != null ? _member.Name + ".get" : "get") + "': not all code paths return a value", acc);
        }
        else if (acc.B != null)
        {
            if (ret.Kind == TypeKind.Void) acc.B = BindExprStatementExpr(acc.B);
            else acc.B = BindValue(acc.B, ret);
        }
        PopScope();
        _inInitAccessor = false;
    }

    private void DeclareParams(Node decl, Symbol m)
    {
        if (decl.List == null || m.Params == null) return;
        for (int i = 0; i < decl.List.Count; i++)
        {
            var p = decl.List[i];
            var ls = new LocalSym(p.Text, m.Params[i].Type);
            ls.IsParam = true;
            ls.IsOut = m.Params[i].IsOut;
            p.Local = ls;
            DeclareLocal(ls, p);
            if (p.B != null) p.B = BindValue(p.B, m.Params[i].Type);
        }
    }

    private void BindMethodBody(Node decl, Symbol m, TypeSym ret)
    {
        DeclareParams(decl, m);
        _returnType = ret;
        if (decl.B != null)
        {
            BindBlock(decl.B);
            if (ret.Kind != TypeKind.Void && !ret.IsError && CanComplete(decl.B))
                Error("CS0161", "'" + (_type != null ? _type.Name + "." : "") + m.Name + "': not all code paths return a value", decl);
        }
        else if (decl.C != null)
        {
            if (ret.Kind == TypeKind.Void) decl.C = BindExprStatementExpr(decl.C);
            else decl.C = BindValue(decl.C, ret);
        }
    }

    // const field の値
    private ConstValue? ConstOfField(Symbol f)
    {
        if (f.ConstState == 2) return f.ConstVal;
        if (f.ConstState == 1)
        {
            Error("CS0110", "The evaluation of the constant value for '" + f.Name + "' involves a circular definition", f.Decl!);
            f.ConstState = 2;
            return null;
        }
        f.ConstState = 1;
        var saveType = _type;
        var saveScope = _scope;
        var saveStatic = _static;
        var saveMember = _member;
        var saveLocals = _locals;
        var saveLambda = _lambda;
        var saveStmt = _stmt;
        EnterType(f.Owner!, f, true);
        var decl = f.Decl!;
        if (decl.A != null)
        {
            decl.A = BindValue(decl.A, f.Type);
            if (decl.A.HasConst) f.ConstVal = decl.A.Const;
            else Error("CS0133", "The expression being assigned to '" + f.Name + "' must be constant", decl.A);
        }
        f.ConstState = 2;
        _type = saveType;
        _scope = saveScope;
        _static = saveStatic;
        _member = saveMember;
        _locals = saveLocals;
        _lambda = saveLambda;
        _stmt = saveStmt;
        return f.ConstVal;
    }

    // ---- 局所変数の scope ----

    private void PushScope()
    {
        _locals = new LocalScope(_locals);
    }

    private void PopScope()
    {
        _locals = _locals!.Parent;
    }

    private void DeclareLocal(LocalSym l, Node at)
    {
        if (l.Name == "_") return;
        var s = _locals;
        while (s != null)
        {
            if (s.Vars.ContainsKey(l.Name))
            {
                if (s == _locals) Error("CS0128", "A local variable or function named '" + l.Name + "' is already defined in this scope", at);
                else if (!s.IsLambda) Error("CS0136", "A local or parameter named '" + l.Name + "' cannot be declared in this scope because that name is used in an enclosing local scope to define a local or parameter", at);
                break;
            }
            if (s.IsLambda) break;
            s = s.Parent;
        }
        // Lua の予約語と、型の global 名 (生成コードは型を名前で参照する) を避ける。
        // 改名した名前はファイルの他の識別子と重ならないものにする
        string ln = l.Name;
        if (LuaNames.IsReserved(ln) || _c.TypeLuaNames.Contains(ln))
        {
            ln = ln + "_";
            while (LuaNames.IsReserved(ln) || _c.TypeLuaNames.Contains(ln) || at.File.Identifiers.Contains(ln)) ln = ln + "_";
        }
        l.LuaName = ln;
        l.Depth = _depth;
        _locals!.Vars[l.Name] = l;
    }

    // 式の中で宣言する局所変数 (out var と pattern)。文の前で宣言させる
    private void DeclareExprLocal(LocalSym l, Node at)
    {
        DeclareLocal(l, at);
        if (_stmt != null)
        {
            if (_stmt.DeclLocals == null) _stmt.DeclLocals = new List<LocalSym>();
            _stmt.DeclLocals.Add(l);
        }
    }

    private LocalSym? LookupLocal(string name)
    {
        var s = _locals;
        bool crossedLambda = false;
        while (s != null)
        {
            if (s.Vars.TryGetValue(name, out var l))
            {
                if (crossedLambda) l.Captured = true;
                return l;
            }
            if (s.IsLambda) crossedLambda = true;
            s = s.Parent;
        }
        return null;
    }

    // ---- 文 ----

    private void BindBlock(Node b)
    {
        PushScope();
        if (b.List != null) BindStatements(b.List);
        PopScope();
    }

    // 文の列。終わりに到達できない文の後の文は到達できない (CS0165 を出さない)
    private void BindStatements(List<Node> stmts)
    {
        bool dead = false;
        foreach (var st in stmts)
        {
            if (dead) _unreachable++;
            BindStatement(st);
            if (dead) _unreachable--;
            else if (!CanComplete(st)) dead = true;
        }
    }

    // cond が定数 value なら、body を到達できないものとして束縛する
    private void BindMaybeDead(Node body, bool dead)
    {
        if (dead) _unreachable++;
        BindEmbedded(body);
        if (dead) _unreachable--;
    }

    private void BindEmbedded(Node s)
    {
        // 埋め込み文は自分の scope を持つ
        if (s.Kind == NodeKind.Block)
        {
            BindBlock(s);
            return;
        }
        PushScope();
        BindStatement(s);
        PopScope();
    }

    private void BindStatement(Node s)
    {
        var saveStmt = _stmt;
        _stmt = s;
        switch (s.Kind)
        {
            case NodeKind.Block:
                _stmt = saveStmt;
                BindBlock(s);
                return;
            case NodeKind.Empty:
            case NodeKind.Unsupported:
                break;
            case NodeKind.LocalDecl:
                BindLocalDecl(s);
                break;
            case NodeKind.DeconstructDecl:
                BindDeconstructDecl(s);
                break;
            case NodeKind.DeconstructAssign:
                BindDeconstructAssign(s, s.A!);
                break;
            case NodeKind.ExprStmt:
                s.A = BindExprStatementExpr(s.A!);
                break;
            case NodeKind.If:
                s.A = BindCondition(s.A!);
                BindMaybeDead(s.B!, IsConstBool(s.A, false));
                if (s.C != null) BindMaybeDead(s.C, IsConstBool(s.A, true));
                break;
            case NodeKind.While:
                PushScope();
                s.A = BindCondition(s.A!);
                _depth++;
                BindMaybeDead(s.B!, IsConstBool(s.A, false));
                _depth--;
                PopScope();
                break;
            case NodeKind.DoWhile:
                _depth++;
                BindEmbedded(s.B!);
                _depth--;
                PushScope();
                s.A = BindCondition(s.A!);
                PopScope();
                break;
            case NodeKind.For:
                BindFor(s);
                break;
            case NodeKind.Foreach:
                BindForeach(s);
                break;
            case NodeKind.Break:
            case NodeKind.Continue:
                break;
            case NodeKind.Return:
                BindReturn(s);
                break;
            case NodeKind.Switch:
                BindSwitch(s);
                break;
            default:
                Error("CS1002", "Unexpected statement", s);
                break;
        }
        _stmt = saveStmt;
    }

    private Node BindCondition(Node e) => BindValue(e, TypeSym.BoolType);

    private void BindReturn(Node s)
    {
        if (_lambda != null)
        {
            if (s.A == null) return;
            if (_lambda.Infer)
            {
                BindExpr(s.A, null);
                _lambda.Returns.Add(s);
                return;
            }
            if (_lambda.Ret == null || _lambda.Ret.Kind == TypeKind.Void)
            {
                s.A = BindExprStatementExpr(s.A);
                return;
            }
            s.A = BindValue(s.A, _lambda.Ret);
            return;
        }
        if (s.A == null)
        {
            if (_returnType.Kind != TypeKind.Void) Error("CS0126", "An object of a type convertible to '" + _returnType.Display() + "' is required", s);
            return;
        }
        if (_returnType.Kind == TypeKind.Void)
        {
            Error("CS0127", "Since the method returns void, a return keyword must not be followed by an object expression", s);
            BindExpr(s.A, null);
            return;
        }
        s.A = BindValue(s.A, _returnType);
    }

    private void BindLocalDecl(Node s)
    {
        bool isVar = s.A!.Kind == NodeKind.TypeName && s.A.Text == "var" && s.A.List == null && s.A.A == null && _d.LookupTypeByName("var", 0, _scope!) == null;
        TypeSym? declared = isVar ? null : _d.ResolveType(s.A, _scope!);
        foreach (var v in s.List!)
        {
            TypeSym t;
            if (declared == null)
            {
                if (v.A == null)
                {
                    Error("CS0818", "Implicitly-typed variables must be initialized", v);
                    t = TypeSym.ErrorType;
                }
                else
                {
                    if (v.A.Kind == NodeKind.ArrayInit)
                    {
                        Error("CS0820", "Cannot initialize an implicitly-typed variable with an array initializer", v);
                    }
                    BindExpr(v.A, null);
                    t = v.A.Type!;
                    if (t.Kind == TypeKind.Null || t.Kind == TypeKind.Lambda || t.Kind == TypeKind.MethodGroup || t.Kind == TypeKind.Void)
                    {
                        Error("CS0815", "Cannot assign " + t.Display() + " to an implicitly-typed variable", v);
                        t = TypeSym.ErrorType;
                    }
                    if (t.Kind == TypeKind.StaticRef)
                    {
                        Error("CS0119", "'" + t.Name + "' is a type, which is not valid in the given context", v);
                        t = TypeSym.ErrorType;
                    }
                }
            }
            else
            {
                t = declared;
                if (v.A != null) v.A = BindInitializer(v.A, t);
            }
            var l = new LocalSym(v.Text, t);
            if (s.Has(Mods.Const))
            {
                l.IsConst = true;
                if (v.A != null && v.A.HasConst) l.ConstVal = v.A.Const;
                else if (v.A != null) Error("CS0133", "The expression being assigned to '" + v.Text + "' must be constant", v);
            }
            // 初期化子の無い local は、読む前にどこかで代入されていなければ CS0165 (ソースの順で見る緩い判定)
            // struct と tuple は field ごとに代入できるので見ない
            if (v.A == null && !l.IsConst && !(t.Decl != null && t.Decl.IsStruct) && t.Kind != TypeKind.Tuple && t.Kind != TypeKind.KeyValuePair) l.Unassigned = true;
            v.Local = l;
            DeclareLocal(l, v);
        }
    }

    // 書き込み先として束縛する (local を読んだことにしない)。束縛した後に代入済みにする
    private void BindWriteTarget(Node target)
    {
        bool save = _writeTarget;
        // 名前そのものだけが書き込み先 (a.F = ... の a や a[i] = ... の i は読む)
        _writeTarget = target.Kind == NodeKind.Name;
        BindExpr(target, null);
        _writeTarget = save;
    }

    private static void MarkAssigned(Node target)
    {
        if (target.Info == RefKind.Local && target.Local != null && (target.Kind == NodeKind.Name || target.Kind == NodeKind.Paren))
            target.Local.Unassigned = false;
        else if (target.Kind == NodeKind.Paren && target.A != null) MarkAssigned(target.A);
    }

    // var (a, b) = expr: record の positional parameter に分ける
    private void BindDeconstructDecl(Node s)
    {
        BindExpr(s.A!, null);
        var t = s.A!.Type!;
        if (t.Kind == TypeKind.Tuple)
        {
            if (t.Args!.Count != s.Count) Error("CS8132", "Cannot deconstruct a tuple of '" + t.Args.Count + "' elements into '" + s.Count + "' variables", s);
            for (int i = 0; i < s.Count; i++)
            {
                var nm = s.Item(i);
                var lt = i < t.Args.Count ? t.Args[i] : TypeSym.ErrorType;
                var l = new LocalSym(nm.Text, lt);
                nm.Local = l;
                nm.Sym = TupleElement(t, "Item" + (i + 1));
                DeclareLocal(l, nm);
            }
            return;
        }
        List<ParamSym>? ps = t.Decl != null ? t.Decl.RecordParams : null;
        if (ps == null)
        {
            if (!t.IsError) SelfHostUnsupported("deconstruction of " + t.Display(), s);
            ps = new List<ParamSym>();
        }
        if (ps.Count != s.Count && !t.IsError)
            Error("CS8130", "Cannot deconstruct a record of " + ps.Count + " elements into " + s.Count + " variables", s);
        for (int i = 0; i < s.Count; i++)
        {
            var nm = s.Item(i);
            var lt = i < ps.Count ? ps[i].Type.Subst(t.Args, null) : TypeSym.ErrorType;
            var l = new LocalSym(nm.Text, lt);
            nm.Local = l;
            if (i < ps.Count) nm.Sym = t.Decl!.Lookup(ps[i].Name)![0];
            DeclareLocal(l, nm);
        }
    }

    // (a, var b, (c, d)) = value
    private void BindDeconstructAssign(Node targets, Node value)
    {
        // 値: tuple の式なら要素ごとに分解の先の型へ
        if (value.Kind == NodeKind.Tuple && value.Count == targets.Count)
        {
            var expected = new TypeSym(TypeKind.Tuple, "tuple");
            expected.Args = new List<TypeSym>();
            bool allKnown = true;
            for (int i = 0; i < targets.Count; i++)
            {
                var tt = TargetType(targets.Item(i));
                if (tt == null) allKnown = false;
                expected.Args.Add(tt ?? TypeSym.ErrorType);
            }
            if (allKnown) BindTupleTo(value, expected);
            else BindExpr(value, null);
        }
        else BindExpr(value, null);
        var vt = value.Type!;
        List<TypeSym>? elems = null;
        if (vt.Kind == TypeKind.Tuple) elems = vt.Args;
        else if (vt.Decl != null && vt.Decl.RecordParams != null)
        {
            elems = new List<TypeSym>();
            foreach (var p in vt.Decl.RecordParams) elems.Add(p.Type.Subst(vt.Args, null));
        }
        else if (!vt.IsError) SelfHostUnsupported("deconstruction of " + vt.Display(), targets);
        if (elems != null && elems.Count != targets.Count) Error("CS8132", "Cannot deconstruct into " + targets.Count + " variables", targets);
        for (int i = 0; i < targets.Count; i++)
        {
            var tgt = targets.Item(i);
            var et = elems != null && i < elems.Count ? elems[i] : TypeSym.ErrorType;
            BindDeconstructTarget(tgt, et);
        }
        targets.Type = vt;
    }

    // 分解の先の型 (宣言なら宣言の型、式なら式の型、var なら null)
    private TypeSym? TargetType(Node t)
    {
        if (t.Kind == NodeKind.PatDecl)
        {
            bool isVar = t.A!.Kind == NodeKind.TypeName && t.A.Text == "var" && t.A.List == null && t.A.A == null;
            if (isVar) return null;
            return _d.ResolveType(t.A, _scope!);
        }
        if (t.Kind == NodeKind.PatDiscard) return null;
        if (t.Kind == NodeKind.DeconstructAssign) return null;
        int save = _c.Diags.Items.Count;
        return null;
    }

    private void BindTupleTo(Node value, TypeSym expected)
    {
        BindTuple(value, expected);
    }

    private void BindDeconstructTarget(Node tgt, TypeSym et)
    {
        switch (tgt.Kind)
        {
            case NodeKind.PatDiscard:
                tgt.Type = et;
                return;
            case NodeKind.PatDecl:
                {
                    bool isVar = tgt.A!.Kind == NodeKind.TypeName && tgt.A.Text == "var" && tgt.A.List == null && tgt.A.A == null;
                    var lt = isVar ? et : _d.ResolveType(tgt.A, _scope!);
                    if (!isVar && !IsImplicitlyConvertible(et, lt)) Error("CS0029", "Cannot implicitly convert type '" + et.Display() + "' to '" + lt.Display() + "'", tgt);
                    var l = new LocalSym(tgt.Text, lt);
                    tgt.Local = l;
                    tgt.Type = lt;
                    DeclareExprLocal(l, tgt);
                    return;
                }
            case NodeKind.DeconstructAssign:
                {
                    // 入れ子: 値の要素をさらに分解する
                    if (et.Kind != TypeKind.Tuple || et.Args!.Count != tgt.Count)
                    {
                        if (!et.IsError) SelfHostUnsupported("nested deconstruction of " + et.Display(), tgt);
                        return;
                    }
                    for (int i = 0; i < tgt.Count; i++) BindDeconstructTarget(tgt.Item(i), et.Args[i]);
                    tgt.Type = et;
                    return;
                }
        }
        BindWriteTarget(tgt);
        CheckAssignable(tgt);
        MarkAssigned(tgt);
        if (!IsImplicitlyConvertible(et, tgt.Type!)) Error("CS0029", "Cannot implicitly convert type '" + et.Display() + "' to '" + tgt.Type!.Display() + "'", tgt);
    }

    private void BindFor(Node s)
    {
        PushScope();
        if (s.List != null)
        {
            foreach (var init in s.List)
            {
                var save = _stmt;
                _stmt = init;
                if (init.Kind == NodeKind.LocalDecl) BindLocalDecl(init);
                else init.A = BindExprStatementExpr(init.A!);
                _stmt = save;
            }
        }
        if (s.A != null) s.A = BindCondition(s.A);
        _depth++;
        BindMaybeDead(s.B!, IsConstBool(s.A, false));
        _depth--;
        // 更新の式は本体の後に実行されるので、本体の後に束縛する (本体の代入を見てから CS0165 を判定する)
        if (s.List2 != null)
        {
            for (int i = 0; i < s.List2.Count; i++) s.List2[i] = BindExprStatementExpr(s.List2[i]);
        }
        PopScope();
    }

    private void BindForeach(Node s)
    {
        BindExpr(s.B!, null);
        var ct = s.B!.Type!;
        TypeSym elem = TypeSym.ErrorType;
        switch (ct.Kind)
        {
            case TypeKind.List:
                elem = ct.Elem;
                s.Info = s.B.Kind == NodeKind.Call && s.B.Sym != null && s.B.Sym.Builtin == "String.EnumerateRunes" ? ForeachKind.Runes : ForeachKind.List;
                break;
            case TypeKind.Array:
                elem = ct.Elem;
                s.Info = ForeachKind.Array;
                break;
            case TypeKind.KeyCollection:
                elem = ct.Arg(0);
                s.Info = ForeachKind.Array;
                break;
            case TypeKind.ValueCollection:
                elem = ct.Arg(1);
                s.Info = ForeachKind.Array;
                break;
            case TypeKind.Dict:
                elem = TypeSym.Generic2(TypeKind.KeyValuePair, "KeyValuePair", ct.Arg(0), ct.Arg(1));
                s.Info = ForeachKind.Dict;
                break;
            case TypeKind.HashSet:
                elem = ct.Elem;
                s.Info = ForeachKind.HashSet;
                break;
            case TypeKind.Queue:
                elem = ct.Elem;
                s.Info = ForeachKind.Queue;
                break;
            case TypeKind.String:
                Error("TCS1002", "unsupported API: foreach over string (use s.EnumerateRunes() or Utf8Bytes)", s.B);
                break;
            case TypeKind.Error:
                break;
            default:
                Error("CS1579", "foreach statement cannot operate on variables of type '" + ct.Display() + "'", s.B);
                break;
        }
        PushScope();
        if (s.List != null)
        {
            // foreach (var (k, v) in dict) と tuple の列
            bool tupleElems = elem.Kind == TypeKind.Tuple && ct.Kind != TypeKind.Dict;
            if (ct.Kind != TypeKind.Dict && !tupleElems) SelfHostUnsupported("foreach deconstruction", s);
            for (int i = 0; i < s.List.Count; i++)
            {
                var nm = s.List[i];
                TypeSym lt;
                if (tupleElems)
                {
                    lt = i < elem.Args!.Count ? elem.Args[i] : TypeSym.ErrorType;
                    nm.Sym = TupleElement(elem, "Item" + (i + 1));
                    if (nm.A != null)
                    {
                        var declared = _d.ResolveType(nm.A, _scope!);
                        if (!IsImplicitlyConvertible(lt, declared)) Error("CS0029", "Cannot implicitly convert type '" + lt.Display() + "' to '" + declared.Display() + "'", nm);
                        lt = declared;
                    }
                }
                else lt = i == 0 && ct.Kind == TypeKind.Dict ? ct.Arg(0) : ct.Kind == TypeKind.Dict ? ct.Arg(1) : TypeSym.ErrorType;
                var l = new LocalSym(nm.Text, lt);
                nm.Local = l;
                DeclareLocal(l, nm);
            }
            if (tupleElems) s.Value = 7;
        }
        else
        {
            TypeSym vt = elem;
            bool isVar = s.A!.Kind == NodeKind.TypeName && s.A.Text == "var" && s.A.List == null && s.A.A == null;
            if (!isVar)
            {
                vt = _d.ResolveType(s.A, _scope!);
                if (!IsExplicitlyConvertible(elem, vt)) Error("CS0030", "Cannot convert type '" + elem.Display() + "' to '" + vt.Display() + "'", s);
                if (!TypeSym.Same(elem, vt) && !elem.IsError && !vt.IsError) s.Type2 = vt;
            }
            var l = new LocalSym(s.Text, vt);
            s.Local = l;
            DeclareLocal(l, s);
        }
        _depth++;
        BindEmbedded(s.C!);
        _depth--;
        PopScope();
    }

    private void BindSwitch(Node s)
    {
        BindExpr(s.A!, null);
        var gt = s.A!.Type!;
        if (gt.Kind == TypeKind.Lambda || gt.Kind == TypeKind.MethodGroup) Error("CS8119", "The switch expression must be a value", s.A);
        foreach (var sec in s.List!)
        {
            PushScope();
            var save = _stmt;
            _stmt = sec;
            foreach (var label in sec.List!)
            {
                if (label.Kind == NodeKind.CaseLabel) label.A = BindPattern(label.A!, gt);
            }
            // 定数の switch で、どの label も値に合わない section (when と文) は到達できない
            bool deadSection = s.A.HasConst && s.A.Const != null && !SectionMayMatch(sec, s.A.Const);
            if (deadSection) _unreachable++;
            foreach (var label in sec.List!)
            {
                if (label.Kind == NodeKind.CaseLabel && label.B != null) label.B = BindCondition(label.B);
            }
            _stmt = save;
            if (sec.List2 != null)
            {
                BindStatements(sec.List2);
                if (sec.List2.Count > 0)
                {
                    var last = sec.List2[sec.List2.Count - 1];
                    if (!EndsFlow(last)) Error("CS0163", "Control cannot fall through from one case label to another", sec);
                }
            }
            if (deadSection) _unreachable--;
            PopScope();
        }
    }

    // section のどれかの label が定数 v に合いうるか (default、定数でない pattern、等しい定数)
    private static bool SectionMayMatch(Node sec, ConstValue v)
    {
        foreach (var label in sec.List!)
        {
            if (label.Kind != NodeKind.CaseLabel) return true;
            var pat = label.A!;
            if (pat.Kind != NodeKind.PatConst) return true;
            var c = pat.A;
            if (c == null || !c.HasConst || c.Const == null) return true;
            if (SameConst(c.Const, v)) return true;
        }
        return false;
    }

    private static bool SameConst(ConstValue a, ConstValue b)
    {
        if (a.Kind != b.Kind) return false;
        switch (a.Kind)
        {
            case ConstKind.Int: return a.I == b.I;
            case ConstKind.Bool: return a.B == b.B;
            case ConstKind.String: return a.S == b.S;
            case ConstKind.Float: return a.F == b.F;
        }
        return false;
    }

    // 文の最後が制御を外へ出すか (switch の section の fall through の検査)
    // struct の値を持つ式が変数 (書き換えられる場所) か: local、parameter、this、変数の struct の field、class の field、配列の要素
    private static bool IsValueVariable(Node r)
    {
        switch (r.Kind)
        {
            case NodeKind.This:
                return true;
            case NodeKind.Paren:
                return IsValueVariable(r.A!);
            case NodeKind.Name:
            case NodeKind.Member:
                if (r.Info == RefKind.Local) return true;
                if (r.Info == RefKind.Field)
                {
                    var f = r.Sym!;
                    if (f.IsStatic || f.Owner == null || !f.Owner.IsStruct) return true;
                    if (r.Kind == NodeKind.Name) return true;
                    return r.A != null && IsValueVariable(r.A);
                }
                return r.Info != RefKind.Property && r.Info != RefKind.MethodGroup;
            case NodeKind.Index:
                return r.A != null && r.A.Type != null && r.A.Type.Kind == TypeKind.Array;
            case NodeKind.Call:
                return false;
        }
        return true;
    }

    private static string ValueSourceName(Node r)
    {
        if ((r.Kind == NodeKind.Member || r.Kind == NodeKind.Name) && r.Sym != null) return (r.Sym.Owner != null ? r.Sym.Owner.Name + "." : "") + r.Sym.Name;
        if (r.Kind == NodeKind.Call && r.Sym != null) return r.Sym.Name + "()";
        return "expression";
    }

    private static bool IsInstanceMember(List<Symbol> members)
    {
        foreach (var m in members)
        {
            if (m.IsStatic || m.IsConst || m.Kind == SymKind.EnumMember) return false;
        }
        return true;
    }

    // ---- 到達可能性 (C# §13.2) ----

    private static bool IsConstBool(Node? e, bool value) =>
        e != null && e.HasConst && e.Const != null && e.Const.Kind == ConstKind.Bool && e.Const.B == value;

    // 文 s に到達したとき、s の終わりに到達できるか (束縛した後の木で見る。定数の条件は畳んだ値を見る)
    private static bool CanComplete(Node s)
    {
        switch (s.Kind)
        {
            case NodeKind.Return:
            case NodeKind.Break:
            case NodeKind.Continue:
            case NodeKind.Unsupported:
                return false;
            case NodeKind.Block:
                if (s.List != null)
                {
                    foreach (var st in s.List)
                    {
                        if (!CanComplete(st)) return false;
                    }
                }
                return true;
            case NodeKind.If:
                {
                    bool thenEnd = !IsConstBool(s.A, false) && CanComplete(s.B!);
                    bool elseReach = !IsConstBool(s.A, true);
                    if (s.C == null) return thenEnd || elseReach;
                    return thenEnd || elseReach && CanComplete(s.C);
                }
            case NodeKind.While:
                return !IsConstBool(s.A, true) || BreaksOut(s.B);
            case NodeKind.DoWhile:
                if (BreaksOut(s.B)) return true;
                return !IsConstBool(s.A, true) && (CanComplete(s.B!) || ContinuesIn(s.B));
            case NodeKind.For:
                if (s.A == null || IsConstBool(s.A, true)) return BreaksOut(s.B);
                return true;
            case NodeKind.Switch:
                {
                    bool hasDefault = false;
                    bool anyBreak = false;
                    if (s.List != null)
                    {
                        foreach (var sec in s.List)
                        {
                            if (sec.List != null)
                            {
                                foreach (var label in sec.List)
                                {
                                    if (label.Kind == NodeKind.DefaultLabel) hasDefault = true;
                                    else if (label.A != null && label.A.Kind == NodeKind.PatDiscard && label.B == null) hasDefault = true;
                                }
                            }
                            if (sec.List2 != null)
                            {
                                foreach (var st in sec.List2)
                                {
                                    if (BreaksOut(st)) anyBreak = true;
                                }
                            }
                        }
                    }
                    return !hasDefault || anyBreak;
                }
        }
        return true;
    }

    // s の中に、s を囲む loop / switch から出る break があるか (内側の loop / switch と lambda の中は見ない)
    private static bool BreaksOut(Node? s) => ContainsJump(s, NodeKind.Break);

    private static bool ContinuesIn(Node? s) => ContainsJump(s, NodeKind.Continue);

    private static bool ContainsJump(Node? s, NodeKind jump)
    {
        if (s == null) return false;
        if (s.Kind == jump) return true;
        switch (s.Kind)
        {
            case NodeKind.While:
            case NodeKind.DoWhile:
            case NodeKind.For:
            case NodeKind.Foreach:
            case NodeKind.Lambda:
                return false;
            case NodeKind.Switch:
                // switch の中の continue は外側の loop へ出る
                if (jump == NodeKind.Break) return false;
                break;
        }
        if (s.Kind != NodeKind.Block && s.Kind != NodeKind.If && s.Kind != NodeKind.Switch && s.Kind != NodeKind.SwitchSection) return false;
        if (ContainsJump(s.B, jump) || ContainsJump(s.C, jump)) return true;
        if (s.List != null)
        {
            foreach (var x in s.List)
            {
                if (ContainsJump(x, jump)) return true;
            }
        }
        if (s.List2 != null)
        {
            foreach (var x in s.List2)
            {
                if (ContainsJump(x, jump)) return true;
            }
        }
        return false;
    }

    private static bool EndsFlow(Node s)
    {
        switch (s.Kind)
        {
            case NodeKind.Break:
            case NodeKind.Continue:
            case NodeKind.Return:
            case NodeKind.Unsupported:
                return true;
            case NodeKind.Block:
                if (s.List == null || s.List.Count == 0) return false;
                return EndsFlow(s.List[s.List.Count - 1]);
            case NodeKind.If:
                return s.C != null && EndsFlow(s.B!) && EndsFlow(s.C);
            case NodeKind.While:
                return s.A!.HasConst && s.A.Const!.Kind == ConstKind.Bool && s.A.Const.B;
        }
        return false;
    }

    // 式文: 代入、呼び出し、++ / --、new だけが書ける
    private Node BindExprStatementExpr(Node e)
    {
        switch (e.Kind)
        {
            case NodeKind.Assign:
            case NodeKind.Call:
            case NodeKind.PostIncDec:
            case NodeKind.PreIncDec:
            case NodeKind.New:
                break;
            default:
                Error("CS0201", "Only assignment, call, increment, decrement, and new object expressions can be used as a statement", e);
                break;
        }
        BindExpr(e, null);
        return e;
    }

    // ---- 式 ----

    // 式を束縛し、target があればその型へ暗黙に変換したノードを返す
    private Node BindValue(Node e, TypeSym target)
    {
        BindExpr(e, target);
        return Coerce(e, target);
    }

    // 束縛済みなら変換だけ、未束縛なら束縛してから変換する (束縛し直すと入れ子の深さに対して指数的に遅くなる)
    private Node BindOrCoerce(Node e, TypeSym to)
    {
        if (e.Type == null) return BindValue(e, to);
        if (e.Type.Kind == TypeKind.Lambda && to.Kind == TypeKind.Delegate) return Coerce(e, to);
        return Coerce(e, to);
    }

    private static Node MakeConv(Node e, TypeSym to, int kind)
    {
        var c = new Node(NodeKind.Conv, e.File, e.Pos);
        c.End = e.End;
        c.A = e;
        c.Type = to;
        c.Info = kind;
        return c;
    }

    // 暗黙の変換。変換できなければ診断を出す
    private Node Coerce(Node e, TypeSym to)
    {
        var from = e.Type!;
        if (from.IsError || to.IsError) return e;
        if (to.Kind == TypeKind.Delegate && (from.Kind == TypeKind.Lambda || from.Kind == TypeKind.MethodGroup))
        {
            if (from.Kind == TypeKind.Lambda)
            {
                BindLambda(e, to);
                return e;
            }
            return ConvertMethodGroup(e, to);
        }
        if (TypeSym.Same(from, to)) return e;
        if (from.Kind == TypeKind.Lambda || from.Kind == TypeKind.MethodGroup)
        {
            Error("CS1660", "Cannot convert " + from.Display() + " to type '" + to.Display() + "' because it is not a delegate type", e);
            return e;
        }
        int kind = ImplicitKind(e, from, to);
        if (kind < 0)
        {
            Error("CS0029", "Cannot implicitly convert type '" + from.Display() + "' to '" + to.Display() + "'", e);
            return e;
        }
        // 定数の変換は畳む
        if (e.HasConst)
        {
            var folded = FoldConversion(e.Const!, from, to);
            if (folded != null)
            {
                var c = MakeConv(e, to, ConvKind.Identity);
                c.HasConst = true;
                c.Const = folded;
                return c;
            }
        }
        if (kind == ConvKind.Identity && from.Kind == to.Kind && from.Kind != TypeKind.Null && from.Kind != TypeKind.Nullable && to.Kind != TypeKind.Nullable)
        {
            // 型だけが変わる (interface への参照変換など)
            var c = MakeConv(e, to, kind);
            return c;
        }
        return MakeConv(e, to, kind);
    }

    private ConstValue? FoldConversion(ConstValue v, TypeSym from, TypeSym to)
    {
        if (to.Kind == TypeKind.Float && v.Kind == ConstKind.Int) return ConstValue.OfFloat(IntToFloat(v.I));
        if (to.Kind == TypeKind.Nullable)
        {
            if (v.Kind == ConstKind.Null) return v;
            return FoldConversion(v, from, to.Elem);
        }
        if (v.Kind == ConstKind.Int && (to.Kind == TypeKind.Int || to.Kind == TypeKind.Byte || to.Kind == TypeKind.Char || to.Kind == TypeKind.UInt || to.Kind == TypeKind.Short || to.Kind == TypeKind.Enum || to.Kind == TypeKind.Long)) return v;
        if (v.Kind == ConstKind.Float && to.Kind == TypeKind.Float) return v;
        if (v.Kind == ConstKind.String && to.Kind == TypeKind.String) return v;
        if (v.Kind == ConstKind.Bool && to.Kind == TypeKind.Bool) return v;
        if (v.Kind == ConstKind.Null && to.IsNullable) return v;
        return null;
    }

    private static float IntToFloat(int i) => i;

    // 暗黙の変換の種類。無ければ -1
    private int ImplicitKind(Node? e, TypeSym from, TypeSym to)
    {
        if (TypeSym.Same(from, to)) return ConvKind.Identity;
        if (from.IsError || to.IsError) return ConvKind.Identity;
        if (from.Kind == TypeKind.Null)
        {
            if (to.IsNullable || to.Kind == TypeKind.TypeParam) return ConvKind.Identity;
            return -1;
        }
        // 定数の int → byte / char ではない狭い整数型
        if (e != null && e.HasConst && e.Const!.Kind == ConstKind.Int && from.Kind == TypeKind.Int)
        {
            int v = e.Const.I;
            if (to.Kind == TypeKind.Byte && v >= 0 && v <= 255) return ConvKind.Identity;
            if (to.Kind == TypeKind.Short && v >= -32768 && v <= 32767) return ConvKind.Identity;
            if (to.Kind == TypeKind.UInt && v >= 0) return ConvKind.Identity;
            if (to.Kind == TypeKind.Enum && v == 0) return ConvKind.Identity;
        }
        int num = NumericImplicit(from, to);
        if (num >= 0) return num;
        // 参照実装の緩和 (CompilationDiagnosticPolicy): enum と整数 (char を除く) の暗黙の変換
        if (from.Kind == TypeKind.Enum && to.IsIntegral && to.Kind != TypeKind.Char) return ConvKind.Identity;
        if (to.Kind == TypeKind.Enum && from.IsIntegral && from.Kind != TypeKind.Char) return ConvKind.Identity;
        if (to.Kind == TypeKind.Nullable)
        {
            if (from.Kind == TypeKind.Nullable)
            {
                // 持ち上げた変換 (int? → float?)
                int lifted = ImplicitKind(null, from.Elem, to.Elem);
                if (lifted == ConvKind.Identity) return ConvKind.Identity;
                if (lifted == ConvKind.IntToFloat) return ConvKind.NullableIntToFloat;
                return -1;
            }
            int inner = ImplicitKind(e, from, to.Elem);
            if (inner >= 0) return inner;
            return -1;
        }
        if (to.Kind == TypeKind.Object) return ConvKind.Identity;
        if (from.Kind == TypeKind.TypeParam)
        {
            var cs = ConstraintsOf(from);
            if (cs != null)
            {
                foreach (var c in cs)
                {
                    if (ImplicitKind(null, c, to) >= 0) return ConvKind.Identity;
                }
            }
            return -1;
        }
        if (to.Kind == TypeKind.Interface)
        {
            if (from.Decl != null && to.Decl != null && from.Decl.Implements(to.Decl)) return ConvKind.Identity;
            return -1;
        }
        if (from.Kind == TypeKind.Tuple && to.Kind == TypeKind.Tuple && from.Args!.Count == to.Args!.Count)
        {
            for (int i = 0; i < from.Args.Count; i++)
            {
                if (ImplicitKind(null, from.Args[i], to.Args[i]) != ConvKind.Identity) return -1;
            }
            return ConvKind.Identity;
        }
        return -1;
    }

    private static int NumericImplicit(TypeSym from, TypeSym to)
    {
        switch (from.Kind)
        {
            case TypeKind.Int:
                if (to.Kind == TypeKind.Float || to.Kind == TypeKind.Double) return ConvKind.IntToFloat;
                if (to.Kind == TypeKind.Long) return ConvKind.Identity;
                break;
            case TypeKind.Char:
                if (to.Kind == TypeKind.Int || to.Kind == TypeKind.UInt || to.Kind == TypeKind.Long) return ConvKind.Identity;
                if (to.Kind == TypeKind.Float || to.Kind == TypeKind.Double) return ConvKind.IntToFloat;
                break;
            case TypeKind.Byte:
                if (to.Kind == TypeKind.Int || to.Kind == TypeKind.UInt || to.Kind == TypeKind.Short || to.Kind == TypeKind.Long) return ConvKind.Identity;
                if (to.Kind == TypeKind.Float || to.Kind == TypeKind.Double) return ConvKind.IntToFloat;
                break;
            case TypeKind.Short:
                if (to.Kind == TypeKind.Int || to.Kind == TypeKind.Long) return ConvKind.Identity;
                if (to.Kind == TypeKind.Float || to.Kind == TypeKind.Double) return ConvKind.IntToFloat;
                break;
            case TypeKind.UInt:
                if (to.Kind == TypeKind.Long) return ConvKind.Identity;
                if (to.Kind == TypeKind.Float || to.Kind == TypeKind.Double) return ConvKind.IntToFloat;
                break;
            case TypeKind.Long:
                if (to.Kind == TypeKind.Float || to.Kind == TypeKind.Double) return ConvKind.IntToFloat;
                break;
            case TypeKind.Float:
                if (to.Kind == TypeKind.Double) return ConvKind.Identity;
                break;
        }
        return -1;
    }

    private bool IsImplicitlyConvertible(TypeSym from, TypeSym to) => ImplicitKind(null, from, to) >= 0;

    private bool IsExplicitlyConvertible(TypeSym from, TypeSym to)
    {
        if (IsImplicitlyConvertible(from, to)) return true;
        if (from.IsNumeric && to.IsNumeric) return true;
        if (from.Kind == TypeKind.Enum && to.IsIntegral || from.IsIntegral && to.Kind == TypeKind.Enum) return true;
        if (from.Kind == TypeKind.Object || from.Kind == TypeKind.Interface || from.Kind == TypeKind.TypeParam) return true;
        if (to.Kind == TypeKind.Interface && from.Kind == TypeKind.Class) return true;
        if (from.Kind == TypeKind.Nullable && TypeSym.Same(from.Elem, to)) return true;
        if (from.Kind == TypeKind.Nullable && IsExplicitlyConvertible(from.Elem, to)) return true;
        return false;
    }

    // 束縛して Type を付ける。expected は target-typed な式 (lambda、null、new() など) の型
    private void BindExpr(Node e, TypeSym? expected)
    {
        switch (e.Kind)
        {
            case NodeKind.IntLit: BindIntLit(e); return;
            case NodeKind.FloatLit: BindFloatLit(e); return;
            case NodeKind.StringLit:
                e.Type = TypeSym.StringType;
                e.HasConst = true;
                e.Const = ConstValue.OfString(e.Text);
                return;
            case NodeKind.CharLit:
                e.Type = TypeSym.CharType;
                e.HasConst = true;
                e.Const = ConstValue.OfInt(e.Value);
                return;
            case NodeKind.BoolLit:
                e.Type = TypeSym.BoolType;
                e.HasConst = true;
                e.Const = ConstValue.OfBool(e.Value != 0);
                return;
            case NodeKind.NullLit:
                e.Type = TypeSym.NullType;
                e.HasConst = true;
                e.Const = new ConstValue(ConstKind.Null);
                return;
            case NodeKind.DefaultLit: BindDefault(e, expected); return;
            case NodeKind.InterpString: BindInterp(e); return;
            case NodeKind.Name: BindName(e); return;
            case NodeKind.Member: BindMember(e); return;
            case NodeKind.Call: BindCall(e); return;
            case NodeKind.Index: BindIndex(e); return;
            case NodeKind.New: BindNew(e, expected); return;
            case NodeKind.NewArray: BindNewArray(e, expected); return;
            case NodeKind.CollectionExpr: BindCollectionExpr(e, expected); return;
            case NodeKind.Unary: BindUnary(e); return;
            case NodeKind.PreIncDec:
            case NodeKind.PostIncDec:
                BindIncDec(e);
                return;
            case NodeKind.Binary: BindBinary(e, expected); return;
            case NodeKind.Assign: BindAssign(e); return;
            case NodeKind.Conditional: BindConditional(e, expected); return;
            case NodeKind.Is: BindIs(e); return;
            case NodeKind.As: BindAs(e); return;
            case NodeKind.Cast: BindCast(e); return;
            case NodeKind.Lambda:
                if (expected != null && expected.Kind == TypeKind.Delegate)
                {
                    BindLambda(e, expected);
                    return;
                }
                e.Type = TypeSym.LambdaType;
                return;
            case NodeKind.This:
                if (_static || _type == null)
                {
                    Error("CS0026", "Keyword 'this' is not valid in a static property, static method, or static field initializer", e);
                    e.Type = TypeSym.ErrorType;
                    return;
                }
                if (_instanceFieldInit) Error("CS0027", "Keyword 'this' is not available in the current context", e);
                e.Type = _type.SelfType();
                return;
            case NodeKind.SwitchExpr: BindSwitchExpr(e, expected); return;
            case NodeKind.Paren:
                BindExpr(e.A!, expected);
                e.Type = e.A!.Type;
                e.HasConst = e.A.HasConst;
                e.Const = e.A.Const;
                return;
            case NodeKind.Tuple:
                BindTuple(e, expected);
                return;
            case NodeKind.Checked:
                {
                    // unchecked(...) の中の定数式は桁あふれを誤りにしない (checked(...) は戻す)
                    bool saveUnchecked = _unchecked;
                    _unchecked = e.Value == 0;
                    BindExpr(e.A!, expected);
                    _unchecked = saveUnchecked;
                }
                e.Type = e.A!.Type;
                e.HasConst = e.A.HasConst;
                e.Const = e.A.Const;
                return;
            case NodeKind.Typeof:
                e.Type = TypeSym.ErrorType;
                return;
            case NodeKind.Conv:
                return;
            case NodeKind.ArrayInit:
                Error("CS0622", "Can only use array initializer expressions to assign to array types", e);
                e.Type = TypeSym.ErrorType;
                return;
        }
        Error("CS1525", "Invalid expression term", e);
        e.Type = TypeSym.ErrorType;
    }

    // (a, b): 要素の型の tuple。対象の tuple 型があれば要素をその型へ変換する
    private void BindTuple(Node e, TypeSym? expected)
    {
        var t = new TypeSym(TypeKind.Tuple, "tuple");
        t.Args = new List<TypeSym>();
        t.Names = new List<string>();
        bool target = expected != null && expected.Kind == TypeKind.Tuple && expected.Args!.Count == e.Count;
        for (int i = 0; i < e.Count; i++)
        {
            if (target) e.List![i] = BindValue(e.Item(i), expected!.Args![i]);
            else BindExpr(e.Item(i), null);
            var et = e.Item(i).Type!;
            if (et.Kind == TypeKind.Null || et.Kind == TypeKind.Lambda || et.Kind == TypeKind.MethodGroup)
            {
                Error("CS8135", "Tuple with " + e.Count + " elements cannot be converted to type 'object'", e.Item(i));
                et = TypeSym.ErrorType;
            }
            t.Args.Add(et);
            string name = e.List2 != null && i < e.List2.Count ? e.List2[i].Text : "";
            if (name == "" && !target)
            {
                // 推論された要素名 (var t = (a, b) の a / b)
                var el = e.Item(i);
                if (el.Kind == NodeKind.Name) name = el.Text;
                else if (el.Kind == NodeKind.Member) name = el.Text;
            }
            t.Names.Add(name);
        }
        if (e.Count > 7) SelfHostUnsupported("tuple of more than 7 elements", e);
        e.Type = target ? expected! : t;
    }

    // tuple の要素の symbol (名前でも ItemN でも引ける。Lua の field は ItemN)
    private static Symbol? TupleElement(TypeSym t, string name)
    {
        for (int i = 0; i < t.Args!.Count; i++)
        {
            string item = "Item" + (i + 1);
            bool named = t.Names != null && i < t.Names.Count && t.Names[i] == name;
            if (name == item || named)
            {
                var s = new Symbol(SymKind.Field, item, t.Args[i]);
                return s;
            }
        }
        return null;
    }

    private void BindIntLit(Node e)
    {
        string t = e.Text;
        var u = new TinySystem.Text.Utf8Bytes(t);
        int n = u.Length;
        // suffix
        bool unsigned = false;
        bool isLong = false;
        while (n > 0)
        {
            int c = u[n - 1];
            if (c == 'u' || c == 'U') unsigned = true;
            else if (c == 'l' || c == 'L') isLong = true;
            else break;
            n--;
        }
        int value = 0;
        bool overflow = false;
        bool hex = n > 2 && u[0] == '0' && (u[1] == 'x' || u[1] == 'X');
        bool bin = n > 2 && u[0] == '0' && (u[1] == 'b' || u[1] == 'B');
        bool big = false;
        if (hex || bin)
        {
            int bits = 0;
            int bitsPer = hex ? 4 : 1;
            for (int i = 2; i < n; i++)
            {
                int d = hex ? Lexer.HexValue(u[i]) : u[i] - '0';
                if (d < 0 || !hex && d > 1)
                {
                    Error("CS1013", "Invalid number", e);
                    break;
                }
                if (bits == 0 && d == 0) continue;
                bits += bitsPer;
                if (bits > 32) overflow = true;
                value = (value << bitsPer) | d;
            }
            if (value < 0) big = true;
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                int d = u[i] - '0';
                if (d < 0 || d > 9)
                {
                    Error("CS1013", "Invalid number", e);
                    break;
                }
                if (value > 214748364 || value == 214748364 && d > 8)
                {
                    overflow = true;
                    break;
                }
                if (value == 214748364 && d == 8)
                {
                    // 2147483648 は uint (単項 - の operand なら int.MinValue)
                    big = true;
                    value = -2147483647 - 1;
                    if (i != n - 1) overflow = true;
                    break;
                }
                value = value * 10 + d;
            }
        }
        if (overflow || isLong)
        {
            Error("TCS1001", "Unsupported syntax: integer literal out of the int / uint range", e);
        }
        e.HasConst = true;
        e.Const = ConstValue.OfInt(value);
        e.Type = unsigned || big ? TypeSym.UIntType : TypeSym.IntType;
        if (big && !hex && !bin) e.Info = 1;
    }

    private void BindFloatLit(Node e)
    {
        string t = e.Text;
        var u = new TinySystem.Text.Utf8Bytes(t);
        int last = u[u.Length - 1];
        string digits = t;
        if (last == 'f' || last == 'F')
        {
            digits = u.Slice(0, u.Length - 1).ToString();
        }
        else
        {
            if (last == 'd' || last == 'D' || last == 'm' || last == 'M') digits = u.Slice(0, u.Length - 1).ToString();
            Error("TCS1001", "Unsupported syntax: DoubleLiteral (write a float literal with the f suffix)", e);
        }
        float v = 0f;
        if (!float.TryParse(digits, out v)) Error("CS1013", "Invalid number", e);
        e.HasConst = true;
        e.Const = ConstValue.OfFloat(v);
        e.Type = TypeSym.FloatType;
    }

    private void BindDefault(Node e, TypeSym? expected)
    {
        TypeSym t;
        if (e.A != null) t = _d.ResolveType(e.A, _scope!);
        else if (expected != null) t = expected;
        else
        {
            Error("CS8716", "There is no target type for the default literal", e);
            t = TypeSym.ErrorType;
        }
        e.Type = t;
        var c = DefaultConst(t);
        if (c != null)
        {
            e.HasConst = true;
            e.Const = c;
        }
    }

    private static ConstValue? DefaultConst(TypeSym t)
    {
        switch (t.Kind)
        {
            case TypeKind.TypeParam:
                // 型引数の既定値は実行時の記述子で決まる
                return null;
            case TypeKind.Int:
            case TypeKind.Char:
            case TypeKind.Byte:
            case TypeKind.UInt:
            case TypeKind.Short:
            case TypeKind.Enum:
                return ConstValue.OfInt(0);
            case TypeKind.Float:
                return ConstValue.OfFloat(0f);
            case TypeKind.Bool:
                return ConstValue.OfBool(false);
        }
        if (t.IsNullable) return new ConstValue(ConstKind.Null);
        return null;
    }

    private void BindInterp(Node e)
    {
        if (e.List != null)
        {
            foreach (var part in e.List)
            {
                if (part.Kind == NodeKind.InterpHole)
                {
                    BindExpr(part.A!, null);
                    CheckStringifiable(part.A!);
                    if (part.B != null) part.B = BindValue(part.B, TypeSym.IntType);
                    if (part.B != null && !part.B.HasConst) Error("CS0150", "A constant value is expected", part.B);
                }
            }
        }
        e.Type = TypeSym.StringType;
    }

    private void CheckStringifiable(Node e)
    {
        var t = e.Type!;
        if (t.Kind == TypeKind.Void) Error("CS0029", "Cannot implicitly convert type 'void' to 'object'", e);
        if (t.Kind == TypeKind.Lambda || t.Kind == TypeKind.MethodGroup) Error("CS0428", "Cannot convert method group to non-delegate type 'object'", e);
        if (t.Kind == TypeKind.Class || t.Kind == TypeKind.Struct || t.Kind == TypeKind.Interface)
        {
            if (!HasToString(t)) Error("TCS1001", "unsupported syntax: UnsupportedToString(" + t.Display() + ")", e);
        }
        if (t.Kind == TypeKind.List || t.Kind == TypeKind.Dict || t.Kind == TypeKind.Array || t.Kind == TypeKind.HashSet || t.Kind == TypeKind.Queue || t.Kind == TypeKind.Delegate)
            Error("TCS1001", "unsupported syntax: UnsupportedToString(" + t.Display() + ")", e);
        if (t.Kind == TypeKind.Utf8Bytes) Error("TCS1001", "unsupported syntax: Utf8Bytes in string conversion (call ToString())", e);
    }

    private static bool HasToString(TypeSym t)
    {
        if (t.Decl == null) return false;
        var list = t.Decl.Lookup("ToString");
        if (list == null) return false;
        foreach (var m in list)
        {
            if (m.Kind == SymKind.Method && m.ParamCount == 0 && m.Decl != null && m.Decl.Has(Mods.Override)) return true;
        }
        return false;
    }

    // 型引数の制約 (今の method か型の型引数)
    private List<TypeSym>? ConstraintsOf(TypeSym tp)
    {
        if (tp.Kind != TypeKind.TypeParam) return null;
        if (tp.IsMethodParam)
        {
            if (_member != null && _member.TypeParamConstraints != null && tp.Index < _member.TypeParamConstraints.Count) return _member.TypeParamConstraints[tp.Index];
            return null;
        }
        var t = _type;
        while (t != null)
        {
            if (tp.Index < t.TypeParams.Count && t.TypeParams[tp.Index] == tp.Name && tp.Index < t.TypeParamConstraints.Count) return t.TypeParamConstraints[tp.Index];
            t = t.Outer;
        }
        return null;
    }

    // ---- 名前 ----

    private void BindName(Node e)
    {
        string name = e.Text;
        if (e.Value == 1)
        {
            // int.Parse などの組み込みの型名
            e.Info = RefKind.Type;
            e.Type = PredefStatic(name, e);
            return;
        }
        if (e.List == null)
        {
            var l = LookupLocal(name);
            if (l != null)
            {
                e.Info = RefKind.Local;
                e.Local = l;
                e.Type = l.Type;
                if (l.Unassigned && !_writeTarget && _unreachable == 0)
                {
                    Error("CS0165", "Use of unassigned local variable '" + name + "'", e);
                    l.Unassigned = false;
                }
                if (l.IsConst && l.ConstVal != null)
                {
                    e.HasConst = true;
                    e.Const = l.ConstVal;
                }
                return;
            }
        }
        if (name == "nameof" && e.List == null)
        {
            e.Info = RefKind.MethodGroup;
            e.Type = TypeSym.MethodGroupType;
            return;
        }
        // 型の member (自分と外側の型)
        var cur = _type;
        while (cur != null)
        {
            var members = cur.Lookup(name);
            if (members != null)
            {
                BindMemberRef(e, null, cur, members, cur == _type ? !_static : false, true);
                if (_instanceFieldInit && cur == _type && IsInstanceMember(members))
                    Error("CS0236", "A field initializer cannot reference the non-static field, method, or property '" + cur.Name + "." + name + "'", e);
                return;
            }
            cur = cur.Outer;
        }
        // 型
        var tsym = _d.LookupTypeByName(name, e.List == null ? 0 : e.List.Count, _scope!);
        if (tsym != null)
        {
            e.Info = RefKind.Type;
            e.Type = tsym.Decl != null && e.List != null ? TypeSym.OfDecl(tsym.Decl, TypeArgsOf(e)) : tsym;
            return;
        }
        // using static の型の static な member
        foreach (var usingType in _d.StaticUsingTypes(_scope!))
        {
            var sms = usingType.Lookup(name);
            if (sms == null) continue;
            var statics = new List<Symbol>();
            foreach (var sm in sms)
            {
                if (sm.IsStatic || sm.IsConst || sm.Kind == SymKind.EnumMember) statics.Add(sm);
            }
            if (statics.Count == 0) continue;
            BindMemberRef(e, null, usingType, statics, false, true);
            return;
        }
        var st = Builtins.StaticType(name);
        if (st != null)
        {
            e.Info = RefKind.Type;
            e.Type = st;
            return;
        }
        var bt = _d.BuiltinType(name, TypeArgsOf(e), e, null);
        if (bt != null)
        {
            e.Info = RefKind.Type;
            e.Type = bt;
            return;
        }
        // namespace
        if (_c.Namespaces.Contains(name) || name == "System" || name == "TinySystem")
        {
            e.Info = RefKind.Namespace;
            e.Type = TypeSym.ErrorType;
            return;
        }
        Error("CS0103", "The name '" + name + "' does not exist in the current context", e);
        e.Info = RefKind.Error;
        e.Type = TypeSym.ErrorType;
    }

    private List<TypeSym>? TypeArgsOf(Node e)
    {
        if (e.List == null) return null;
        var args = new List<TypeSym>();
        foreach (var a in e.List) args.Add(_d.ResolveType(a, _scope!));
        return args;
    }

    private TypeSym PredefStatic(string name, Node at)
    {
        switch (name)
        {
            case "int": return new TypeSym(TypeKind.StaticRef, "int.static");
            case "float": return new TypeSym(TypeKind.StaticRef, "float.static");
            case "string": return new TypeSym(TypeKind.StaticRef, "string.static");
            case "char": return new TypeSym(TypeKind.StaticRef, "char.static");
            case "bool": return new TypeSym(TypeKind.StaticRef, "bool.static");
            case "object": return new TypeSym(TypeKind.StaticRef, "object.static");
        }
        Error("TCS1002", "unsupported API: " + name, at);
        return TypeSym.ErrorType;
    }

    // member の参照 (receiver は null なら暗黙の this か static)
    private void BindMemberRef(Node e, Node? receiver, TypeDeclSym owner, List<Symbol> members, bool instanceOk, bool implicitThis)
    {
        var first = members[0];
        if (first.Kind == SymKind.Method)
        {
            e.Info = RefKind.MethodGroup;
            e.Cands = members;
            e.Type = TypeSym.MethodGroupType;
            return;
        }
        e.Sym = first;
        if (first.Kind == SymKind.EnumMember)
        {
            e.Info = RefKind.EnumMember;
            e.Type = first.Type;
            e.HasConst = true;
            e.Const = EnumValue(first);
            return;
        }
        if (first.Kind == SymKind.Field)
        {
            e.Info = RefKind.Field;
            e.Type = first.Type;
            if (first.IsConst)
            {
                var cv = ConstOfField(first);
                if (cv != null)
                {
                    e.HasConst = true;
                    e.Const = cv;
                }
            }
        }
        else
        {
            e.Info = RefKind.Property;
            e.Type = first.Type;
        }
        if (implicitThis && !first.IsStatic && !instanceOk)
        {
            Error("CS0120", "An object reference is required for the non-static field, method, or property '" + owner.Name + "." + first.Name + "'", e);
        }
        if (first.Owner != null && first.Owner.TypeParams.Count > 0 && receiver != null && receiver.Type != null)
        {
            e.Type = first.Type.Subst(receiver.Type.Args, null);
        }
    }

    private void BindMember(Node e)
    {
        var recv = e.A!;
        int diagMark = _c.Diags.Items.Count;
        bool recvFresh = recv.Type == null;
        if (recv.Type == null) BindExpr(recv, null);
        string name = e.Text;
        if (e.Value == 1)
        {
            BindConditionalMember(e);
            return;
        }
        var rt = recv.Type!;
        if (recv.Info == RefKind.Namespace && (recv.Kind == NodeKind.Name || recv.Kind == NodeKind.Member))
        {
            string ns = NamespaceText(recv);
            string full = ns + "." + name;
            if (_c.TypesByFullName.TryGetValue(full, out var td))
            {
                e.Info = RefKind.Type;
                e.Type = TypeSym.OfDecl(td, TypeArgsOf(e));
                return;
            }
            var st = ns == "TinySystem" && name == "List" ? new TypeSym(TypeKind.StaticRef, "TinySystem.List") : Builtins.StaticType(name);
            if (st != null)
            {
                e.Info = RefKind.Type;
                e.Type = st;
                return;
            }
            var bt = _d.BuiltinType(name, TypeArgsOf(e), e, ns);
            if (bt != null)
            {
                e.Info = RefKind.Type;
                e.Type = bt;
                return;
            }
            e.Info = RefKind.Namespace;
            e.Type = TypeSym.ErrorType;
            return;
        }
        if (rt.IsError)
        {
            e.Type = TypeSym.ErrorType;
            e.Info = RefKind.Error;
            return;
        }
        if (recv.Kind == NodeKind.Name && recv.Info != RefKind.Type && rt.Decl != null && rt.Decl.Name == recv.Text && IsStaticMemberOf(rt.Decl, name))
        {
            // Color Color の規則 (C# §12.8.7.2): 名前と同じ名前の型を持つ値の、型の static な member は型から引く
            recv.Info = RefKind.Type;
            recv.Sym = null;
            recv.Local = null;
            recv.HasConst = false;
            // static な文脈で instance の member を指した診断 (CS0120) は、型として読むので取り消す
            if (recvFresh) _c.Diags.Truncate(diagMark);
        }
        bool isStatic = recv.Info == RefKind.Type;
        LookupMemberOn(e, recv, RecvType(recv), name, isStatic);
        Chain(e, recv);
    }

    // t の name が static な member (const、enum の member、全ての overload が static な method を含む) か入れ子の型か
    private static bool IsStaticMemberOf(TypeDeclSym t, string name)
    {
        foreach (var nested in t.Nested)
        {
            if (nested.Name == name) return true;
        }
        var ms = t.Lookup(name);
        if (ms == null) return false;
        foreach (var m in ms)
        {
            if (!m.IsStatic && !m.IsConst && m.Kind != SymKind.EnumMember) return false;
        }
        return true;
    }

    private static bool HasInheritedMember(TypeDeclSym t, string name)
    {
        foreach (var i in t.Interfaces)
        {
            if (i.Decl != null && (i.Decl.Lookup(name) != null || HasInheritedMember(i.Decl, name))) return true;
        }
        return false;
    }

    private static string NamespaceText(Node n)
    {
        if (n.Kind == NodeKind.Name) return n.Text;
        return NamespaceText(n.A!) + "." + n.Text;
    }

    private void LookupMemberOn(Node e, Node recv, TypeSym rt, string name, bool isStatic)
    {
        if (rt.Kind == TypeKind.Tuple && !isStatic)
        {
            var el = TupleElement(rt, name);
            if (el != null)
            {
                e.Sym = el;
                e.Info = RefKind.Field;
                e.Type = el.Type;
                return;
            }
        }
        if (rt.Kind == TypeKind.TypeParam && !isStatic)
        {
            // 型引数の member は制約の型から探す
            var cs = ConstraintsOf(rt);
            if (cs != null)
            {
                foreach (var c in cs)
                {
                    if (c.Decl != null && (c.Decl.Lookup(name) != null || HasInheritedMember(c.Decl, name)))
                    {
                        LookupMemberOn(e, recv, c, name, false);
                        return;
                    }
                }
            }
        }
        if (rt.Decl != null)
        {
            var members = rt.Decl.Lookup(name);
            if (members != null)
            {
                var first = members[0];
                if (first.Kind != SymKind.Method && first.Kind != SymKind.EnumMember && first.IsStatic != isStatic && !first.IsConst)
                {
                    if (isStatic) Error("CS0120", "An object reference is required for the non-static field, method, or property '" + rt.Decl.Name + "." + name + "'", e);
                    else Error("CS0176", "Member '" + rt.Decl.Name + "." + name + "' cannot be accessed with an instance reference; qualify it with a type name instead", e);
                }
                BindMemberRef(e, recv, rt.Decl, members, true, false);
                return;
            }
            if (isStatic)
            {
                foreach (var nested in rt.Decl.Nested)
                {
                    if (nested.Name == name)
                    {
                        e.Info = RefKind.Type;
                        e.Type = TypeSym.OfDecl(nested, TypeArgsOf(e));
                        return;
                    }
                }
            }
            if (!isStatic && name == "ToString")
            {
                var obj = Builtins.Lookup(TypeSym.ObjectType, name, false);
                if (obj != null && !rt.IsValueType || rt.Kind == TypeKind.Enum)
                {
                    var list = Builtins.Lookup(rt.Kind == TypeKind.Enum ? new TypeSym(TypeKind.Enum, "enum") : TypeSym.ObjectType, name, false)!;
                    e.Info = RefKind.MethodGroup;
                    e.Cands = list;
                    e.Type = TypeSym.MethodGroupType;
                    return;
                }
            }
            if (rt.Kind == TypeKind.Interface && !isStatic)
            {
                // 継承した interface の member
                foreach (var bi in rt.Decl.Interfaces)
                {
                    if (bi.Decl != null && (bi.Decl.Lookup(name) != null || HasInheritedMember(bi.Decl, name)))
                    {
                        LookupMemberOn(e, recv, bi, name, false);
                        return;
                    }
                }
            }
        }
        var builtins = Builtins.Lookup(rt, name, isStatic);
        if (builtins != null)
        {
            var first = builtins[0];
            if (first.Kind == SymKind.Method)
            {
                e.Info = RefKind.MethodGroup;
                e.Cands = builtins;
                e.Type = TypeSym.MethodGroupType;
                return;
            }
            e.Sym = first;
            e.Info = first.Kind == SymKind.Field ? RefKind.Field : RefKind.Property;
            e.Type = first.Type.Subst(rt.Args, null);
            if (first.IsConst && first.ConstVal != null)
            {
                e.HasConst = true;
                e.Const = first.ConstVal;
            }
            return;
        }
        // 拡張メソッド
        if (!isStatic && _c.Extensions.TryGetValue(name, out var exts))
        {
            e.Info = RefKind.MethodGroup;
            e.Cands = exts;
            e.Type = TypeSym.MethodGroupType;
            e.ExtGroup = true;
            return;
        }
        if (rt.Kind == TypeKind.String && name == "Length")
        {
            Error("TCS1002", "unsupported API: string.Length (use s.RuneCount() or new Utf8Bytes(s).Length from TinySystem.Text)", e);
            e.Type = TypeSym.ErrorType;
            return;
        }
        if (rt.Kind == TypeKind.StaticRef || rt.Kind == TypeKind.List || rt.Kind == TypeKind.Dict || rt.Kind == TypeKind.String || rt.Kind == TypeKind.Int || rt.Kind == TypeKind.Float || rt.Kind == TypeKind.Char)
        {
            Error("TCS1002", "unsupported API: " + rt.Display() + "." + name, e);
        }
        else Error("CS1061", "'" + rt.Display() + "' does not contain a definition for '" + name + "'", e);
        e.Info = RefKind.Error;
        e.Type = TypeSym.ErrorType;
    }

    // a?.b: 受け手が null なら null。値型の member は T? になる
    private void BindConditionalMember(Node e)
    {
        var recv = e.A!;
        var rt = recv.Type!;
        if (rt.IsError)
        {
            e.Type = TypeSym.ErrorType;
            return;
        }
        if (!rt.IsNullable)
        {
            Error("CS0023", "Operator '?' cannot be applied to operand of type '" + rt.Display() + "'", e);
        }
        var inner = rt.Kind == TypeKind.Nullable ? rt.Elem : rt;
        LookupMemberOn(e, recv, inner, e.Text, false);
        if (recv.CondChain) recv.CondTop = false;
        e.CondChain = true;
        e.CondTop = true;
        Lift(e);
    }

    // ?. の鎖の中の式の型を持ち上げる (値型は T?)
    private static void Lift(Node e)
    {
        var t = e.Type!;
        e.Type2 = t;
        // method group は値でない (Call の Info は CallKind なので比べない)
        if (e.Kind != NodeKind.Call && e.Info == RefKind.MethodGroup) return;
        if (t.IsValueType && t.Kind != TypeKind.Nullable && t.Kind != TypeKind.Void) e.Type = TypeSym.NullableOf(t);
    }

    // 受け手の型 (?. の鎖の中なら持ち上げる前の型)
    private static TypeSym RecvType(Node recv)
    {
        if (recv.CondChain && recv.Type2 != null) return recv.Type2;
        return recv.Type!;
    }

    // 受け手が ?. の鎖なら、この式も鎖に入れる
    private static void Chain(Node e, Node recv)
    {
        if (!recv.CondChain) return;
        recv.CondTop = false;
        e.CondChain = true;
        e.CondTop = true;
        Lift(e);
    }

    // ---- 呼び出し ----

    private void BindCall(Node e)
    {
        if (e.Text == "with")
        {
            // record の with: 複製してから member を書き換える
            BindExpr(e.A!, null);
            var wt = e.A!.Type!;
            if (!wt.IsError && (wt.Decl == null || !wt.Decl.IsRecord && !wt.Decl.IsStruct))
                Error("CS8858", "The receiver type '" + wt.Display() + "' is not a valid record type and is not a struct type", e);
            e.Type = wt;
            if (e.B != null && !wt.IsError) BindObjectInitializer(e.B, wt);
            return;
        }
        var callee = e.A!;
        if (callee.Kind == NodeKind.Member && callee.Text == "Invoke" && callee.List == null)
        {
            // d.Invoke(x) と d?.Invoke(x) は delegate の呼び出し
            BindExpr(callee.A!, null);
            if (callee.A!.Type!.Kind == TypeKind.Delegate)
            {
                e.Value = callee.Value;
                e.A = callee.A;
                callee = e.A;
            }
        }
        if (callee.Type == null) BindExpr(callee, null);
        // list.Count(pred): 呼べない member (property) は拡張メソッドを探す
        if (callee.Kind == NodeKind.Member && (callee.Info == RefKind.Field || callee.Info == RefKind.Property) && callee.Type != null && callee.Type.Kind != TypeKind.Delegate
            && _c.Extensions.TryGetValue(callee.Text, out var extCands))
        {
            callee.Info = RefKind.MethodGroup;
            callee.Cands = extCands;
            callee.Sym = null;
            callee.HasConst = false;
            callee.Type = TypeSym.MethodGroupType;
            callee.ExtGroup = true;
        }
        if (callee.Kind == NodeKind.Name && callee.Text == "nameof" && callee.Info == RefKind.MethodGroup && callee.Cands == null)
        {
            BindNameof(e);
            return;
        }
        var ct = callee.Type!;
        if (callee.Info == RefKind.MethodGroup && callee.Cands != null)
        {
            Node? receiver = callee.Kind == NodeKind.Member ? callee.A : null;
            bool conditional = callee.CondChain;
            TypeSym? recvType = receiver != null ? RecvType(receiver) : null;
            if (callee.Kind == NodeKind.Member && callee.Value == 1 && recvType != null && recvType.Kind == TypeKind.Nullable) recvType = recvType.Elem;
            bool isExt = callee.ExtGroup;
            var cands = callee.Cands;
            Symbol? m;
            if (isExt)
            {
                if (callee.List != null) _explicitTypeArgs = TypeArgsOf(callee);
                var extRecv = receiver!;
                if (extRecv.CondChain && extRecv.Type2 != null)
                {
                    // ?. の鎖の中の受け手は持ち上げる前の型で渡す
                    extRecv = MakeConv(extRecv, extRecv.Type2, ConvKind.Identity);
                }
                else if (callee.Value == 1 && extRecv.Type != null && extRecv.Type.Kind == TypeKind.Nullable)
                {
                    // x?.Ext(): 拡張メソッドには T? の中の値を渡す
                    var unwrapped = MakeConv(extRecv, extRecv.Type.Elem, ConvKind.Identity);
                    extRecv = unwrapped;
                }
                m = ResolveExtension(cands, e, extRecv, callee);
                e.Info = CallKind.Extension;
            }
            else
            {
                var explicitArgs = callee.List != null ? TypeArgsOf(callee) : null;
                _explicitTypeArgs = explicitArgs;
                int diagMark = _c.Diags.Items.Count;
                m = ResolveCall(cands, e, recvType, callee);
                if (m != null && m.Builtin != "") e.Info = CallKind.Builtin;
                else e.Info = CallKind.Method;
                // instance の method に当てはまるものが無ければ、拡張メソッドを探す (C# §12.8.10.3)
                if (m == null && receiver != null && receiver.Info != RefKind.Type && callee.Kind == NodeKind.Member && callee.Value != 1
                    && _c.Extensions.TryGetValue(callee.Text, out var fallback) && AnyVisibleExtension(fallback))
                {
                    _c.Diags.Truncate(diagMark);
                    callee.Cands = fallback;
                    callee.ExtGroup = true;
                    _explicitTypeArgs = explicitArgs;
                    m = ResolveExtension(fallback, e, receiver, callee);
                    e.Info = CallKind.Extension;
                    isExt = true;
                }
            }
            e.Sym = m;
            if (m == null)
            {
                e.Type = TypeSym.ErrorType;
                return;
            }
            if (m.Owner != null && !m.IsStatic && receiver == null && _static && !isExt)
            {
                Error("CS0120", "An object reference is required for the non-static field, method, or property '" + m.Owner.Name + "." + m.Name + "'", callee);
            }
            if (receiver != null && receiver.Info == RefKind.Type && !m.IsStatic && !isExt)
            {
                Error("CS0120", "An object reference is required for the non-static field, method, or property '" + m.Name + "'", callee);
            }
            if (receiver != null && receiver.Info != RefKind.Type && m.IsStatic && m.Builtin == "" && !isExt)
            {
                Error("CS0176", "Member '" + m.Name + "' cannot be accessed with an instance reference; qualify it with a type name instead", callee);
            }
            var ret = m.Type;
            ret = ret.Subst(recvType != null ? recvType.Args : null, e.TypeArgs);
            if (m.Builtin == "Delegate.Invoke" && recvType != null) ret = recvType.Ret!;
            e.Type = ret;
            if (conditional)
            {
                callee.CondTop = false;
                e.CondChain = true;
                e.CondTop = true;
                Lift(e);
            }
            return;
        }
        if (ct.Kind == TypeKind.Delegate)
        {
            e.Info = CallKind.Delegate;
            var ps = ct.Args!;
            int n = e.Count;
            if (n != ps.Count) Error("CS1593", "Delegate '" + ct.Display() + "' does not take " + n + " arguments", e);
            for (int i = 0; i < n; i++)
            {
                var arg = e.Item(i);
                if (i < ps.Count) arg.A = BindValue(arg.A!, ps[i]);
                else BindExpr(arg.A!, null);
                arg.Type = arg.A!.Type;
            }
            e.Type = ct.Ret;
            // d?.Invoke(): ?. の鎖 (値型の戻り値は T?)
            if (e.Value == 1)
            {
                if (callee.CondChain) callee.CondTop = false;
                e.CondChain = true;
                e.CondTop = true;
                Lift(e);
            }
            else Chain(e, callee);
            return;
        }
        if (callee.Info == RefKind.Namespace) Error("CS0234", "The type or namespace name '" + callee.Text + "' does not exist in the namespace", callee);
        else if (!ct.IsError) Error("CS0149", "Method name expected", callee);
        if (e.List != null)
        {
            foreach (var a in e.List)
            {
                if (a.A != null && a.A.Kind != NodeKind.PatDecl && a.A.Kind != NodeKind.PatDiscard) BindExpr(a.A, null);
            }
        }
        e.Type = TypeSym.ErrorType;
    }

    private void BindNameof(Node e)
    {
        if (e.Count != 1)
        {
            Error("CS1501", "No overload for method 'nameof' takes " + e.Count + " arguments", e);
            e.Type = TypeSym.ErrorType;
            return;
        }
        var arg = e.Item(0).A!;
        string name = arg.Kind == NodeKind.Member ? arg.Text : arg.Kind == NodeKind.Name ? arg.Text : "";
        if (name == "") Error("CS8081", "Expression does not have a name", arg);
        else
        {
            int save = _c.Diags.Items.Count;
            BindExpr(arg, null);
        }
        e.Info = CallKind.Nameof;
        e.Type = TypeSym.StringType;
        e.HasConst = true;
        e.Const = ConstValue.OfString(name);
    }

    // 引数の型が決まらない (target-typed な) 式か
    private static bool IsTargetTyped(Node a)
    {
        switch (a.Kind)
        {
            case NodeKind.Lambda:
            case NodeKind.NullLit:
            case NodeKind.CollectionExpr:
                return true;
            case NodeKind.DefaultLit:
                return a.A == null;
            case NodeKind.New:
                return a.A == null;
            case NodeKind.Paren:
                return IsTargetTyped(a.A!);
        }
        return false;
    }

    // 引数を先に束縛する (target-typed な式は後で)
    private void PreBindArgs(Node call)
    {
        if (call.List == null) return;
        foreach (var arg in call.List)
        {
            var a = arg.A!;
            if (arg.Has(Mods.Out))
            {
                if (a.Kind == NodeKind.PatDecl)
                {
                    bool isVar = a.A!.Kind == NodeKind.TypeName && a.A.Text == "var" && a.A.List == null && a.A.A == null;
                    a.Type = isVar ? null : _d.ResolveType(a.A, _scope!);
                }
                else if (a.Kind == NodeKind.PatDiscard)
                {
                    a.Type = null;
                }
                else
                {
                    BindWriteTarget(a);
                    CheckAssignable(a);
                    MarkAssigned(a);
                }
                continue;
            }
            if (arg.Has(Mods.Ref) || arg.Has(Mods.In)) Unsupported("ref / in argument", arg);
            if (!IsTargetTyped(a)) BindExpr(a, null);
        }
    }

    // 引数 a は parameter 型 p へ渡せるか
    private bool ArgApplicable(Node arg, TypeSym p, bool isOut)
    {
        var a = arg.A!;
        if (isOut != arg.Has(Mods.Out)) return false;
        if (isOut)
        {
            if (a.Kind == NodeKind.PatDiscard) return true;
            if (a.Kind == NodeKind.PatDecl) return a.Type == null || TypeSym.Same(a.Type, p) || p.ContainsTypeParam();
            return a.Type == null || TypeSym.Same(a.Type, p) || a.Type.IsError || p.ContainsTypeParam();
        }
        if (IsTargetTyped(a))
        {
            var k = a.Kind == NodeKind.Paren ? a.A! : a;
            switch (k.Kind)
            {
                case NodeKind.Lambda:
                    if (p.Kind == TypeKind.TypeParam) return true;
                    if (p.Kind != TypeKind.Delegate || p.Args!.Count != k.Count) return false;
                    return LambdaFits(k, p);
                case NodeKind.NullLit:
                    return p.IsNullable || p.Kind == TypeKind.TypeParam;
                case NodeKind.CollectionExpr:
                    return p.Kind == TypeKind.List || p.Kind == TypeKind.Array || p.Kind == TypeKind.HashSet || p.Kind == TypeKind.TypeParam;
                case NodeKind.DefaultLit:
                    return true;
                case NodeKind.New:
                    return p.Kind == TypeKind.Class || p.Kind == TypeKind.Struct || p.Kind == TypeKind.List || p.Kind == TypeKind.Dict || p.Kind == TypeKind.HashSet || p.Kind == TypeKind.Queue;
            }
        }
        var t = a.Type!;
        if (t.Kind == TypeKind.MethodGroup)
        {
            if (p.Kind == TypeKind.TypeParam) return true;
            if (p.Kind != TypeKind.Delegate) return false;
            return a.Cands == null || FindMethodForDelegate(a.Cands, p) != null || p.ContainsTypeParam();
        }
        if (p.ContainsTypeParam()) return true;
        return ImplicitKind(a, t, p) >= 0;
    }

    // overload の解決。call の引数 (Arg) を束縛し、選んだ method の parameter 型へ変換する
    // 呼び出しに明示した型引数 (M<int>(x))。ResolveCall の間だけ有効
    private List<TypeSym>? _explicitTypeArgs;

    private Symbol? ResolveCall(List<Symbol> cands, Node call, TypeSym? recvType, Node at)
    {
        var explicitArgs = _explicitTypeArgs;
        _explicitTypeArgs = null;
        PreBindArgs(call);
        int n = call.Count;
        var applicable = new List<Symbol>();
        var inferred = new List<List<TypeSym>?>();
        foreach (var m in cands)
        {
            if (m.Kind != SymKind.Method && m.Kind != SymKind.Ctor) continue;
            if (n > m.ParamCount || n < m.RequiredParamCount) continue;
            List<TypeSym>? margs = null;
            if (explicitArgs != null)
            {
                if (m.TypeParams == null || m.TypeParams.Count != explicitArgs.Count) continue;
                margs = explicitArgs;
            }
            else if (m.TypeParams != null || HasMethodTypeParam(m))
            {
                margs = InferTypeArgs(m, call, recvType);
                if (margs == null) continue;
            }
            bool ok = true;
            for (int i = 0; i < n; i++)
            {
                var p = m.Params![i];
                var pt = p.Type.Subst(recvType != null ? recvType.Args : null, margs);
                if (!ArgApplicable(call.Item(i), pt, p.IsOut))
                {
                    ok = false;
                    break;
                }
            }
            if (!ok) continue;
            applicable.Add(m);
            inferred.Add(margs);
        }
        if (applicable.Count == 0)
        {
            string name = cands.Count > 0 ? cands[0].Name : "?";
            if (cands.Count > 0 && cands[0].Kind == SymKind.Ctor) name = cands[0].Owner != null ? cands[0].Owner!.Name : "?";
            bool anyArity = false;
            foreach (var m in cands)
            {
                if (n <= m.ParamCount && n >= m.RequiredParamCount) anyArity = true;
            }
            if (!anyArity) Error("CS1501", "No overload for method '" + name + "' takes " + n + " arguments", at);
            else Error("CS1503", "Argument type mismatch for '" + name + "'", at);
            BindRemainingArgs(call);
            return null;
        }
        int best = 0;
        for (int i = 1; i < applicable.Count; i++)
        {
            int cmp = Better(applicable[i], applicable[best], call, recvType, inferred[i], inferred[best]);
            if (cmp > 0) best = i;
        }
        // 曖昧さの検査
        for (int i = 0; i < applicable.Count; i++)
        {
            if (i == best) continue;
            if (Better(applicable[best], applicable[i], call, recvType, inferred[best], inferred[i]) <= 0)
            {
                Error("CS0121", "The call is ambiguous between the following methods or properties: '" + applicable[best].Name + "'", at);
                break;
            }
        }
        var chosen = applicable[best];
        call.TypeArgs = inferred[best];
        ApplyArgs(chosen, call, recvType, inferred[best]);
        return chosen;
    }

    private static bool HasMethodTypeParam(Symbol m)
    {
        if (m.Params == null) return false;
        foreach (var p in m.Params)
        {
            if (ContainsMethodParam(p.Type)) return true;
        }
        return ContainsMethodParam(m.Type);
    }

    private static bool ContainsMethodParam(TypeSym t)
    {
        if (t.Kind == TypeKind.TypeParam) return t.IsMethodParam;
        if (t.Args != null)
        {
            foreach (var a in t.Args)
            {
                if (ContainsMethodParam(a)) return true;
            }
        }
        if (t.Ret != null) return ContainsMethodParam(t.Ret);
        return false;
    }

    private void BindRemainingArgs(Node call)
    {
        if (call.List == null) return;
        foreach (var arg in call.List)
        {
            var a = arg.A!;
            if (a.Type == null && a.Kind != NodeKind.PatDecl && a.Kind != NodeKind.PatDiscard && a.Kind != NodeKind.Lambda)
            {
                BindExpr(a, null);
            }
            if (a.Kind == NodeKind.PatDecl)
            {
                var l = new LocalSym(a.Text, a.Type ?? TypeSym.ErrorType);
                a.Local = l;
                DeclareExprLocal(l, a);
            }
        }
    }

    // 1 なら a が b より良い、-1 なら悪い、0 は決まらない
    private int Better(Symbol a, Symbol b, Node call, TypeSym? recvType, List<TypeSym>? ia, List<TypeSym>? ib)
    {
        int n = call.Count;
        bool aBetter = false;
        bool bBetter = false;
        for (int i = 0; i < n; i++)
        {
            var arg = call.Item(i).A!;
            var pa = a.Params![i].Type.Subst(recvType != null ? recvType.Args : null, ia);
            var pb = b.Params![i].Type.Subst(recvType != null ? recvType.Args : null, ib);
            if (TypeSym.Same(pa, pb)) continue;
            int c = BetterConversion(arg, pa, pb);
            if (c > 0) aBetter = true;
            else if (c < 0) bBetter = true;
        }
        if (aBetter && !bBetter) return 1;
        if (bBetter && !aBetter) return -1;
        if (aBetter && bBetter) return 0;
        // 非 generic が良い
        bool ag = ia != null;
        bool bg = ib != null;
        if (!ag && bg) return 1;
        if (ag && !bg) return -1;
        // 宣言の parameter 型が型引数でない方が具体的 (G<string> の Pick(T) と Pick(string))
        int ta = 0;
        int tb = 0;
        for (int i = 0; i < n; i++)
        {
            if (a.Params![i].Type.ContainsTypeParam()) ta++;
            if (b.Params![i].Type.ContainsTypeParam()) tb++;
        }
        if (ta < tb) return 1;
        if (tb < ta) return -1;
        // 省略引数を使わない方が良い
        bool aDefaults = a.ParamCount > n;
        bool bDefaults = b.ParamCount > n;
        if (!aDefaults && bDefaults) return 1;
        if (aDefaults && !bDefaults) return -1;
        return 0;
    }

    private int BetterConversion(Node arg, TypeSym t1, TypeSym t2)
    {
        var s = arg.Type;
        if (s != null && !IsTargetTyped(arg))
        {
            if (TypeSym.Same(s, t1)) return 1;
            if (TypeSym.Same(s, t2)) return -1;
        }
        if (arg.Kind == NodeKind.Lambda && t1.Kind == TypeKind.Delegate && t2.Kind == TypeKind.Delegate)
        {
            // lambda の推論した戻り型 X: Func<.., X> が良く、そうでなければ戻り型どうしの良い変換
            if (t1.Ret == null || t2.Ret == null || TypeSym.Same(t1.Ret, t2.Ret)) return 0;
            var x = LambdaReturnFor(arg, t1);
            if (x != null)
            {
                bool s1 = TypeSym.Same(x, t1.Ret);
                bool s2 = TypeSym.Same(x, t2.Ret);
                if (s1 && !s2) return 1;
                if (s2 && !s1) return -1;
            }
            if (t1.Ret.Kind == TypeKind.Void || t2.Ret.Kind == TypeKind.Void) return 0;
            bool r12 = IsImplicitlyConvertible(t1.Ret, t2.Ret);
            bool r21 = IsImplicitlyConvertible(t2.Ret, t1.Ret);
            if (r12 && !r21) return 1;
            if (r21 && !r12) return -1;
            return 0;
        }
        bool t1to2 = IsImplicitlyConvertible(t1, t2);
        bool t2to1 = IsImplicitlyConvertible(t2, t1);
        if (t1to2 && !t2to1) return 1;
        if (t2to1 && !t1to2) return -1;
        return 0;
    }

    // method の型引数を引数から推論する。推論できなければ null
    private List<TypeSym>? InferTypeArgs(Symbol m, Node call, TypeSym? recvType)
    {
        int count = m.TypeParams != null ? m.TypeParams.Count : 1;
        if (m.TypeParams == null)
        {
            // 組み込みの generic method (string.Join<T>)
            count = 1;
        }
        var result = new List<TypeSym?>();
        for (int i = 0; i < count; i++) result.Add(null);
        int n = call.Count;
        // 1 段目: lambda 以外の引数
        for (int i = 0; i < n && i < m.ParamCount; i++)
        {
            var a = call.Item(i).A!;
            if (IsTargetTyped(a) || a.Type == null) continue;
            if (call.Item(i).Has(Mods.Out)) continue;
            var pt = m.Params![i].Type.Subst(recvType != null ? recvType.Args : null, null);
            Unify(pt, a.Type, result);
        }
        // 2 段目: lambda と method group の戻り型
        for (int i = 0; i < n && i < m.ParamCount; i++)
        {
            var a = call.Item(i).A!;
            if (a.Type != null && a.Type.Kind == TypeKind.MethodGroup && a.Cands != null)
            {
                var mpt = m.Params![i].Type.Subst(recvType != null ? recvType.Args : null, null);
                if (mpt.Kind != TypeKind.Delegate || mpt.Ret == null || !ContainsMethodParam(mpt.Ret)) continue;
                var ps2 = new List<TypeSym>();
                bool ready2 = true;
                foreach (var pa in mpt.Args!)
                {
                    var s2 = SubstPartial(pa, result);
                    if (s2 == null)
                    {
                        ready2 = false;
                        break;
                    }
                    ps2.Add(s2);
                }
                if (!ready2) continue;
                foreach (var cm in a.Cands)
                {
                    if (cm.ParamCount != ps2.Count || cm.TypeParams != null) continue;
                    bool same = true;
                    for (int k = 0; k < ps2.Count; k++)
                    {
                        if (!TypeSym.Same(cm.Params![k].Type, ps2[k])) same = false;
                    }
                    if (!same) continue;
                    Unify(mpt.Ret, cm.Type, result);
                    break;
                }
                continue;
            }
            if (a.Kind != NodeKind.Lambda) continue;
            var pt = m.Params![i].Type.Subst(recvType != null ? recvType.Args : null, null);
            if (pt.Kind != TypeKind.Delegate || pt.Ret == null) continue;
            if (!ContainsMethodParam(pt.Ret)) continue;
            // parameter 型が決まっているか
            bool ready = true;
            var ps = new List<TypeSym>();
            foreach (var pa in pt.Args!)
            {
                var s = SubstPartial(pa, result);
                if (s == null)
                {
                    ready = false;
                    break;
                }
                ps.Add(s);
            }
            if (!ready || ps.Count != a.Count) continue;
            var retType = InferLambdaReturn(a, ps);
            if (retType != null) Unify(pt.Ret, retType, result);
        }
        var final = new List<TypeSym>();
        foreach (var r in result)
        {
            if (r == null) return null;
            final.Add(r);
        }
        return final;
    }

    private TypeSym? SubstPartial(TypeSym t, List<TypeSym?> inferred)
    {
        if (t.Kind == TypeKind.TypeParam && t.IsMethodParam)
        {
            if (t.Index < inferred.Count) return inferred[t.Index];
            return null;
        }
        if (!ContainsMethodParam(t)) return t;
        var args = new List<TypeSym>();
        foreach (var r in inferred)
        {
            if (r == null) return null;
            args.Add(r);
        }
        return t.Subst(null, args);
    }

    private static void Unify(TypeSym p, TypeSym a, List<TypeSym?> result)
    {
        if (p.Kind == TypeKind.TypeParam && p.IsMethodParam)
        {
            if (p.Index < result.Count && result[p.Index] == null && a.Kind != TypeKind.Null) result[p.Index] = a;
            return;
        }
        if (p.Args != null && a.Args != null && p.Kind == a.Kind && p.Args.Count == a.Args.Count)
        {
            for (int i = 0; i < p.Args.Count; i++) Unify(p.Args[i], a.Args[i], result);
        }
        if (p.Ret != null && a.Ret != null) Unify(p.Ret, a.Ret, result);
    }

    // lambda を delegate の parameter 型で束縛したときの戻り型 (void は VoidType、分からなければ null)
    private TypeSym? LambdaReturnFor(Node lambda, TypeSym del)
    {
        if (del.Args == null || del.Args.Count != lambda.Count) return null;
        foreach (var a in del.Args)
        {
            if (a.ContainsTypeParam()) return null;
        }
        return InferLambdaReturn(lambda, del.Args);
    }

    // lambda をこの delegate に変換できるか (戻り値の型が合うか)
    private bool LambdaFits(Node lambda, TypeSym del)
    {
        if (del.Ret == null || del.Ret.ContainsTypeParam()) return true;
        var x = LambdaReturnFor(lambda, del);
        if (x == null || x.IsError) return true;
        if (del.Ret.Kind == TypeKind.Void)
        {
            // 式本体は式文になれる式だけ (呼び出しなど)。ブロックは return に値が無いこと
            if (lambda.A != null) return IsStatementExpression(lambda.A);
            return x.Kind == TypeKind.Void;
        }
        if (x.Kind == TypeKind.Void) return false;
        if (x.Kind == TypeKind.Null) return del.Ret.IsNullable;
        return ImplicitKind(lambda.A != null && lambda.A.HasConst ? lambda.A : null, x, del.Ret) >= 0;
    }

    private static bool IsStatementExpression(Node e)
    {
        switch (e.Kind)
        {
            case NodeKind.Assign:
            case NodeKind.Call:
            case NodeKind.PostIncDec:
            case NodeKind.PreIncDec:
            case NodeKind.New:
                return true;
        }
        return false;
    }

    // lambda の本体を試しに束縛して戻り型を求める (診断と束縛の結果は捨てる)
    private TypeSym? InferLambdaReturn(Node lambda, List<TypeSym> ps)
    {
        int save = _c.Diags.Items.Count;
        var del = TypeSym.Delegate(ps, TypeSym.ErrorType);
        var t = BindLambdaInfer(lambda, del, true);
        _c.Diags.Truncate(save);
        return t;
    }

    private Symbol? ResolveExtension(List<Symbol> cands, Node call, Node receiver, Node at)
    {
        // receiver を最初の引数として解決する
        var recvArg = new Node(NodeKind.Arg, receiver.File, receiver.Pos);
        recvArg.A = receiver;
        var args = new List<Node>();
        args.Add(recvArg);
        if (call.List != null)
        {
            foreach (var a in call.List) args.Add(a);
        }
        var temp = new Node(NodeKind.Call, call.File, call.Pos);
        temp.List = args;
        var visible = new List<Symbol>();
        foreach (var c in cands)
        {
            if (c.Owner != null && IsNamespaceVisible(c.Owner.Namespace)) visible.Add(c);
        }
        visible = InnermostExtensions(visible, receiver.Type);
        if (visible.Count == 0)
        {
            Error("CS1061", "'" + receiver.Type!.Display() + "' does not contain a definition for '" + cands[0].Name + "'", at);
            BindRemainingArgs(call);
            return null;
        }
        var m = ResolveCall(visible, temp, null, at);
        call.TypeArgs = temp.TypeArgs;
        // receiver の変換を戻す
        call.B = recvArg.A;
        return m;
    }

    private bool AnyVisibleExtension(List<Symbol> cands)
    {
        foreach (var c in cands)
        {
            if (c.Owner != null && IsNamespaceVisible(c.Owner.Namespace)) return true;
        }
        return false;
    }

    // C# は拡張メソッドを内側の namespace の scope から順に探し、受け手を受け取れる候補のある最初の集合で止まる。
    // 1 つの scope では、その namespace で宣言した型の集合を先に、その namespace に書いた using が取り込む型の集合を後に見る。
    // 受け手以外の引数は見ないので、内側の集合の候補が引数で合わないときは (C# なら外側に進むところを) 誤りにする
    private List<Symbol> InnermostExtensions(List<Symbol> visible, TypeSym? recv)
    {
        if (visible.Count < 2 || recv == null) return visible;
        string level = _scope!.Namespace;
        while (true)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                var group = new List<Symbol>();
                bool accepts = false;
                foreach (var c in visible)
                {
                    string ns = c.Owner!.Namespace;
                    if (pass == 0 ? ns != level : !ImportedAt(ns, level)) continue;
                    group.Add(c);
                    if (c.Params == null || c.Params.Count == 0) continue;
                    var pt = c.Params[0].Type;
                    if (pt.ContainsTypeParam() || IsImplicitlyConvertible(recv, pt)) accepts = true;
                }
                if (accepts) return group;
            }
            if (level == "") break;
            level = Declarer.ParentNamespace(level);
        }
        return visible;
    }

    // level の namespace (compilation unit は "") に書いた using が ns を取り込むか
    private bool ImportedAt(string ns, string level)
    {
        var levels = _scope!.UsingLevels;
        for (int i = 0; i < _scope.Usings.Count; i++)
        {
            string at = levels != null && i < levels.Count ? levels[i] : "";
            if (at == level && _scope.Usings[i] == ns) return true;
        }
        return false;
    }

    private bool IsNamespaceVisible(string ns)
    {
        if (ns == "") return true;
        string cur = _scope!.Namespace;
        while (true)
        {
            if (cur == ns) return true;
            if (cur == "") break;
            cur = Declarer.ParentNamespace(cur);
        }
        foreach (var u in _scope.Usings)
        {
            if (u == ns) return true;
        }
        return false;
    }

    // 選んだ method の parameter へ引数を変換する
    private void ApplyArgs(Symbol m, Node call, TypeSym? recvType, List<TypeSym>? margs)
    {
        int n = call.Count;
        for (int i = 0; i < n; i++)
        {
            var arg = call.Item(i);
            var p = m.Params![i];
            var pt = p.Type.Subst(recvType != null ? recvType.Args : null, margs);
            var a = arg.A!;
            if (p.IsOut)
            {
                if (a.Kind == NodeKind.PatDecl)
                {
                    var lt = a.Type ?? pt;
                    var l = new LocalSym(a.Text, lt);
                    a.Local = l;
                    a.Type = lt;
                    DeclareExprLocal(l, a);
                }
                else if (a.Kind == NodeKind.PatDiscard)
                {
                    a.Type = pt;
                }
                arg.Type = pt;
                continue;
            }
            arg.A = BindOrCoerce(a, pt);
            arg.Type = pt;
        }
    }

    private void CheckAssignable(Node e)
    {
        switch (e.Kind)
        {
            case NodeKind.Name:
            case NodeKind.Member:
                if (e.Info == RefKind.Local)
                {
                    if (e.Local!.IsConst) Error("CS0131", "The left-hand side of an assignment must be a variable, property or indexer", e);
                    return;
                }
                if ((e.Info == RefKind.Field || e.Info == RefKind.Property) && e.Kind == NodeKind.Member && e.Sym != null && !e.Sym.IsStatic
                    && e.Sym.Owner != null && e.Sym.Owner.IsStruct && !IsValueVariable(e.A!))
                {
                    // property や method が返した struct の値の member は書き換えられない
                    Error("CS1612", "Cannot modify the return value of '" + ValueSourceName(e.A!) + "' because it is not a variable", e);
                    return;
                }
                if (e.Info == RefKind.Field)
                {
                    var f = e.Sym!;
                    if (f.IsConst || f.Builtin != "") Error("CS0131", "The left-hand side of an assignment must be a variable, property or indexer", e);
                    else if (f.IsReadonly && !(_member != null && (_member.Kind == SymKind.Ctor || _member == f || _inInitAccessor) && f.Owner == _type))
                        Error("CS0191", "A readonly field cannot be assigned to (except in a constructor or init-only setter of the type in which the field is defined or a variable initializer)", e);
                    return;
                }
                if (e.Info == RefKind.Property)
                {
                    var p = e.Sym!;
                    bool initContext = _member != null && (_member.Kind == SymKind.Ctor || _inInitAccessor) && p.Owner == _type;
                    if (!p.HasSetter && !((p.IsAuto || p.IsInit) && initContext))
                        Error("CS0200", "Property or indexer '" + p.Name + "' cannot be assigned to -- it is read only", e);
                    return;
                }
                if (e.Info == RefKind.Error) return;
                break;
            case NodeKind.Index:
                if (e.A!.Type != null && e.A.Type.Kind == TypeKind.Utf8Bytes) break;
                return;
            case NodeKind.Paren:
                CheckAssignable(e.A!);
                return;
        }
        if (e.Type != null && e.Type.IsError) return;
        Error("CS0131", "The left-hand side of an assignment must be a variable, property or indexer", e);
    }

    // delegate の signature に合う method (parameter は同じ型、戻り値は同じか参照の変換)
    private Symbol? FindMethodForDelegate(List<Symbol> cands, TypeSym del)
    {
        foreach (var m in cands)
        {
            if (m.ParamCount != del.Args!.Count || m.TypeParams != null) continue;
            bool ok = true;
            for (int i = 0; i < m.ParamCount; i++)
            {
                if (!TypeSym.Same(m.Params![i].Type, del.Args[i]) || m.Params[i].IsOut) ok = false;
            }
            if (!ok) continue;
            if (!TypeSym.Same(m.Type, del.Ret!) && !(del.Ret!.Kind == TypeKind.Void) && !(m.Type.IsReferenceLike && IsImplicitlyConvertible(m.Type, del.Ret))) continue;
            return m;
        }
        return null;
    }

    // method group を delegate へ変換する
    private Node ConvertMethodGroup(Node e, TypeSym del)
    {
        var cands = e.Cands;
        if (cands == null)
        {
            Error("CS0428", "Cannot convert method group to non-delegate type", e);
            return e;
        }
        Symbol? found = FindMethodForDelegate(cands, del);
        if (found == null && e.List != null)
        {
            // Pick<int> のように型引数を明示した generic な method
            var targs = TypeArgsOf(e)!;
            foreach (var m in cands)
            {
                if (m.TypeParams == null || m.TypeParams.Count != targs.Count || m.ParamCount != del.Args!.Count) continue;
                bool ok = true;
                for (int i = 0; i < m.ParamCount; i++)
                {
                    if (!TypeSym.Same(m.Params![i].Type.Subst(null, targs), del.Args[i])) ok = false;
                }
                if (ok && (TypeSym.Same(m.Type.Subst(null, targs), del.Ret!) || del.Ret!.Kind == TypeKind.Void))
                {
                    found = m;
                    e.TypeArgs = targs;
                    break;
                }
            }
        }
        if (found == null)
        {
            Error("CS0123", "No overload for '" + cands[0].Name + "' matches delegate '" + del.Display() + "'", e);
            return e;
        }
        if (found.Builtin != "") SelfHostUnsupported("method group of a library method", e);
        e.Sym = found;
        var c = MakeConv(e, del, ConvKind.MethodGroup);
        return c;
    }

    // ---- lambda ----

    private void BindLambda(Node e, TypeSym del)
    {
        BindLambdaInfer(e, del, false);
        e.Type = del;
    }

    // del の Ret が ErrorType で infer なら戻り型を推論して返す
    private TypeSym? BindLambdaInfer(Node e, TypeSym del, bool infer)
    {
        var ps = del.Args!;
        if (e.Count != ps.Count)
        {
            Error("CS1593", "Delegate '" + del.Display() + "' does not take " + e.Count + " arguments", e);
            return null;
        }
        var saveLambda = _lambda;
        var saveStmt = _stmt;
        var saveDepth = _depth;
        var frame = new LambdaFrame();
        frame.Ret = infer ? null : del.Ret;
        frame.Infer = infer;
        _lambda = frame;
        _depth = 0;
        PushScope();
        _locals!.IsLambda = true;
        for (int i = 0; i < e.Count; i++)
        {
            var p = e.Item(i);
            var pt = ps[i];
            if (p.A != null)
            {
                var declared = _d.ResolveType(p.A, _scope!);
                if (!TypeSym.Same(declared, pt) && !pt.IsError && !infer) Error("CS1678", "Parameter " + (i + 1) + " is declared as type '" + declared.Display() + "' but should be '" + pt.Display() + "'", p);
                pt = declared;
            }
            var l = new LocalSym(p.Text, pt);
            l.IsParam = true;
            p.Local = l;
            DeclareLocal(l, p);
        }
        TypeSym? result = null;
        if (e.A != null)
        {
            _stmt = null;
            if (infer)
            {
                BindExpr(e.A, null);
                result = e.A.Type;
            }
            else if (del.Ret!.Kind == TypeKind.Void)
            {
                e.A = BindExprStatementExpr(e.A);
            }
            else
            {
                e.A = BindValue(e.A, del.Ret);
            }
        }
        else if (e.B != null)
        {
            _stmt = null;
            BindBlock(e.B);
            if (!infer && del.Ret != null && del.Ret.Kind != TypeKind.Void && !del.Ret.IsError && CanComplete(e.B))
                Error("CS1643", "Not all code paths return a value in lambda expression of type '" + del.Display() + "'", e);
            if (infer)
            {
                foreach (var r in frame.Returns)
                {
                    if (r.A != null && r.A.Type != null && r.A.Type.Kind != TypeKind.Null)
                    {
                        result = r.A.Type;
                        break;
                    }
                }
                if (result == null && frame.Returns.Count == 0) result = TypeSym.VoidType;
            }
        }
        PopScope();
        _lambda = saveLambda;
        _stmt = saveStmt;
        _depth = saveDepth;
        return result;
    }

    // ---- 添字 ----

    private void BindIndex(Node e)
    {
        BindExpr(e.A!, null);
        var rt = RecvType(e.A!);
        if (e.Value == 1)
        {
            if (!rt.IsNullable && !rt.IsError) Error("CS0023", "Operator '?' cannot be applied to operand of type '" + rt.Display() + "'", e);
            if (rt.Kind == TypeKind.Nullable) rt = rt.Elem;
        }
        if (e.Count != 1)
        {
            Error("CS0022", "Wrong number of indices inside []", e);
            e.Type = TypeSym.ErrorType;
            return;
        }
        var idx = e.Item(0);
        switch (rt.Kind)
        {
            case TypeKind.List:
            case TypeKind.Array:
                e.List![0] = BindValue(idx, TypeSym.IntType);
                e.Type = rt.Elem;
                IndexChain(e);
                return;
            case TypeKind.Dict:
                e.List![0] = BindValue(idx, rt.Arg(0));
                e.Type = rt.Arg(1);
                IndexChain(e);
                return;
            case TypeKind.Utf8Bytes:
                e.List![0] = BindValue(idx, TypeSym.IntType);
                e.Type = TypeSym.ByteType;
                IndexChain(e);
                return;
            case TypeKind.String:
                Error("TCS1002", "unsupported API: string indexer (use new Utf8Bytes(s)[i])", e);
                BindExpr(idx, null);
                e.Type = TypeSym.ErrorType;
                return;
            case TypeKind.Error:
                BindExpr(idx, null);
                e.Type = TypeSym.ErrorType;
                return;
        }
        Error("CS0021", "Cannot apply indexing with [] to an expression of type '" + rt.Display() + "'", e);
        BindExpr(idx, null);
        e.Type = TypeSym.ErrorType;
    }

    private static void IndexChain(Node e)
    {
        if (e.Value == 1)
        {
            if (e.A!.CondChain) e.A.CondTop = false;
            e.CondChain = true;
            e.CondTop = true;
            Lift(e);
        }
        else Chain(e, e.A!);
    }

    // ---- new ----

    private void BindNew(Node e, TypeSym? expected)
    {
        TypeSym t;
        if (e.A != null) t = _d.ResolveType(e.A, _scope!);
        else if (expected != null) t = expected;
        else
        {
            Error("CS8754", "There is no target type for 'new()'", e);
            t = TypeSym.ErrorType;
        }
        if (t.Kind == TypeKind.Nullable) t = t.Elem;
        e.Type = t;
        int n = e.Count;
        switch (t.Kind)
        {
            case TypeKind.Class:
            case TypeKind.Struct:
                {
                    var decl = t.Decl!;
                    if (decl.IsStatic) Error("CS0712", "Cannot create an instance of the static class '" + decl.Name + "'", e);
                    if (decl.IsInterface) Error("CS0144", "Cannot create an instance of the abstract type or interface '" + decl.Name + "'", e);
                    e.Info = t.Kind == TypeKind.Struct ? NewKind.Struct : NewKind.Class;
                    if (decl.Ctors.Count == 0)
                    {
                        if (n > 0) Error("CS1729", "'" + decl.Name + "' does not contain a constructor that takes " + n + " arguments", e);
                        PreBindArgs(e);
                    }
                    else
                    {
                        if (n == 0 && t.Kind == TypeKind.Struct)
                        {
                            // 引数なしの struct は既定値 (引数なしの constructor があればそれ)
                            Symbol? zero = null;
                            foreach (var c in decl.Ctors)
                            {
                                if (c.ParamCount == 0) zero = c;
                            }
                            e.Sym = zero;
                        }
                        else e.Sym = ResolveCall(decl.Ctors, e, t, e);
                    }
                    break;
                }
            case TypeKind.List:
                if (n == 0) e.Info = NewKind.List;
                else if (n == 1)
                {
                    BindExpr(e.Item(0).A!, null);
                    var st = e.Item(0).A!.Type!;
                    if (st.Kind == TypeKind.Int)
                    {
                        Error("TCS1002", "unsupported API: List<T>(int capacity)", e);
                    }
                    else if (st.Kind == TypeKind.List || st.Kind == TypeKind.Array || st.Kind == TypeKind.KeyCollection || st.Kind == TypeKind.ValueCollection)
                    {
                        var se = st.Kind == TypeKind.KeyCollection ? st.Arg(0) : st.Kind == TypeKind.ValueCollection ? st.Arg(1) : st.Elem;
                        if (!TypeSym.Same(se, t.Elem)) Error("CS1503", "Argument 1: cannot convert from '" + st.Display() + "' to 'List<" + t.Elem.Display() + ">'", e);
                        e.Info = NewKind.ListCopy;
                    }
                    else if (!st.IsError) Error("CS1503", "Argument 1: cannot convert from '" + st.Display() + "'", e);
                }
                else Error("CS1729", "'List<T>' does not contain a constructor that takes " + n + " arguments", e);
                break;
            case TypeKind.Dict:
                e.Info = NewKind.Dict;
                if (n != 0) SelfHostUnsupported("Dictionary constructor arguments", e);
                break;
            case TypeKind.HashSet:
                e.Info = NewKind.HashSet;
                if (n != 0) SelfHostUnsupported("HashSet constructor arguments", e);
                break;
            case TypeKind.Queue:
                e.Info = NewKind.Queue;
                if (n != 0) Error("TCS1002", "unsupported API: Queue<T> constructor arguments", e);
                break;
            case TypeKind.Utf8Bytes:
                e.Info = NewKind.Utf8Bytes;
                if (n == 0) e.Value = 2;
                else if (n != 1) Error("CS1729", "'Utf8Bytes' does not contain a constructor that takes " + n + " arguments", e);
                else e.Item(0).A = BindValue(e.Item(0).A!, TypeSym.StringType);
                break;
            case TypeKind.Random:
                e.Info = NewKind.Random;
                if (n > 1) Error("CS1729", "'Random' does not contain a constructor that takes " + n + " arguments", e);
                else if (n == 1) e.Item(0).A = BindValue(e.Item(0).A!, TypeSym.IntType);
                break;
            case TypeKind.Error:
                PreBindArgs(e);
                break;
            case TypeKind.TypeParam:
                // new T() (where T : new())
                e.Info = NewKind.TypeParam;
                if (n != 0) Error("CS0417", "'" + t.Name + "': cannot provide arguments when creating an instance of a variable type", e);
                break;
            default:
                {
                    // 数値、bool、char、enum の new T() は既定値 (default(T) と同じ)
                    var zero = n == 0 && e.B == null && !t.IsNullable ? DefaultConst(t) : null;
                    if (zero != null)
                    {
                        e.Kind = NodeKind.DefaultLit;
                        e.A = null;
                        e.List = null;
                        e.Type = t;
                        e.HasConst = true;
                        e.Const = zero;
                        return;
                    }
                    Error("CS0143", "The type '" + t.Display() + "' has no constructors defined", e);
                    break;
                }
        }
        if (e.B != null) BindObjectInitializer(e.B, t);
    }

    private void BindObjectInitializer(Node init, TypeSym t)
    {
        if (init.Kind == NodeKind.ObjectInit)
        {
            // with { } と new T { } は member を持たない
            if (init.List == null) return;
            foreach (var item in init.List)
            {
                if (item.Kind == NodeKind.MemberInit)
                {
                    Symbol? member = null;
                    if (t.Decl != null)
                    {
                        var list = t.Decl.Lookup(item.Text);
                        if (list != null) member = list[0];
                    }
                    if (member == null || member.Kind != SymKind.Field && member.Kind != SymKind.Property)
                    {
                        if (!t.IsError) Error("CS0117", "'" + t.Display() + "' does not contain a definition for '" + item.Text + "'", item);
                        BindExpr(item.A!, null);
                        continue;
                    }
                    if (member.IsStatic) Error("CS1914", "Static field or property '" + member.Name + "' cannot be assigned in an object initializer", item);
                    bool nestedInit = item.A!.Kind == NodeKind.ObjectInit || item.A.Kind == NodeKind.CollectionInit;
                    if (member.Kind == SymKind.Property && !member.HasSetter && !member.IsInit && !nestedInit) Error("CS0200", "Property or indexer '" + member.Name + "' cannot be assigned to -- it is read only", item);
                    item.Sym = member;
                    var mt = member.Type.Subst(t.Args, null);
                    item.Type = mt;
                    if (item.A!.Kind == NodeKind.ObjectInit || item.A.Kind == NodeKind.CollectionInit)
                    {
                        // 入れ子の初期化子: 既にある member の object / collection に対して初期化する
                        item.Value = 1;
                        BindObjectInitializer(item.A, mt);
                    }
                    else item.A = BindValue(item.A, mt);
                }
                else
                {
                    // [k] = v
                    if (t.Kind != TypeKind.Dict)
                    {
                        SelfHostUnsupported("index initializer", item);
                        continue;
                    }
                    item.A = BindValue(item.A!, t.Arg(0));
                    item.B = BindValue(item.B!, t.Arg(1));
                }
            }
            if (t.Kind == TypeKind.Dict || t.Kind == TypeKind.List) init.Info = 1;
            return;
        }
        // collection の初期化子
        if (init.List == null) return;
        for (int i = 0; i < init.List.Count; i++)
        {
            var item = init.List[i];
            if (t.Kind == TypeKind.List || t.Kind == TypeKind.HashSet || t.Kind == TypeKind.Queue)
            {
                if (item.Kind == NodeKind.ArrayInit)
                {
                    Error("CS1061", "Invalid collection initializer element", item);
                    continue;
                }
                init.List[i] = BindValue(item, t.Elem);
                if (t.Kind == TypeKind.Queue) SelfHostUnsupported("Queue initializer", item);
            }
            else if (t.Kind == TypeKind.Dict)
            {
                if (item.Kind != NodeKind.ArrayInit || item.Count != 2)
                {
                    Error("CS7036", "Dictionary initializer element must be { key, value }", item);
                    continue;
                }
                item.List![0] = BindValue(item.Item(0), t.Arg(0));
                item.List[1] = BindValue(item.Item(1), t.Arg(1));
            }
            else if (!t.IsError)
            {
                Error("CS1922", "Cannot initialize type '" + t.Display() + "' with a collection initializer", init);
                break;
            }
        }
    }

    private void BindNewArray(Node e, TypeSym? expected)
    {
        TypeSym elem;
        if (e.A != null) elem = _d.ResolveType(e.A, _scope!);
        else if (expected != null && expected.Kind == TypeKind.Array) elem = expected.Elem;
        else
        {
            // new[] { ... }: 要素の型から
            elem = TypeSym.ErrorType;
            if (e.C != null && e.C.List != null && e.C.List.Count > 0)
            {
                BindExpr(e.C.List[0], null);
                elem = e.C.List[0].Type!;
            }
        }
        e.Type = TypeSym.ArrayOf(elem);
        if (e.B != null) e.B = BindValue(e.B, TypeSym.IntType);
        if (e.C != null) BindArrayElements(e.C, elem);
        if (e.B != null && e.C != null)
        {
            // new T[n] { ... } の n は定数で、要素の数と同じ
            if (!e.B.HasConst) Error("CS0150", "A constant value is expected", e.B);
            else if (e.B.Const!.Kind == ConstKind.Int && e.B.Const.I != e.C.Count) Error("CS0847", "An array initializer of length '" + e.B.Const.I + "' is expected", e.C);
        }
        else if (e.B != null && !elem.IsError && (!elem.IsValueType || elem.IsNullable || elem.Kind == TypeKind.TypeParam)
            && !(e.B.HasConst && e.B.Const!.Kind == ConstKind.Int && e.B.Const.I == 0))
        {
            // 長さで作る配列の要素の既定値が nil になる型は Lua の sequence で Length を保てない
            // 参照実装と同じく警告 (変換は続け、tcs check と IDE で止める)
            _c.Diags.Warning("TCS1003", "unsupported collection null: " + e.Type.Display() + " created by size cannot keep Length because nil elements do not exist in Lua sequence tables", e.File, e.Pos);
        }
    }

    private Node BindArrayInit(Node init, TypeSym t)
    {
        if (t.Kind != TypeKind.Array)
        {
            if (!t.IsError) Error("CS0622", "Can only use array initializer expressions to assign to array types", init);
            init.Type = TypeSym.ErrorType;
            return init;
        }
        BindArrayElements(init, t.Elem);
        init.Type = t;
        return init;
    }

    private void BindArrayElements(Node init, TypeSym elem)
    {
        if (init.List == null) return;
        for (int i = 0; i < init.List.Count; i++)
        {
            init.List[i] = BindValue(init.List[i], elem);
            if (init.List[i].HasConst && init.List[i].Const!.Kind == ConstKind.Null) _c.Diags.Warning("TCS1003", "unsupported collection null: an array cannot store null elements in Lua sequence tables", init.List[i].File, init.List[i].Pos);
        }
        init.Type = TypeSym.ArrayOf(elem);
    }

    private void BindCollectionExpr(Node e, TypeSym? expected)
    {
        if (expected == null || expected.Kind != TypeKind.List && expected.Kind != TypeKind.Array)
        {
            if (expected == null) Error("CS9176", "There is no target type for the collection expression", e);
            else if (!expected.IsError) SelfHostUnsupported("collection expression of type " + expected.Display(), e);
            e.Type = expected ?? TypeSym.ErrorType;
            if (e.List != null)
            {
                foreach (var x in e.List) BindExpr(x, null);
            }
            return;
        }
        e.Type = expected;
        if (e.List == null) return;
        for (int i = 0; i < e.List.Count; i++) e.List[i] = BindValue(e.List[i], expected.Elem);
    }

    // ---- 演算子 ----

    private void BindUnary(Node e)
    {
        string op = e.Text;
        if (op == "-" && e.A!.Kind == NodeKind.IntLit)
        {
            BindExpr(e.A, null);
            if (e.A.Info == 1)
            {
                // -2147483648
                e.Type = TypeSym.IntType;
                e.HasConst = true;
                e.Const = ConstValue.OfInt(-2147483647 - 1);
                e.A.Type = TypeSym.IntType;
                return;
            }
        }
        else BindExpr(e.A!, null);
        var t = e.A!.Type!;
        if (t.IsError)
        {
            e.Type = t;
            return;
        }
        if (t.Kind == TypeKind.Nullable && (op == "-" || op == "!" || op == "~" || op == "+"))
        {
            // 持ち上げた単項演算子
            var inner = op == "!" ? TypeSym.BoolType : Promote(t.Elem);
            if (inner != null && (op != "!" || t.Elem.Kind == TypeKind.Bool))
            {
                e.A = Coerce(e.A, TypeSym.NullableOf(inner));
                e.Type = TypeSym.NullableOf(inner);
                e.Info = 4;
                return;
            }
        }
        switch (op)
        {
            case "!":
                e.A = Coerce(e.A, TypeSym.BoolType);
                e.Type = TypeSym.BoolType;
                if (e.A.HasConst && e.A.Const!.Kind == ConstKind.Bool)
                {
                    e.HasConst = true;
                    e.Const = ConstValue.OfBool(!e.A.Const.B);
                }
                return;
            case "-":
            case "+":
                {
                    if (t.Decl != null)
                    {
                        var op1 = FindUserOperator(t, op == "-" ? "op_UnaryNegation" : "op_UnaryPlus", 1);
                        if (op1 != null)
                        {
                            e.Sym = op1;
                            e.Type = op1.Type;
                            return;
                        }
                    }
                    var pt = Promote(t);
                    if (pt == null)
                    {
                        Error("CS0023", "Operator '" + op + "' cannot be applied to operand of type '" + t.Display() + "'", e);
                        e.Type = TypeSym.ErrorType;
                        return;
                    }
                    e.A = Coerce(e.A, pt);
                    e.Type = pt;
                    if (e.A.HasConst)
                    {
                        e.HasConst = true;
                        if (pt.Kind == TypeKind.Float) e.Const = ConstValue.OfFloat(op == "-" ? -e.A.Const!.F : e.A.Const!.F);
                        else
                        {
                            int v = e.A.Const!.I;
                            if (op == "-" && v == -2147483647 - 1 && pt.Kind == TypeKind.Int && !_unchecked)
                                Error("CS0220", "The operation overflows at compile time in checked mode", e);
                            e.Const = ConstValue.OfInt(op == "-" ? unchecked(-v) : v);
                        }
                    }
                    return;
                }
            case "~":
                {
                    if (t.Kind == TypeKind.Enum)
                    {
                        e.Type = t;
                        return;
                    }
                    var pt = Promote(t);
                    if (pt == null || pt.Kind == TypeKind.Float)
                    {
                        Error("CS0023", "Operator '~' cannot be applied to operand of type '" + t.Display() + "'", e);
                        e.Type = TypeSym.ErrorType;
                        return;
                    }
                    e.A = Coerce(e.A, pt);
                    e.Type = pt;
                    if (e.A.HasConst)
                    {
                        e.HasConst = true;
                        e.Const = ConstValue.OfInt(~e.A.Const!.I);
                    }
                    return;
                }
        }
        Error("CS0023", "Operator '" + op + "' cannot be applied", e);
        e.Type = TypeSym.ErrorType;
    }

    // 単項 / 二項の数値昇格の型
    private static TypeSym? Promote(TypeSym t)
    {
        switch (t.Kind)
        {
            case TypeKind.Int:
            case TypeKind.Char:
            case TypeKind.Byte:
            case TypeKind.Short:
                return TypeSym.IntType;
            case TypeKind.UInt:
                return TypeSym.UIntType;
            case TypeKind.Float:
                return TypeSym.FloatType;
            case TypeKind.Long:
                return TypeSym.LongType;
            case TypeKind.Double:
                return TypeSym.DoubleType;
        }
        return null;
    }

    private void BindIncDec(Node e)
    {
        BindExpr(e.A!, null);
        CheckAssignable(e.A!);
        var t = e.A!.Type!;
        e.Type = t;
        if (t.IsError) return;
        if (t.IsNumeric || t.Kind == TypeKind.Enum) return;
        if (t.Kind == TypeKind.Nullable && t.Elem.IsNumeric)
        {
            e.Info = 4;
            return;
        }
        Error("CS0023", "Operator '" + e.Text + "' cannot be applied to operand of type '" + t.Display() + "'", e);
    }

    // 二項の user の演算子の overload の解決 (両方の型の候補から、より良い変換のもの)
    private Symbol? ResolveUserOperator(string name, Node a, Node b, TypeSym lt, TypeSym rt)
    {
        var cands = new List<Symbol>();
        AddOperatorCands(lt, name, cands);
        if (rt.Decl != lt.Decl) AddOperatorCands(rt, name, cands);
        Symbol? best = null;
        List<TypeSym>? bestArgs = null;
        foreach (var c in cands)
        {
            if (c.ParamCount != 2) continue;
            // generic な型の演算子は、その型の型引数で parameter を置き換える
            var ownerArgs = c.Owner == lt.Decl ? lt.Args : c.Owner == rt.Decl ? rt.Args : null;
            var p0 = c.Params![0].Type.Subst(ownerArgs, null);
            var p1 = c.Params[1].Type.Subst(ownerArgs, null);
            if (ImplicitKind(a, lt, p0) < 0 || ImplicitKind(b, rt, p1) < 0) continue;
            if (best == null)
            {
                best = c;
                bestArgs = ownerArgs;
            }
            else
            {
                int c1 = BetterConversion(a, p0, best.Params![0].Type.Subst(bestArgs, null));
                int c2 = BetterConversion(b, p1, best.Params[1].Type.Subst(bestArgs, null));
                if (c1 >= 0 && c2 >= 0 && (c1 > 0 || c2 > 0))
                {
                    best = c;
                    bestArgs = ownerArgs;
                }
            }
        }
        _operatorOwnerArgs = bestArgs;
        return best;
    }

    // ResolveUserOperator が選んだ演算子の型の型引数
    private List<TypeSym>? _operatorOwnerArgs;

    private static void AddOperatorCands(TypeSym t, string name, List<Symbol> into)
    {
        if (t.Decl == null) return;
        var list = t.Decl.Lookup(name);
        if (list == null) return;
        foreach (var m in list) into.Add(m);
    }

    private Symbol? FindUserOperator(TypeSym t, string name, int arity)
    {
        if (t.Decl == null) return null;
        var list = t.Decl.Lookup(name);
        if (list == null) return null;
        foreach (var m in list)
        {
            if (m.ParamCount == arity) return m;
        }
        return null;
    }

    private void BindBinary(Node e, TypeSym? expected)
    {
        string op = e.Text;
        if (op == "??")
        {
            BindCoalesce(e, expected);
            return;
        }
        if (op == "&&" || op == "||")
        {
            e.A = BindValue(e.A!, TypeSym.BoolType);
            e.B = BindValue(e.B!, TypeSym.BoolType);
            e.Type = TypeSym.BoolType;
            if (e.A.HasConst && e.B.HasConst && e.A.Const!.Kind == ConstKind.Bool && e.B.Const!.Kind == ConstKind.Bool)
            {
                e.HasConst = true;
                e.Const = ConstValue.OfBool(op == "&&" ? e.A.Const.B && e.B.Const.B : e.A.Const.B || e.B.Const.B);
            }
            return;
        }
        // == default / != default: default の型は他方の operand から
        if ((op == "==" || op == "!=") && e.A!.Kind == NodeKind.DefaultLit && e.A.A == null)
        {
            BindExpr(e.B!, null);
            BindExpr(e.A, e.B!.Type);
        }
        else if ((op == "==" || op == "!=") && e.B!.Kind == NodeKind.DefaultLit && e.B.A == null)
        {
            BindExpr(e.A!, null);
            BindExpr(e.B, e.A!.Type);
        }
        else
        {
            BindExpr(e.A!, null);
            BindExpr(e.B!, null);
        }
        var lt = e.A!.Type!;
        var rt = e.B!.Type!;
        if (lt.IsError || rt.IsError)
        {
            e.Type = op == "==" || op == "!=" || op == "<" || op == ">" || op == "<=" || op == ">=" ? TypeSym.BoolType : TypeSym.ErrorType;
            return;
        }
        // user の演算子 (両方の operand の型の演算子から、引数に合うものを選ぶ)
        if (lt.Decl != null && lt.Kind != TypeKind.Interface && lt.Kind != TypeKind.Enum || rt.Decl != null && rt.Kind != TypeKind.Interface && rt.Kind != TypeKind.Enum)
        {
            var cand = ResolveUserOperator("op_" + Declarer.OperatorName(op, 2), e.A, e.B, lt, rt);
            if (cand != null)
            {
                var oa = _operatorOwnerArgs;
                e.A = Coerce(e.A, cand.Params![0].Type.Subst(oa, null));
                e.B = Coerce(e.B, cand.Params[1].Type.Subst(oa, null));
                e.Sym = cand;
                e.Type = cand.Type.Subst(oa, null);
                return;
            }
        }
        switch (op)
        {
            case "+":
                if (lt.Kind == TypeKind.String || rt.Kind == TypeKind.String)
                {
                    CheckStringifiable(e.A);
                    CheckStringifiable(e.B);
                    e.Type = TypeSym.StringType;
                    e.Info = 1;
                    if (e.A.HasConst && e.B.HasConst && e.A.Const!.Kind == ConstKind.String && e.B.Const!.Kind == ConstKind.String)
                    {
                        e.HasConst = true;
                        e.Const = ConstValue.OfString(e.A.Const.S + e.B.Const.S);
                    }
                    return;
                }
                if (lt.Kind == TypeKind.Enum && rt.IsIntegral)
                {
                    e.B = Coerce(e.B, lt.Decl!.EnumBase!.Kind == TypeKind.Int ? TypeSym.IntType : rt);
                    e.Type = lt;
                    return;
                }
                if (rt.Kind == TypeKind.Enum && lt.IsIntegral)
                {
                    e.Type = rt;
                    return;
                }
                if (lt.Kind == TypeKind.Delegate && rt.Kind == TypeKind.Delegate)
                {
                    Unsupported("delegate combination", e);
                    e.Type = lt;
                    return;
                }
                BindArithmetic(e, lt, rt);
                return;
            case "-":
                if (lt.Kind == TypeKind.Enum && rt.Kind == TypeKind.Enum)
                {
                    e.Type = lt.Decl!.EnumBase!;
                    return;
                }
                if (lt.Kind == TypeKind.Enum && rt.IsIntegral)
                {
                    e.Type = lt;
                    return;
                }
                BindArithmetic(e, lt, rt);
                return;
            case "*":
            case "/":
            case "%":
                BindArithmetic(e, lt, rt);
                return;
            case "<<":
            case ">>":
                {
                    var pl = Promote(lt);
                    if ((lt.Kind == TypeKind.Nullable || rt.Kind == TypeKind.Nullable) && TryLifted(e, lt, rt, false)) return;
                    if (pl == null || pl.Kind == TypeKind.Float || Promote(rt) == null || Promote(rt)!.Kind != TypeKind.Int)
                    {
                        Error("CS0019", "Operator '" + op + "' cannot be applied to operands of type '" + lt.Display() + "' and '" + rt.Display() + "'", e);
                        e.Type = TypeSym.ErrorType;
                        return;
                    }
                    e.A = Coerce(e.A, pl);
                    e.B = Coerce(e.B, TypeSym.IntType);
                    e.Type = pl;
                    FoldInt(e);
                    return;
                }
            case "&":
            case "|":
            case "^":
                if (lt.Kind == TypeKind.Bool && rt.Kind == TypeKind.Bool)
                {
                    e.Type = TypeSym.BoolType;
                    return;
                }
                if (lt.Kind == TypeKind.Enum && TypeSym.Same(lt, rt))
                {
                    e.Type = lt;
                    return;
                }
                if (lt.Kind == TypeKind.Enum && e.B.HasConst && rt.IsIntegral)
                {
                    e.Type = lt;
                    return;
                }
                {
                    var pt = BinaryPromote(lt, rt);
                    if (pt == null && TryLifted(e, lt, rt, false)) return;
                    if (pt == null || pt.Kind == TypeKind.Float)
                    {
                        Error("CS0019", "Operator '" + op + "' cannot be applied to operands of type '" + lt.Display() + "' and '" + rt.Display() + "'", e);
                        e.Type = TypeSym.ErrorType;
                        return;
                    }
                    e.A = Coerce(e.A, pt);
                    e.B = Coerce(e.B, pt);
                    e.Type = pt;
                    FoldInt(e);
                    return;
                }
            case "==":
            case "!=":
                BindEquality(e, lt, rt);
                return;
            case "<":
            case ">":
            case "<=":
            case ">=":
                {
                    e.Type = TypeSym.BoolType;
                    if (lt.Kind == TypeKind.Enum && TypeSym.Same(lt, rt)) return;
                    var pt = BinaryPromote(lt, rt);
                    if (pt == null)
                    {
                        if (TryLifted(e, lt, rt, true)) return;
                        if (lt.Kind == TypeKind.Nullable || rt.Kind == TypeKind.Nullable)
                        {
                            SelfHostUnsupported("lifted comparison", e);
                            return;
                        }
                        Error("CS0019", "Operator '" + op + "' cannot be applied to operands of type '" + lt.Display() + "' and '" + rt.Display() + "'", e);
                        return;
                    }
                    e.A = Coerce(e.A, pt);
                    e.B = Coerce(e.B, pt);
                    e.Type2 = pt;
                    FoldCompare(e, pt);
                    return;
                }
        }
        Error("CS0019", "Operator '" + op + "' cannot be applied", e);
        e.Type = TypeSym.ErrorType;
    }

    private static TypeSym? BinaryPromote(TypeSym a, TypeSym b)
    {
        var pa = Promote(a);
        var pb = Promote(b);
        if (pa == null || pb == null) return null;
        if (pa.Kind == TypeKind.Double || pb.Kind == TypeKind.Double) return TypeSym.DoubleType;
        if (pa.Kind == TypeKind.Float || pb.Kind == TypeKind.Float) return TypeSym.FloatType;
        if (pa.Kind == TypeKind.Long || pb.Kind == TypeKind.Long) return TypeSym.LongType;
        if (pa.Kind == TypeKind.UInt && pb.Kind == TypeKind.UInt) return TypeSym.UIntType;
        if (pa.Kind == TypeKind.UInt || pb.Kind == TypeKind.UInt) return TypeSym.LongType;
        return TypeSym.IntType;
    }

    // T? の operand を持つ演算子 (持ち上げた演算子。片方でも null なら null)
    private bool TryLifted(Node e, TypeSym lt, TypeSym rt, bool comparison)
    {
        if (lt.Kind != TypeKind.Nullable && rt.Kind != TypeKind.Nullable) return false;
        var li = lt.Kind == TypeKind.Nullable ? lt.Elem : lt;
        var ri = rt.Kind == TypeKind.Nullable ? rt.Elem : rt;
        if (lt.Kind == TypeKind.Null || rt.Kind == TypeKind.Null) return false;
        TypeSym? pt;
        if (li.Kind == TypeKind.Bool && ri.Kind == TypeKind.Bool && (e.Text == "&" || e.Text == "|" || e.Text == "^")) pt = TypeSym.BoolType;
        else if ((e.Text == "<<" || e.Text == ">>") && Promote(li) != null) pt = Promote(li);
        else pt = BinaryPromote(li, ri);
        if (pt == null) return false;
        var npt = TypeSym.NullableOf(pt);
        e.A = Coerce(e.A!, e.Text == ">>" || e.Text == "<<" ? npt : npt);
        e.B = Coerce(e.B!, e.Text == ">>" || e.Text == "<<" ? TypeSym.NullableOf(TypeSym.IntType) : npt);
        e.Info = 4;
        e.Type2 = pt;
        e.Type = comparison ? TypeSym.BoolType : npt;
        return true;
    }

    private void BindArithmetic(Node e, TypeSym lt, TypeSym rt)
    {
        var pt = BinaryPromote(lt, rt);
        if (pt == null)
        {
            if (TryLifted(e, lt, rt, false)) return;
            if (lt.Kind == TypeKind.Nullable || rt.Kind == TypeKind.Nullable) SelfHostUnsupported("lifted arithmetic", e);
            else Error("CS0019", "Operator '" + e.Text + "' cannot be applied to operands of type '" + lt.Display() + "' and '" + rt.Display() + "'", e);
            e.Type = TypeSym.ErrorType;
            return;
        }
        e.A = Coerce(e.A!, pt);
        e.B = Coerce(e.B!, pt);
        e.Type = pt;
        if (pt.Kind == TypeKind.Float) FoldFloat(e);
        else FoldInt(e);
    }

    private void FoldInt(Node e)
    {
        if (!e.A!.HasConst || !e.B!.HasConst) return;
        if (e.A.Const!.Kind != ConstKind.Int || e.B.Const!.Kind != ConstKind.Int) return;
        int a = e.A.Const.I;
        int b = e.B.Const.I;
        int r = 0;
        bool overflow = false;
        switch (e.Text)
        {
            case "+":
                r = unchecked(a + b);
                overflow = ((a ^ r) & (b ^ r)) < 0;
                break;
            case "-":
                r = unchecked(a - b);
                overflow = ((a ^ b) & (a ^ r)) < 0;
                break;
            case "*":
                r = unchecked(a * b);
                overflow = a != 0 && (r / a != b || a == -1 && b == -2147483647 - 1);
                break;
            case "/":
                if (b == 0)
                {
                    Error("CS0020", "Division by constant zero", e);
                    return;
                }
                if (a == -2147483647 - 1 && b == -1)
                {
                    if (!_unchecked && e.Type != null && e.Type.Kind == TypeKind.Int) Error("CS0220", "The operation overflows at compile time in checked mode", e);
                    return;
                }
                r = a / b;
                break;
            case "%":
                if (b == 0)
                {
                    Error("CS0020", "Division by constant zero", e);
                    return;
                }
                if (b == -1) r = 0;
                else r = a % b;
                break;
            case "&": r = a & b; break;
            case "|": r = a | b; break;
            case "^": r = a ^ b; break;
            case "<<": r = a << (b & 31); break;
            case ">>": r = a >> (b & 31); break;
            default: return;
        }
        // int の定数式の桁あふれは compile error (uint などは 32 bit に写した値で畳むだけ)
        if (overflow && !_unchecked && e.Type != null && e.Type.Kind == TypeKind.Int)
            Error("CS0220", "The operation overflows at compile time in checked mode", e);
        e.HasConst = true;
        e.Const = ConstValue.OfInt(r);
    }

    private void FoldFloat(Node e)
    {
        if (!e.A!.HasConst || !e.B!.HasConst) return;
        if (e.A.Const!.Kind != ConstKind.Float || e.B.Const!.Kind != ConstKind.Float) return;
        float a = e.A.Const.F;
        float b = e.B.Const.F;
        float r;
        switch (e.Text)
        {
            case "+": r = a + b; break;
            case "-": r = a - b; break;
            case "*": r = a * b; break;
            case "/": r = a / b; break;
            default: return;
        }
        e.HasConst = true;
        e.Const = ConstValue.OfFloat(r);
    }

    private void FoldCompare(Node e, TypeSym pt)
    {
        if (!e.A!.HasConst || !e.B!.HasConst) return;
        var a = e.A.Const!;
        var b = e.B.Const!;
        bool r;
        if (a.Kind == ConstKind.Int && b.Kind == ConstKind.Int)
        {
            switch (e.Text)
            {
                case "<": r = a.I < b.I; break;
                case ">": r = a.I > b.I; break;
                case "<=": r = a.I <= b.I; break;
                case ">=": r = a.I >= b.I; break;
                case "==": r = a.I == b.I; break;
                case "!=": r = a.I != b.I; break;
                default: return;
            }
        }
        else if (a.Kind == ConstKind.Float && b.Kind == ConstKind.Float)
        {
            switch (e.Text)
            {
                case "<": r = a.F < b.F; break;
                case ">": r = a.F > b.F; break;
                case "<=": r = a.F <= b.F; break;
                case ">=": r = a.F >= b.F; break;
                case "==": r = a.F == b.F; break;
                case "!=": r = a.F != b.F; break;
                default: return;
            }
        }
        else return;
        e.HasConst = true;
        e.Const = ConstValue.OfBool(r);
    }

    private void BindEquality(Node e, TypeSym lt, TypeSym rt)
    {
        e.Type = TypeSym.BoolType;
        // null との比較
        if (lt.Kind == TypeKind.Null || rt.Kind == TypeKind.Null)
        {
            var other = lt.Kind == TypeKind.Null ? rt : lt;
            if (!other.IsNullable && other.Kind != TypeKind.Null && other.Kind != TypeKind.TypeParam)
            {
                // int == null は常に false (C# は警告)
                if (other.Kind == TypeKind.Struct) Error("CS0019", "Operator '" + e.Text + "' cannot be applied to operands of type '" + other.Display() + "' and '<null>'", e);
            }
            return;
        }
        var pt = BinaryPromote(lt, rt);
        if (pt != null)
        {
            e.A = Coerce(e.A!, pt);
            e.B = Coerce(e.B!, pt);
            e.Type2 = pt;
            FoldCompare(e, pt);
            return;
        }
        if (lt.Kind == TypeKind.Nullable || rt.Kind == TypeKind.Nullable)
        {
            var inner = lt.Kind == TypeKind.Nullable ? lt.Elem : lt;
            var innerR = rt.Kind == TypeKind.Nullable ? rt.Elem : rt;
            if (TypeSym.Same(inner, innerR) || BinaryPromote(inner, innerR) != null)
            {
                var pp = BinaryPromote(inner, innerR);
                if (pp != null && !(TypeSym.Same(inner, pp) && TypeSym.Same(innerR, pp)))
                {
                    // int? == float? などは両方を持ち上げた共通の型にする
                    e.A = Coerce(e.A!, TypeSym.NullableOf(pp));
                    e.B = Coerce(e.B!, TypeSym.NullableOf(pp));
                }
                return;
            }
        }
        if (lt.Kind == TypeKind.Bool && rt.Kind == TypeKind.Bool || lt.Kind == TypeKind.String && rt.Kind == TypeKind.String)
        {
            if (e.A!.HasConst && e.B!.HasConst)
            {
                var a = e.A.Const!;
                var b = e.B.Const!;
                bool eq = a.Kind == ConstKind.Bool ? a.B == b.B : a.S == b.S;
                e.HasConst = true;
                e.Const = ConstValue.OfBool(e.Text == "==" ? eq : !eq);
            }
            return;
        }
        if (lt.Kind == TypeKind.Tuple || rt.Kind == TypeKind.Tuple)
        {
            Error("TCS1001", "unsupported syntax: TupleEquality", e);
            return;
        }
        if (lt.Kind == TypeKind.Struct && TypeSym.Same(lt, rt) && lt.Decl != null && lt.Decl.IsRecord)
        {
            // record struct の値の等価
            e.Info = 3;
            return;
        }
        if (lt.Kind == TypeKind.Enum && TypeSym.Same(lt, rt))
        {
            if (e.A!.HasConst && e.B!.HasConst)
            {
                bool eq = e.A.Const!.I == e.B.Const!.I;
                e.HasConst = true;
                e.Const = ConstValue.OfBool(e.Text == "==" ? eq : !eq);
            }
            return;
        }
        if (lt.Kind == TypeKind.Enum && e.B!.HasConst && e.B.Const!.Kind == ConstKind.Int && e.B.Const.I == 0) return;
        // 参照実装の緩和: enum と整数の == / !=
        if (lt.Kind == TypeKind.Enum && rt.IsIntegral && rt.Kind != TypeKind.Char || rt.Kind == TypeKind.Enum && lt.IsIntegral && lt.Kind != TypeKind.Char) return;
        // 参照の比較
        if (lt.IsReferenceLike && rt.IsReferenceLike)
        {
            if (lt.Kind == TypeKind.Struct || rt.Kind == TypeKind.Struct)
            {
                Error("CS0019", "Operator '" + e.Text + "' cannot be applied to operands of type '" + lt.Display() + "' and '" + rt.Display() + "'", e);
                return;
            }
            if (TypeSym.Same(lt, rt) || IsImplicitlyConvertible(lt, rt) || IsImplicitlyConvertible(rt, lt)) return;
            if (lt.Kind == TypeKind.Interface || rt.Kind == TypeKind.Interface) return;
        }
        if (lt.Kind == TypeKind.TypeParam || rt.Kind == TypeKind.TypeParam)
        {
            Error("CS0019", "Operator '" + e.Text + "' cannot be applied to operands of type '" + lt.Display() + "' and '" + rt.Display() + "'", e);
            return;
        }
        Error("CS0019", "Operator '" + e.Text + "' cannot be applied to operands of type '" + lt.Display() + "' and '" + rt.Display() + "'", e);
    }

    private void BindCoalesce(Node e, TypeSym? expected)
    {
        BindExpr(e.A!, null);
        var lt = e.A!.Type!;
        if (lt.IsError)
        {
            BindExpr(e.B!, expected);
            e.Type = TypeSym.ErrorType;
            return;
        }
        if (!lt.IsNullable)
        {
            Error("CS0019", "Operator '??' cannot be applied to operands of type '" + lt.Display() + "'", e);
            BindExpr(e.B!, null);
            e.Type = lt;
            return;
        }
        var inner = lt.Kind == TypeKind.Nullable ? lt.Elem : lt;
        BindExpr(e.B!, inner);
        var rt = e.B!.Type!;
        if (lt.Kind == TypeKind.Nullable)
        {
            if (rt.Kind == TypeKind.Nullable && TypeSym.Same(rt, lt))
            {
                e.Type = lt;
                return;
            }
            if (ImplicitKind(e.B, rt, inner) < 0 && IsImplicitlyConvertible(inner, rt))
            {
                // byte? ?? int: 結果は右の型
                e.A = Coerce(e.A, rt.Kind == TypeKind.Nullable ? rt : TypeSym.NullableOf(rt));
                e.Type = rt;
                e.Info = 1;
                return;
            }
            e.B = Coerce(e.B, inner);
            e.Type = inner;
            e.Info = 1;
            return;
        }
        if (IsImplicitlyConvertible(rt, lt) || rt.Kind == TypeKind.Lambda || rt.Kind == TypeKind.MethodGroup)
        {
            e.B = Coerce(e.B, lt);
            e.Type = lt;
            return;
        }
        if (IsImplicitlyConvertible(lt, rt))
        {
            e.A = Coerce(e.A, rt);
            e.Type = rt;
            return;
        }
        Error("CS0019", "Operator '??' cannot be applied to operands of type '" + lt.Display() + "' and '" + rt.Display() + "'", e);
        e.Type = lt;
    }

    private void BindAssign(Node e)
    {
        string op = e.Text;
        var target = e.A!;
        if (op == "=" && target.Kind == NodeKind.Name && target.Text == "_" && LookupLocal("_") == null)
        {
            // _ = expr (discard)
            BindExpr(e.B!, null);
            e.Info = 2;
            e.Type = e.B!.Type;
            target.Type = e.B.Type;
            return;
        }
        if (op == "=") BindWriteTarget(target);
        else BindExpr(target, null);
        CheckAssignable(target);
        var tt = target.Type!;
        if (op == "=")
        {
            if (target.Kind == NodeKind.Member && target.Value == 1) Error("CS0131", "The left-hand side of an assignment must be a variable, property or indexer", target);
            e.B = BindInitializer(e.B!, tt);
            e.Type = tt;
            // 右辺を読んだ後に代入済みにする (x = x + 1 の右辺の x は代入前)
            MarkAssigned(target);
            return;
        }
        if (op == "??=")
        {
            if (!tt.IsNullable && !tt.IsError) Error("CS0019", "Operator '??=' cannot be applied to operand of type '" + tt.Display() + "'", e);
            var inner = tt.Kind == TypeKind.Nullable ? tt.Elem : tt;
            BindExpr(e.B!, inner);
            if (tt.Kind == TypeKind.Nullable && e.B!.Type != null && e.B.Type.Kind == TypeKind.Nullable)
            {
                // a ??= b (b も T?) の値は T?
                e.B = Coerce(e.B, tt);
                e.Type = tt;
                return;
            }
            e.B = Coerce(e.B!, inner);
            e.Type = inner;
            return;
        }
        // 複合代入: a op= b は a = (T)(a op b)
        string bop = op.Replace("=", "");
        BindExpr(e.B!, null);
        var rt = e.B!.Type!;
        e.Type = tt;
        if (tt.IsError || rt.IsError) return;
        if (bop == "+" && tt.Kind == TypeKind.String)
        {
            CheckStringifiable(e.B);
            e.Info = 1;
            return;
        }
        if ((bop == "+" || bop == "-") && tt.Kind == TypeKind.Delegate)
        {
            Unsupported("delegate combination", e);
            return;
        }
        if (tt.Decl != null && tt.Kind != TypeKind.Enum || rt.Decl != null && rt.Kind != TypeKind.Enum && rt.Kind != TypeKind.Interface)
        {
            var cand = ResolveUserOperator("op_" + Declarer.OperatorName(bop, 2), target, e.B, tt, rt);
            if (cand != null)
            {
                e.B = Coerce(e.B, cand.Params![1].Type);
                e.Sym = cand;
                return;
            }
        }
        if (tt.Kind == TypeKind.Enum)
        {
            if (bop == "&" || bop == "|" || bop == "^")
            {
                e.B = Coerce(e.B, tt);
                return;
            }
            if (bop == "+" || bop == "-")
            {
                e.B = Coerce(e.B, tt.Decl!.EnumBase!);
                return;
            }
        }
        if (tt.Kind == TypeKind.Bool && rt.Kind == TypeKind.Bool && (bop == "&" || bop == "|" || bop == "^")) return;
        var pt = bop == "<<" || bop == ">>" ? Promote(tt) : BinaryPromote(tt, rt);
        if ((pt == null || !tt.IsNumeric) && tt.Kind == TypeKind.Nullable)
        {
            var li = tt.Elem;
            var ri = rt.Kind == TypeKind.Nullable ? rt.Elem : rt;
            var lp = bop == "<<" || bop == ">>" ? Promote(li) : BinaryPromote(li, ri);
            if (lp != null || li.Kind == TypeKind.Bool && ri.Kind == TypeKind.Bool)
            {
                var ip = lp ?? TypeSym.BoolType;
                e.B = Coerce(e.B, bop == "<<" || bop == ">>" ? TypeSym.NullableOf(TypeSym.IntType) : TypeSym.NullableOf(ip));
                e.Type2 = ip;
                e.Info = 4;
                return;
            }
        }
        if (pt == null || !tt.IsNumeric)
        {
            if (tt.Kind == TypeKind.Nullable) SelfHostUnsupported("lifted compound assignment", e);
            else Error("CS0019", "Operator '" + op + "' cannot be applied to operands of type '" + tt.Display() + "' and '" + rt.Display() + "'", e);
            return;
        }
        if (bop == "<<" || bop == ">>") e.B = Coerce(e.B, TypeSym.IntType);
        else e.B = Coerce(e.B, pt);
        e.Type2 = pt;
        if (pt.Kind == TypeKind.Float && tt.Kind != TypeKind.Float && tt.Kind != TypeKind.Double)
            Error("CS0266", "Cannot implicitly convert type 'float' to '" + tt.Display() + "'. An explicit conversion exists (are you missing a cast?)", e);
    }

    private void BindConditional(Node e, TypeSym? expected)
    {
        e.A = BindCondition(e.A!);
        bool bTarget = IsTargetTyped(e.B!);
        bool cTarget = IsTargetTyped(e.C!);
        if (bTarget && !cTarget)
        {
            BindExpr(e.C!, expected);
            BindExpr(e.B!, expected ?? e.C!.Type);
        }
        else if (cTarget && !bTarget)
        {
            BindExpr(e.B!, expected);
            BindExpr(e.C!, expected ?? e.B!.Type);
        }
        else
        {
            BindExpr(e.B!, expected);
            BindExpr(e.C!, expected);
        }
        var bt = e.B!.Type!;
        var ct = e.C!.Type!;
        TypeSym? result = null;
        if (TypeSym.Same(bt, ct)) result = bt;
        else if (bt.Kind == TypeKind.Null && ct.IsNullable) result = ct;
        else if (ct.Kind == TypeKind.Null && bt.IsNullable) result = bt;
        else if (bt.Kind == TypeKind.Null && ct.IsValueType) result = TypeSym.NullableOf(ct);
        else if (ct.Kind == TypeKind.Null && bt.IsValueType) result = TypeSym.NullableOf(bt);
        else if (e.B.Kind == NodeKind.DefaultLit && e.B.A == null) result = ct;
        else if (e.C.Kind == NodeKind.DefaultLit && e.C.A == null) result = bt;
        else if (IsTargetTyped(e.B) || IsTargetTyped(e.C) || bt.Kind == TypeKind.Lambda || ct.Kind == TypeKind.Lambda) result = expected;
        else if (IsImplicitlyConvertible(bt, ct) && !IsImplicitlyConvertible(ct, bt)) result = ct;
        else if (IsImplicitlyConvertible(ct, bt) && !IsImplicitlyConvertible(bt, ct)) result = bt;
        else if (expected != null && IsImplicitlyConvertible(bt, expected) && IsImplicitlyConvertible(ct, expected)) result = expected;
        if (result == null)
        {
            if (!bt.IsError && !ct.IsError) Error("CS0173", "Type of conditional expression cannot be determined because there is no implicit conversion between '" + bt.Display() + "' and '" + ct.Display() + "'", e);
            e.Type = TypeSym.ErrorType;
            return;
        }
        e.B = Coerce(e.B, result);
        e.C = Coerce(e.C, result);
        e.Type = result;
        if (e.A.HasConst && e.B.HasConst && e.C.HasConst && e.A.Const!.Kind == ConstKind.Bool)
        {
            e.HasConst = true;
            e.Const = e.A.Const.B ? e.B.Const : e.C.Const;
        }
    }

    private void BindCast(Node e)
    {
        var to = _d.ResolveType(e.A!, _scope!);
        BindExpr(e.B!, to);
        var from = e.B!.Type!;
        e.Type = to;
        if (from.IsError || to.IsError) return;
        if (from.Kind == TypeKind.Lambda || from.Kind == TypeKind.MethodGroup)
        {
            e.B = Coerce(e.B, to);
            return;
        }
        int implicitKind = ImplicitKind(e.B, from, to);
        if (implicitKind >= 0)
        {
            e.B = Coerce(e.B, to);
            e.Info = ConvKind.Identity;
            if (e.B.HasConst)
            {
                e.HasConst = true;
                e.Const = e.B.Const;
            }
            return;
        }
        // 明示の変換
        if (from.IsNumeric && to.IsNumeric || from.Kind == TypeKind.Enum && to.IsIntegral || from.IsIntegral && to.Kind == TypeKind.Enum || from.Kind == TypeKind.Enum && to.Kind == TypeKind.Enum)
        {
            if (from.Kind == TypeKind.Float && to.IsIntegral) e.Info = ConvKind.FloatToInt;
            else if (from.IsIntegral && to.Kind == TypeKind.Float) e.Info = ConvKind.IntToFloat;
            else if (to.Kind == TypeKind.Char && from.Kind != TypeKind.Byte) e.Info = ConvKind.IntToChar;
            else if (to.Kind == TypeKind.Byte)
            {
                if (!(e.B.HasConst && e.B.Const!.Kind == ConstKind.Int && e.B.Const.I >= 0 && e.B.Const.I <= 255))
                    Error("TCS1001", "Unsupported syntax: ByteNarrowing", e);
                e.Info = ConvKind.Identity;
            }
            else e.Info = ConvKind.Identity;
            if (e.B.HasConst)
            {
                var c = e.B.Const!;
                if (e.Info == ConvKind.Identity && c.Kind == ConstKind.Int)
                {
                    e.HasConst = true;
                    e.Const = c;
                }
                else if (e.Info == ConvKind.IntToFloat && c.Kind == ConstKind.Int)
                {
                    e.HasConst = true;
                    e.Const = ConstValue.OfFloat(IntToFloat(c.I));
                }
                else if (e.Info == ConvKind.IntToChar && c.Kind == ConstKind.Int && c.I >= 0 && c.I <= 255)
                {
                    e.HasConst = true;
                    e.Const = c;
                    e.Info = ConvKind.Identity;
                }
            }
            return;
        }
        if (from.Kind == TypeKind.Nullable && TypeSym.Same(from.Elem, to))
        {
            e.Info = ConvKind.NullableValue;
            return;
        }
        if (from.Kind == TypeKind.Object || from.Kind == TypeKind.Interface || from.Kind == TypeKind.TypeParam)
        {
            if (to.Kind == TypeKind.Class)
            {
                e.Info = ConvKind.CastClass;
                return;
            }
            if (to.Kind == TypeKind.Interface)
            {
                e.Info = ConvKind.CastInterface;
                return;
            }
            if (from.Kind == TypeKind.Object)
            {
                if (to.Kind == TypeKind.Int)
                {
                    e.Info = ConvKind.Unbox;
                    return;
                }
                if (to.Kind == TypeKind.Float)
                {
                    e.Info = ConvKind.UnboxFloat;
                    return;
                }
                if (to.Kind == TypeKind.Bool)
                {
                    e.Info = ConvKind.UnboxBool;
                    return;
                }
                if (to.Kind == TypeKind.String)
                {
                    e.Info = ConvKind.UnboxString;
                    return;
                }
            }
            Error("TCS1001", "Unsupported syntax: UnverifiableCast(" + to.Display() + ")", e);
            return;
        }
        if (from.Kind == TypeKind.Class && to.Kind == TypeKind.Interface)
        {
            e.Info = ConvKind.CastInterface;
            return;
        }
        Error("CS0030", "Cannot convert type '" + from.Display() + "' to '" + to.Display() + "'", e);
    }

    private void BindAs(Node e)
    {
        BindExpr(e.A!, null);
        var to = _d.ResolveType(e.B!, _scope!);
        e.Type = to;
        if (to.IsError || e.A!.Type!.IsError) return;
        if (!to.IsNullable || to.Kind == TypeKind.Nullable)
        {
            Error("CS0077", "The as operator must be used with a reference type or nullable type", e);
            return;
        }
        if (to.Kind != TypeKind.Class && to.Kind != TypeKind.Interface)
        {
            Error("TCS1001", "Unsupported syntax: UnverifiableTypeTest(" + to.Display() + ")", e);
        }
    }

    // ---- is と pattern ----

    private void BindIs(Node e)
    {
        BindExpr(e.A!, null);
        var it = e.A!.Type!;
        e.B = BindPattern(e.B!, it);
        e.Type = TypeSym.BoolType;
    }

    private bool TryResolveTypeQuiet(Node typeNode, out TypeSym t)
    {
        int save = _c.Diags.Items.Count;
        t = _d.ResolveType(typeNode, _scope!);
        if (_c.Diags.Items.Count > save || t.IsError)
        {
            _c.Diags.Truncate(save);
            return false;
        }
        return true;
    }

    // 型として読んだ構文を式に直す (case Color.Red: など)
    private static Node TypeNodeToExpr(Node t)
    {
        if (t.Kind == NodeKind.TypeName)
        {
            if (t.A == null)
            {
                var n = new Node(NodeKind.Name, t.File, t.Pos);
                n.Text = t.Text;
                n.End = t.End;
                n.List = t.List;
                return n;
            }
            var m = new Node(NodeKind.Member, t.File, t.Pos);
            m.A = TypeNodeToExpr(t.A);
            m.Text = t.Text;
            m.End = t.End;
            m.List = t.List;
            return m;
        }
        if (t.Kind == NodeKind.PredefType)
        {
            var n = new Node(NodeKind.Name, t.File, t.Pos);
            n.Text = t.Text;
            n.Value = 1;
            return n;
        }
        return t;
    }

    private Node BindPattern(Node p, TypeSym input)
    {
        switch (p.Kind)
        {
            case NodeKind.PatDiscard:
                p.Type = input;
                return p;
            case NodeKind.PatVar:
                {
                    var l = new LocalSym(p.Text, input);
                    p.Local = l;
                    DeclareExprLocal(l, p);
                    p.Type = input;
                    return p;
                }
            case NodeKind.PatType:
                {
                    if (TryResolveTypeQuiet(p.A!, out var t))
                    {
                        p.Type = t;
                        CheckTypeTest(input, t, p);
                        return p;
                    }
                    // 定数
                    var c = new Node(NodeKind.PatConst, p.File, p.Pos);
                    c.A = TypeNodeToExpr(p.A!);
                    return BindPattern(c, input);
                }
            case NodeKind.PatDecl:
                {
                    var t = _d.ResolveType(p.A!, _scope!);
                    p.Type = t;
                    CheckTypeTest(input, t, p);
                    var l = new LocalSym(p.Text, t);
                    p.Local = l;
                    DeclareExprLocal(l, p);
                    return p;
                }
            case NodeKind.PatConst:
                {
                    var target = input.Kind == TypeKind.Nullable ? input.Elem : input;
                    BindExpr(p.A!, target.Kind == TypeKind.Object || target.Kind == TypeKind.Interface ? null : target);
                    var vt = p.A!.Type!;
                    if (!p.A.HasConst)
                    {
                        if (!vt.IsError) Error("CS0150", "A constant value is expected", p.A);
                        p.Type = input;
                        return p;
                    }
                    if (vt.Kind == TypeKind.Null)
                    {
                        if (!input.IsNullable && !input.IsError) Error("CS8520", "The given expression always matches the provided constant", p);
                    }
                    else if (target.Kind != TypeKind.Object && !input.IsError)
                    {
                        p.A = Coerce(p.A, target);
                    }
                    p.Type = input;
                    return p;
                }
            case NodeKind.PatRel:
                {
                    var target = input.Kind == TypeKind.Nullable ? input.Elem : input;
                    p.A = BindValue(p.A!, target);
                    if (!p.A.HasConst) Error("CS0150", "A constant value is expected", p.A);
                    if (!target.IsNumeric && target.Kind != TypeKind.Enum && !target.IsError) Error("CS8781", "Relational patterns may not be used for a value of type '" + target.Display() + "'", p);
                    p.Type = input;
                    return p;
                }
            case NodeKind.PatNot:
                p.A = BindPattern(p.A!, input);
                p.Type = input;
                return p;
            case NodeKind.PatAnd:
                p.A = BindPattern(p.A!, input);
                p.B = BindPattern(p.B!, NarrowedType(p.A, input));
                p.Type = input;
                return p;
            case NodeKind.PatOr:
                p.A = BindPattern(p.A!, input);
                p.B = BindPattern(p.B!, input);
                p.Type = input;
                return p;
            case NodeKind.PatProp:
                {
                    var t = input;
                    if (p.A != null)
                    {
                        t = _d.ResolveType(p.A, _scope!);
                        CheckTypeTest(input, t, p);
                    }
                    p.Type = t;
                    if (p.List != null)
                    {
                        foreach (var sub in p.List)
                        {
                            var holder = new Node(NodeKind.Member, sub.File, sub.Pos);
                            var selfNode = new Node(NodeKind.Name, sub.File, sub.Pos);
                            selfNode.Type = t;
                            holder.A = selfNode;
                            holder.Text = sub.Text;
                            LookupMemberOn(holder, selfNode, t.Kind == TypeKind.Nullable ? t.Elem : t, sub.Text, false);
                            sub.Sym = holder.Sym;
                            sub.Info = holder.Info;
                            var mt = holder.Type ?? TypeSym.ErrorType;
                            sub.Type = mt;
                            sub.A = BindPattern(sub.A!, mt);
                        }
                    }
                    if (p.Text != "")
                    {
                        var l = new LocalSym(p.Text, t);
                        p.Local = l;
                        DeclareExprLocal(l, p);
                    }
                    return p;
                }
        }
        Error("CS8504", "Pattern is not supported", p);
        p.Type = input;
        return p;
    }

    private static TypeSym NarrowedType(Node p, TypeSym input)
    {
        if (p.Kind == NodeKind.PatType || p.Kind == NodeKind.PatDecl) return p.Type!;
        return input;
    }

    private void CheckTypeTest(TypeSym input, TypeSym t, Node at)
    {
        if (input.IsError || t.IsError) return;
        switch (t.Kind)
        {
            case TypeKind.Class:
            case TypeKind.Interface:
            case TypeKind.String:
            case TypeKind.Int:
            case TypeKind.Float:
            case TypeKind.Bool:
                break;
            default:
                if (!TypeSym.Same(input, t) && !(input.Kind == TypeKind.Nullable && TypeSym.Same(input.Elem, t))) Error("TCS1001", "Unsupported syntax: UnverifiableTypeTest(" + t.Display() + ")", at);
                break;
        }
        if (input.Kind == TypeKind.Nullable && TypeSym.Same(input.Elem, t)) return;
        if (input.Kind == TypeKind.Struct || input.IsValueType && !TypeSym.Same(input, t) && input.Kind != TypeKind.Nullable)
        {
            if (!TypeSym.Same(input, t)) Error("CS8121", "An expression of type '" + input.Display() + "' cannot be handled by a pattern of type '" + t.Display() + "'", at);
        }
    }

    private void BindSwitchExpr(Node e, TypeSym? expected)
    {
        BindExpr(e.A!, null);
        var it = e.A!.Type!;
        TypeSym? result = null;
        bool targetTyped = false;
        foreach (var arm in e.List!)
        {
            PushScope();
            var save = _stmt;
            _stmt = arm;
            arm.A = BindPattern(arm.A!, it);
            if (arm.B != null) arm.B = BindCondition(arm.B);
            BindExpr(arm.C!, expected);
            _stmt = save;
            PopScope();
            var at = arm.C!.Type!;
            if (IsTargetTyped(arm.C) || at.Kind == TypeKind.Null) targetTyped = true;
            if (result == null && at.Kind != TypeKind.Null && !at.IsError) result = at;
            else if (result != null && !TypeSym.Same(result, at) && !at.IsError && at.Kind != TypeKind.Null)
            {
                if (IsImplicitlyConvertible(result, at) && !IsImplicitlyConvertible(at, result)) result = at;
            }
        }
        if (expected != null && (targetTyped || result == null || !TypeSym.Same(result, expected) && IsImplicitlyConvertible(result, expected))) result = expected;
        if (result == null)
        {
            Error("CS8506", "No best type was found for the switch expression", e);
            result = TypeSym.ErrorType;
        }
        foreach (var arm in e.List)
        {
            arm.C = Coerce(arm.C!, result);
        }
        e.Type = result;
    }
}
