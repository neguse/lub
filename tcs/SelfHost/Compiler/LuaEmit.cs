using System.Collections.Generic;

namespace TcsSelf;

// 出力した型の記録 (module の artifact と hot reload が使う)
public sealed class EmittedTypeInfo
{
    public string Name = "";
    public string Kind = "";
    public string CSharpName = "";
    public List<string> DefinitionKeys = new List<string>();
    public List<string> EnumMemberNames = new List<string>();
    public List<int> EnumMemberValues = new List<int>();
}

// static の初期化子を後で出す型 (StaticInit)
public sealed class PendingStaticInit
{
    public string Name;
    public EmittedTypeInfo Info;
    public List<FieldInit> Inits;

    public PendingStaticInit(string name, EmittedTypeInfo info, List<FieldInit> inits)
    {
        Name = name;
        Info = info;
        Inits = inits;
    }
}

// struct の値を構成する member (名前と型)
public sealed class ValueMember
{
    public string Name;
    public TypeSym Type;

    public ValueMember(string name, TypeSym type)
    {
        Name = name;
        Type = type;
    }
}

// field / 自動実装の property の初期化子 (名前、初期化子の式、型)
public sealed class FieldInit
{
    public string Name;
    public Node? Init;
    public TypeSym Type;

    public FieldInit(string name, Node? init, TypeSym type)
    {
        Name = name;
        Init = init;
        Type = type;
    }
}

// 束縛済みの木から Lua の chunk を出す。参照実装の LuaEmitter (型の出力、constructor、method、property、enum、
// static の初期化、型の local、--trace) を写したもの。IL の組み立ては IlBuild、IL の描画は LuaGen
public sealed class LuaEmit
{
    public readonly Compilation C;
    public readonly LuaGen G = new LuaGen();
    public IlBuild B;
    public bool InstanceRegistry;
    // IL を C backend に渡す経路は引数の copy 地点を残す (C は値渡しで copy が要らない)
    public bool KeepsArgumentCopies;
    public List<string> TracePrefixes = new List<string>();
    public int TypeLocalBudget;
    public List<string> Errors = new List<string>();
    public List<EmittedTypeInfo> EmittedTypes = new List<EmittedTypeInfo>();
    // 型 (Lua 名) → static の初期化子が先に初期化を求める型 (StaticInitOrder)。null なら宣言の順
    public Dictionary<string, List<string>>? StaticInitDependencies;

    private bool _headerEmitted;
    private int _typeLocalsChunk = -1;
    private List<string> _typeLocals = new List<string>();
    private HashSet<string> _emittedTypeNames = new HashSet<string>();
    private EmittedTypeInfo? _currentType;
    private List<PendingStaticInit> _pendingStaticInits = new List<PendingStaticInit>();
    // emit 中の型と関数の文脈
    public TypeDeclSym? CurrentDecl;
    public SourceFile? CurrentFile;
    public Node? CurrentUnit;
    // generic な型の instance の member は型引数の既定値を self から読む
    public bool TypeArgDefaultsOnSelf;
    // emit 中の method (式本体が void かを見る)
    public Symbol? CurrentFunction;
    // 組み立て中の lambda の戻りが void か (0 = lambda の外、1 = void、2 = 値)
    public int CurrentLambdaVoid;

    // chunk 直下の local は 200 個が上限。prelude の helper と利用者の top-level の文の local の余地を残した型の数の上限
    public const int MaxTypeLocals = 100;

    public LuaEmit(Compilation c)
    {
        C = c;
        B = new IlBuild(this);
    }

    public void Internal(string message)
    {
        G.InternalErrors.Add(message);
    }

    // ---- 入口 ----

    // 1 つの compile の Lua の chunk (prelude を含む)。entry があれば最後にその型を return する
    public string EmitProgram(string entry)
    {
        var emitted = new List<int>();
        // library は user の型より先に定義する (user の static の初期化子が使える)
        for (int i = 0; i < C.Files.Count; i++)
        {
            if (C.Files[i].IsLibrary) emitted.Add(i);
        }
        for (int i = 0; i < C.Files.Count; i++)
        {
            if (!C.Files[i].IsLibrary && !C.Files[i].IsRef) emitted.Add(i);
        }
        TypeLocalBudget = DefaultTypeLocalBudget(emitted);
        bool hasTopLevel = false;
        for (int i = 0; i < C.Units.Count; i++)
        {
            if (C.Units[i].Count2 > 0) hasTopLevel = true;
        }
        if (hasTopLevel)
        {
            foreach (var i in emitted) Visit(C.Units[i], true, false);
            foreach (var i in emitted) Visit(C.Units[i], false, true);
        }
        else
        {
            foreach (var i in emitted) Visit(C.Units[i], true, true);
        }
        var lua = Finish();
        if (entry != "")
        {
            TypeDeclSym? found = null;
            if (C.TypesByFullName.TryGetValue(entry, out var byFull)) found = byFull;
            if (found == null)
            {
                var candidates = new List<TypeDeclSym>();
                foreach (var t in C.Types)
                {
                    if (t.Name == entry) candidates.Add(t);
                }
                if (candidates.Count > 1)
                {
                    var names = new List<string>();
                    foreach (var t in candidates) names.Add(t.FullName);
                    Errors.Add("entry class is ambiguous: " + entry + " (" + string.Join(", ", names) + ")");
                    return lua;
                }
                if (candidates.Count == 1) found = candidates[0];
            }
            if (found == null)
            {
                Errors.Add("entry class not found: " + entry);
                return lua;
            }
            if (found.IsRef)
            {
                Errors.Add("entry class is reference-only (--ref): " + entry);
                return lua;
            }
            if (found.DeclKind != DeclKinds.Class && found.DeclKind != DeclKinds.RecordClass)
            {
                Errors.Add("entry class must be a class or record class: " + found.FullName);
                return lua;
            }
            lua = lua + "return " + TypeName(found) + "\n";
        }
        return lua;
    }

    // emit する tree の群に対する既定の budget。top-level の文の local は chunk 直下に出るので、その分 (過大評価で可) を差し引く
    private int DefaultTypeLocalBudget(List<int> files)
    {
        int topLevelLocals = 0;
        foreach (var i in files)
        {
            var unit = C.Units[i];
            if (unit.List2 == null) continue;
            foreach (var g in unit.List2) topLevelLocals += CountLocalDesignations(g);
        }
        int budget = MaxTypeLocals - topLevelLocals;
        return budget < 0 ? 0 : budget;
    }

    // VariableDeclarator / foreach / 単一の変数の designation (out var と pattern の変数) の数
    private static int CountLocalDesignations(Node n)
    {
        int count = 0;
        if (n.Kind == NodeKind.VarDeclarator || n.Kind == NodeKind.Foreach || n.Kind == NodeKind.PatDecl || n.Kind == NodeKind.PatVar)
            count++;
        if (n.Kind == NodeKind.Arg && n.Text == "var") count++;
        if (n.A != null) count += CountLocalDesignations(n.A);
        if (n.B != null) count += CountLocalDesignations(n.B);
        if (n.C != null) count += CountLocalDesignations(n.C);
        if (n.D != null) count += CountLocalDesignations(n.D);
        if (n.List != null)
        {
            foreach (var x in n.List) count += CountLocalDesignations(x);
        }
        if (n.List2 != null)
        {
            foreach (var x in n.List2) count += CountLocalDesignations(x);
        }
        if (n.List3 != null)
        {
            foreach (var x in n.List3) count += CountLocalDesignations(x);
        }
        return count;
    }

    // ---- 名前 ----

    // 型の Lua の global 名。namespace の中の型は namespace 修飾名、入れ子の型は外側の型の名前を `_` でつなぐ
    public string TypeName(TypeDeclSym t)
    {
        return t.LuaName;
    }

    // ---- 出力の道具 ----

    public void AppendLine(string line)
    {
        G.AppendLine(line);
    }

    public void SetSource(Node node)
    {
        G.SetSource(Origin(node));
    }

    // 構文の位置 (同じ node には同じ IlPos)
    public IlPos Origin(Node node)
    {
        if (node.IlOrigin == null) node.IlOrigin = new IlPos(node.File.Path, node.File.LineOf(node.Pos));
        return node.IlOrigin;
    }

    // ---- --trace ----

    private bool IsTracedPath(string path)
    {
        var normalized = TraceMatchForm(path);
        foreach (var raw in TracePrefixes)
        {
            var prefix = TraceMatchForm(raw);
            if (prefix == "" || normalized == prefix || normalized.StartsWith(prefix + "/")) return true;
        }
        return false;
    }

    private static string TraceMatchForm(string path)
    {
        var p = LuaGen.NormalizeTracePath(path);
        while (p.StartsWith("./")) p = Strings.From(p, 2);
        while (p.EndsWith("/")) p = Strings.Prefix(p, Strings.ByteLength(p) - 1);
        return p;
    }

    // 関数の本体の先頭 (parameter の既定値より前、どの goto / label より前) で呼ぶ
    public void BeginTraceFunction()
    {
        G.TraceFunction = TracePrefixes.Count > 0 && CurrentFile != null && IsTracedPath(CurrentFile.Path);
        if (G.TraceFunction) AppendLine(LuaGen.TraceLocal);
    }

    public void EndTraceFunction()
    {
        G.TraceFunction = false;
    }

    // 対象のファイルの型の table (class / record / struct) を global の __tcs_trace_classes に登録する
    private void RegisterTraceClass(string name, string kind, TypeDeclSym? symbol)
    {
        if (TracePrefixes.Count == 0 || kind == "enum") return;
        if (symbol == null || !IsTracedPath(symbol.Decl.File.Path)) return;
        AppendLine("__tcs_trace_classes[" + LuaGen.EscapeLuaString(name) + "] = " + name);
    }

    // ---- unit と member ----

    // local の改名を避ける型の名前 (simple 名、global 名、--ref の型の path の先頭)
    private HashSet<string>? _reservedTypeNames;
    private HashSet<Node> _renamedUnits = new HashSet<Node>();

    public HashSet<string> ReservedTypeNames()
    {
        if (_reservedTypeNames != null) return _reservedTypeNames;
        var names = new HashSet<string>();
        foreach (var t in C.Types)
        {
            names.Add(t.Name);
            if (t.IsRef)
            {
                var path = t.LuaName;
                if (path != "") names.Add(path.Split('.')[0]);
            }
            else names.Add(t.LuaName);
        }
        _reservedTypeNames = names;
        return names;
    }

    private void Visit(Node unit, bool emitNonGlobalMembers, bool emitGlobalStatements)
    {
        CurrentFile = unit.File;
        CurrentUnit = unit;
        if (_renamedUnits.Add(unit)) LocalNames.AssignUnit(unit, ReservedTypeNames());
        if (!_headerEmitted)
        {
            _typeLocalsChunk = LuaPrelude.Emit(G, InstanceRegistry, TracePrefixes.Count > 0, TypeLocalBudget > 0);
            _headerEmitted = true;
        }
        if (emitNonGlobalMembers && unit.List != null)
        {
            foreach (var member in unit.List)
            {
                if (member.Kind != NodeKind.Using) VisitMember(member);
            }
        }
        if (emitGlobalStatements && unit.List2 != null && unit.List2.Count > 0)
        {
            // top-level の文は実行時に static を触りうるので、実行前に static を初期化する
            FlushStaticInitializers();
            CurrentDecl = null;
            B.ScopeRoots.Clear();
            B.ScopeRoots.Add(unit);
            foreach (var global in unit.List2) VisitGlobalStatement(global);
        }
    }

    private void VisitGlobalStatement(Node stmt)
    {
        var acc = new List<IlStat>();
        if (B.BuildStatInto(stmt, acc))
        {
            G.EmitIlBlock(new IlBlock(acc), null);
            return;
        }
        ReportUnsupportedBody(stmt);
    }

    public void ReportUnsupportedBody(Node at)
    {
        Errors.Add(at.File.Path + "(" + at.File.LineOf(at.Pos) + "," + at.File.ColumnOf(at.Pos) + "): error TCS1001: unsupported syntax: " + at.Kind);
    }

    private void VisitMember(Node member)
    {
        switch (member.Kind)
        {
            case NodeKind.Namespace:
                if (member.List != null)
                {
                    foreach (var m in member.List) VisitMember(m);
                }
                return;
            case NodeKind.TypeDecl:
                switch (member.Value)
                {
                    case DeclKinds.Class: VisitClass(member); break;
                    case DeclKinds.Struct: VisitStruct(member); break;
                    case DeclKinds.RecordClass: VisitRecord(member); break;
                    case DeclKinds.RecordStruct: VisitRecordStruct(member); break;
                    case DeclKinds.Enum: VisitEnum(member); break;
                }
                // 入れ子の型は外側の型の後に、独立した global の型として出す
                if (member.List != null)
                {
                    foreach (var m in member.List)
                    {
                        if (m.Kind == NodeKind.TypeDecl) VisitMember(m);
                    }
                }
                return;
        }
    }

    // Lua の global の table を持つ型 (class / record / enum / struct) の共通の入口
    private EmittedTypeInfo BeginType(string name, string kind, TypeDeclSym symbol)
    {
        var info = new EmittedTypeInfo();
        info.Name = name;
        info.Kind = kind;
        info.CSharpName = symbol.FullName;
        EmittedTypes.Add(info);
        _currentType = info;
        _emittedTypeNames.Add(name);
        AppendTypeTableDecl(name);
        RegisterTraceClass(name, kind, symbol);
        return info;
    }

    // `Name = {}` の宣言の行。budget の中なら chunk の先頭で forward 宣言した local へ代入し、global にも同じ table を公開する
    private void AppendTypeTableDecl(string name)
    {
        if (_typeLocals.Contains(name) || (_typeLocals.Count < TypeLocalBudget && _headerEmitted))
        {
            if (!_typeLocals.Contains(name)) _typeLocals.Add(name);
            AppendLine(name + " = {}; _ENV." + name + " = " + name);
        }
        else
        {
            AppendLine(name + " = {}");
        }
    }

    public void AddDefinitionKey(string key)
    {
        if (_currentType != null) _currentType.DefinitionKeys.Add(key);
    }

    // ---- class ----

    private static bool IsStaticField(Node field)
    {
        return field.Has(Mods.Static) || field.Has(Mods.Const);
    }

    // 自動実装の property (accessor のどれにも本体が無い)
    public static bool IsAutoProperty(Node prop)
    {
        if (prop.B == null && prop.C == null) return false;
        if (prop.B != null && (prop.B.A != null || prop.B.B != null)) return false;
        if (prop.C != null && (prop.C.A != null || prop.C.B != null)) return false;
        // 式本体の property (`int P => e;`) は B に式を持つ get
        return true;
    }

    // 式本体の property (`int P => e;`)
    private static bool IsExpressionBodiedProperty(Node prop)
    {
        return prop.C == null && prop.B != null && prop.B.A == null && prop.B.B != null && prop.B.Pos == prop.Pos;
    }

    private void CollectFieldInits(Node decl, List<FieldInit> instance, List<FieldInit> statics, List<Node> ctors)
    {
        if (decl.List == null) return;
        foreach (var member in decl.List)
        {
            switch (member.Kind)
            {
                case NodeKind.Field:
                {
                    var target = IsStaticField(member) ? statics : instance;
                    if (member.List == null) break;
                    foreach (var v in member.List)
                    {
                        var t = v.Sym != null ? v.Sym.Type : TypeSym.ErrorType;
                        target.Add(new FieldInit(v.Text, v.A, t));
                    }
                    break;
                }
                case NodeKind.Property:
                    if (IsAutoProperty(member))
                    {
                        var target = member.Has(Mods.Static) ? statics : instance;
                        var t = member.Sym != null ? member.Sym.Type : TypeSym.ErrorType;
                        target.Add(new FieldInit(member.Text, member.D, t));
                    }
                    break;
                case NodeKind.Ctor:
                    ctors.Add(member);
                    break;
            }
        }
    }

    private void VisitClass(Node cls)
    {
        SetSource(cls);
        var symbol = cls.TypeDecl!;
        CurrentDecl = symbol;
        var name = TypeName(symbol);
        var info = BeginType(name, "class", symbol);
        AppendLine(name + ".__index = " + name);
        info.DefinitionKeys.Add("__index");
        EmitImplementedInterfaces(name, info, symbol);
        AppendLine("");

        var fieldInits = new List<FieldInit>();
        var staticFieldInits = new List<FieldInit>();
        var ctors = new List<Node>();
        CollectFieldInits(cls, fieldInits, staticFieldInits, ctors);

        EmitStaticPreZero(name, info, staticFieldInits);
        if (ctors.Count == 0) EmitConstructor(name, symbol, null, fieldInits);
        foreach (var c in ctors) EmitConstructor(name, symbol, c, fieldInits);

        var operators = new List<Node>();
        if (cls.List != null)
        {
            foreach (var member in cls.List)
            {
                switch (member.Kind)
                {
                    case NodeKind.Method:
                        VisitMethod(name, member, false);
                        break;
                    case NodeKind.Property:
                        if (IsExpressionBodiedProperty(member)) VisitExpressionBodiedProperty(name, member, false);
                        else if (!IsAutoProperty(member)) VisitCustomProperty(name, member, false);
                        break;
                    case NodeKind.OperatorDecl:
                        operators.Add(member);
                        break;
                }
            }
        }
        EmitOperators(name, operators);
        DeferStaticFieldInitializers(name, info, staticFieldInits);
        _currentType = null;
        CurrentDecl = null;
    }

    // ---- struct / record struct ----

    // struct の instance は metatable の無い plain table で、member は静的自由関数 (`S.M(self, ...)`)。
    // 継承が無く呼び出しの静的型が決まるので、呼び出し側 (IlBuild) が直接呼ぶ
    private void VisitStruct(Node decl)
    {
        SetSource(decl);
        var symbol = decl.TypeDecl!;
        CurrentDecl = symbol;
        var name = TypeName(symbol);
        var info = BeginStructType(name, symbol);
        var statics = EmitStructStaticPreZero(name, info, decl);
        EmitStructNew(name, symbol);
        EmitStructCopyFunction(name, symbol);
        EmitExplicitStructCtors(name, decl);
        EmitStructEquality(name, symbol);
        EmitStructMembers(name, decl);
        DeferStaticFieldInitializers(name, info, statics);
        _currentType = null;
        CurrentDecl = null;
    }

    private EmittedTypeInfo BeginStructType(string name, TypeDeclSym symbol)
    {
        var info = BeginType(name, "struct", symbol);
        AppendLine("");
        return info;
    }

    // static field と static の自動実装の property は class と同じく型の table に pre-zero し、初期化子は全型の定義の後に実行する。
    // const は使う所で値に畳むので持たない
    private List<FieldInit> EmitStructStaticPreZero(string name, EmittedTypeInfo info, Node decl)
    {
        var statics = new List<FieldInit>();
        if (decl.List != null)
        {
            foreach (var member in decl.List)
            {
                if (!member.Has(Mods.Static)) continue;
                if (member.Kind == NodeKind.Field && member.List != null)
                {
                    foreach (var v in member.List)
                    {
                        var t = v.Sym != null ? v.Sym.Type : TypeSym.ErrorType;
                        statics.Add(new FieldInit(v.Text, v.A, t));
                    }
                }
                else if (member.Kind == NodeKind.Property && IsAutoProperty(member))
                {
                    var t = member.Sym != null ? member.Sym.Type : TypeSym.ErrorType;
                    statics.Add(new FieldInit(member.Text, member.D, t));
                }
            }
        }
        EmitStaticPreZero(name, info, statics);
        return statics;
    }

    // record struct: struct の上に positional な primary ctor と値の等価 (op_Equality) を足す
    private void VisitRecordStruct(Node decl)
    {
        SetSource(decl);
        var symbol = decl.TypeDecl!;
        CurrentDecl = symbol;
        var name = TypeName(symbol);
        var info = BeginStructType(name, symbol);
        var statics = EmitStructStaticPreZero(name, info, decl);
        EmitStructNew(name, symbol);
        // positional な primary ctor: zero → positional の代入 → 初期化子 (初期化子は parameter を名前で読める)
        if (decl.A != null && decl.A.List != null && decl.A.List.Count > 0)
        {
            var paramNames = new List<string>();
            foreach (var p in decl.A.List) paramNames.Add(p.Text);
            var hidden = TypeTypeArgDefaultParams(symbol);
            var all = new List<string>();
            foreach (var p in paramNames) all.Add(p);
            foreach (var h in hidden) all.Add(h);
            AddDefinitionKey("ctor");
            AppendLine("function " + name + ".ctor(" + string.Join(", ", all) + ")");
            G.Indent++;
            AppendLine("local self = " + name + ".new(" + string.Join(", ", hidden) + ")");
            foreach (var p in paramNames) AppendLine("self." + p + " = " + p);
            B.ScopeRoots.Clear();
            B.ScopeRoots.Add(decl);
            EmitMemberInitializers(decl);
            AppendLine("return self");
            G.Indent--;
            AppendLine("end");
            AppendLine("");
        }
        EmitExplicitStructCtors(name, decl);
        EmitStructCopyFunction(name, symbol);
        EmitStructEquality(name, symbol);
        EmitStructMembers(name, decl);
        DeferStaticFieldInitializers(name, info, statics);
        _currentType = null;
        CurrentDecl = null;
    }

    // 値の等価 (memberwise)。record struct の == / != と、struct の要素の List.Contains / IndexOf / Remove が使う
    private void EmitStructEquality(string name, TypeDeclSym symbol)
    {
        AddDefinitionKey("op_Equality");
        AppendLine("function " + name + ".op_Equality(a, b)");
        G.Indent++;
        var parts = ValueEqualityParts(symbol.SelfType(), "a", "b");
        if (parts.Count == 0) AppendLine("return true");
        else AppendLine("return " + string.Join(" and ", parts));
        G.Indent--;
        AppendLine("end");
        AppendLine("");
    }

    // zero 値の constructor。`new S()` / default(S) / field の既定値が通る
    private void EmitStructNew(string name, TypeDeclSym symbol)
    {
        AddDefinitionKey("new");
        var hidden = TypeTypeArgDefaultParams(symbol);
        AppendLine("function " + name + ".new(" + string.Join(", ", hidden) + ")");
        G.Indent++;
        var zero = new List<string>();
        foreach (var h in hidden) zero.Add(h + " = " + h);
        foreach (var m in ValueMembers(symbol.SelfType())) zero.Add(m.Name + " = " + GetDefaultValueForType(m.Type));
        AppendLine("return {" + string.Join(", ", zero) + "}");
        G.Indent--;
        AppendLine("end");
        AppendLine("");
    }

    // member の出力。override は ToString だけが診断を通る
    private void EmitStructMembers(string name, Node decl)
    {
        var operators = new List<Node>();
        if (decl.List != null)
        {
            foreach (var member in decl.List)
            {
                if (member.Kind == NodeKind.OperatorDecl) operators.Add(member);
            }
        }
        EmitOperators(name, operators);
        if (decl.List == null) return;
        foreach (var member in decl.List)
        {
            switch (member.Kind)
            {
                case NodeKind.Method:
                    VisitMethod(name, member, true);
                    break;
                case NodeKind.Property:
                    if (IsExpressionBodiedProperty(member)) VisitExpressionBodiedProperty(name, member, true);
                    else if (!IsAutoProperty(member) && (member.B != null || member.C != null)) VisitCustomProperty(name, member, true);
                    break;
            }
        }
    }

    // field / property の初期化子は明示の ctor を実行するときだけ走る (C# 11 の意味)
    private void EmitMemberInitializers(Node decl)
    {
        if (decl.List == null) return;
        foreach (var member in decl.List)
        {
            if (member.Has(Mods.Static) || member.Has(Mods.Const)) continue;
            if (member.Kind == NodeKind.Field && member.List != null)
            {
                foreach (var v in member.List)
                {
                    if (v.A != null) AppendLine("self." + v.Text + " = " + RenderExprViaIl(v.A, true));
                }
            }
            else if (member.Kind == NodeKind.Property && member.D != null)
            {
                AppendLine("self." + member.Text + " = " + RenderExprViaIl(member.D, true));
            }
        }
    }

    // 明示の ctor は zero 値 (S.new) の上で、field の初期化子 → 本文の順に実行する。
    // `new S()` は ctor を通らない zero 値なので、呼び出しは builder が S.ctor へ振り分ける
    private void EmitExplicitStructCtors(string name, Node decl)
    {
        if (decl.List == null) return;
        foreach (var ctor in decl.List)
        {
            if (ctor.Kind != NodeKind.Ctor || ctor.Has(Mods.Static)) continue;
            if (ctor.List == null || ctor.List.Count == 0) continue;
            EmitStructCtor(name, decl, ctor);
        }
    }

    private void EmitStructCtor(string name, Node decl, Node ctor)
    {
        SetSource(ctor);
        var ctorParams = new List<string>();
        foreach (var p in ctor.List!) ctorParams.Add(ParamLuaName(p));
        var hidden = TypeTypeArgDefaultParams(decl.TypeDecl!);
        var ctorName = ctor.Sym != null ? CtorName(ctor.Sym) : "ctor";
        AddDefinitionKey(ctorName);
        var all = new List<string>();
        foreach (var p in ctorParams) all.Add(p);
        foreach (var h in hidden) all.Add(h);
        B.ScopeRoots.Clear();
        B.ScopeRoots.Add(ctor);
        AppendLine("function " + name + "." + ctorName + "(" + string.Join(", ", all) + ")");
        G.Indent++;
        BeginTraceFunction();
        EmitParameterDefaults(ctor);
        EmitParameterEntryCopies(ctor);
        AppendLine("local self = " + name + ".new(" + string.Join(", ", hidden) + ")");
        EmitMemberInitializers(decl);
        if (ctor.B != null) EmitStatsViaIl(ctor.B);
        AppendLine("return self");
        EndTraceFunction();
        G.Indent--;
        AppendLine("end");
        AppendLine("");
    }

    // 型別の copy の関数。struct の中の struct は再帰して copy する (shallow だと copy を通した部分の書き込みが共有される)。
    // readonly な struct の member は変わらないので共有する
    private void EmitStructCopyFunction(string name, TypeDeclSym symbol)
    {
        AddDefinitionKey("__copy");
        var selfType = symbol.SelfType();
        // generic な struct は型引数の値を写す関数を受ける (StructCopiers)
        var copierParts = new List<string>();
        if (PassesTypeArgDefaults(symbol))
        {
            foreach (var tp in symbol.TypeParams) copierParts.Add(", __tcs_cc_" + tp);
        }
        var copierParams = string.Join("", copierParts);
        AppendLine("function " + name + ".__copy(s" + copierParams + ")");
        G.Indent++;
        var copied = new List<string>();
        foreach (var h in TypeTypeArgDefaultParams(symbol)) copied.Add(h + " = s." + h);
        foreach (var m in ValueMembers(selfType))
        {
            if (m.Type.Kind == TypeKind.TypeParam)
            {
                var cc = "__tcs_cc_" + m.Type.Name;
                copied.Add(m.Name + " = " + cc + " and " + cc + "(s." + m.Name + ") or s." + m.Name);
            }
            else if (IlTypes.IsMutableStruct(m.Type))
                copied.Add(m.Name + " = " + B.TypeName(m.Type) + ".__copy(s." + m.Name + B.StructCopiers(m.Type, true) + ")");
            else copied.Add(m.Name + " = s." + m.Name);
        }
        AppendLine("return {" + string.Join(", ", copied) + "}");
        G.Indent--;
        AppendLine("end");
        AppendLine("");
        // 型引数に入れたときの記述子 (可変な struct と ToString を持つ struct)。hot reload で差し替わる関数を引くので closure
        if (copierParams == "" && NeedsTypeArgDescriptor(selfType))
        {
            AddDefinitionKey("__tcs_td");
            AppendLine(name + ".__tcs_td = {__tcs_td = true, n = function() return " + name + ".new() end, c = function(v) return " + name + ".__copy(v) end" + DescriptorToString(name, selfType) + "}");
            AppendLine("");
        }
    }

    // 型引数に入れたとき、既定値の値では足りない struct: 可変 (copy の作り方が要る) か、ToString の override を持つ
    public static bool NeedsTypeArgDescriptor(TypeSym t)
    {
        return IlTypes.IsMutableStruct(t) || (IlTypes.IsUserStruct(t) && IlBuild.HasToStringOverride(t));
    }

    private static string DescriptorToString(string name, TypeSym t)
    {
        if (!IlBuild.HasToStringOverride(t)) return "";
        return ", s = function(v) return " + name + ".ToString(v) or \"\" end";
    }

    // 値を構成する member (field と自動実装の property。positional な property を含む)。
    // 基底の record から同名の member を継承する positional parameter は property にならない
    public static List<ValueMember> ValueMembers(TypeSym? t)
    {
        var r = new List<ValueMember>();
        if (t == null) return r;
        if (t.Kind == TypeKind.Tuple)
        {
            for (int i = 0; i < t.Args!.Count; i++) r.Add(new ValueMember("Item" + (i + 1), t.Args[i]));
            return r;
        }
        if (t.Decl == null) return r;
        bool generic = t.Args != null && t.Args.Count > 0;
        foreach (var m in t.Decl.Members)
        {
            if (m.IsStatic || m.IsConst) continue;
            if (m.Kind == SymKind.Field || (m.Kind == SymKind.Property && m.IsAuto))
            {
                if (m.Kind == SymKind.Property && t.Decl.BaseClass != null && t.Decl.BaseClass.Lookup(m.Name) != null) continue;
                r.Add(new ValueMember(m.Name, generic ? m.Type.Subst(t.Args, null) : m.Type));
            }
        }
        return r;
    }

    public static List<string> ValueEqualityParts(TypeSym t, string a, string b)
    {
        var r = new List<string>();
        foreach (var m in ValueMembers(t))
        {
            foreach (var p in MemberEqualityParts(m.Type, a + "." + m.Name, b + "." + m.Name)) r.Add(p);
        }
        return r;
    }

    // 値 1 つの等価の式 (EqualityComparer<T>.Default)。struct の値は field へ展開し、struct の T? は値の有無が同じで中の値が等しいとき等しい。
    // float / double (とその T?) と型引数は NaN 同士も等しい。それ以外は Lua の == (record class は __eq)
    public static List<string> MemberEqualityParts(TypeSym t, string a, string b)
    {
        if (IlTypes.IsUserStruct(t)) return ValueEqualityParts(t, a, b);
        var r = new List<string>();
        bool nullable = IlTypes.IsNullableValueType(t);
        var inner = nullable ? t.Elem : t;
        if (nullable && IlTypes.IsUserStruct(inner))
        {
            var parts = new List<string>();
            parts.Add(b + " ~= nil");
            foreach (var p in ValueEqualityParts(inner, a, b)) parts.Add(p);
            r.Add("(" + a + " == nil and " + b + " == nil or " + a + " ~= nil and " + string.Join(" and ", parts) + ")");
            return r;
        }
        if (inner.Kind == TypeKind.Float || inner.Kind == TypeKind.Double || inner.Kind == TypeKind.TypeParam)
        {
            r.Add("(" + a + " == " + b + " or " + a + " ~= " + a + " and " + b + " ~= " + b + ")");
            return r;
        }
        r.Add(a + " == " + b);
        return r;
    }

    // 型の型引数の既定値を受ける隠れ引数の名前 (new / ctor / static の member)
    public static List<string> TypeTypeArgDefaultParams(TypeDeclSym t)
    {
        var r = new List<string>();
        if (!PassesTypeArgDefaults(t)) return r;
        foreach (var tp in t.TypeParams) r.Add("__tcs_dc_" + tp);
        return r;
    }

    // ---- record class ----

    // record class の instance は class と同じ table + metatable
    private void VisitRecord(Node decl)
    {
        SetSource(decl);
        var symbol = decl.TypeDecl!;
        CurrentDecl = symbol;
        var name = TypeName(symbol);
        if (symbol.BaseClass != null) Internal("record base not ported");
        var info = BeginType(name, "record", symbol);
        AppendLine(name + ".__index = " + name);
        info.DefinitionKeys.Add("__index");
        EmitImplementedInterfaces(name, info, symbol);
        AppendLine("");

        var paramNames = new List<string>();
        if (decl.A != null && decl.A.List != null)
        {
            foreach (var p in decl.A.List) paramNames.Add(p.Text);
        }
        var hidden = TypeTypeArgDefaultParams(symbol);
        var ownFields = new List<string>();
        foreach (var h in hidden) ownFields.Add(h + " = " + h);
        foreach (var p in paramNames) ownFields.Add(p + " = " + p);
        info.DefinitionKeys.Add("new");
        var all = new List<string>();
        foreach (var p in paramNames) all.Add(p);
        foreach (var h in hidden) all.Add(h);
        AppendLine("function " + name + ".new(" + string.Join(", ", all) + ")");
        G.Indent++;
        AppendLine("local self = setmetatable({" + string.Join(", ", ownFields) + "}, " + name + ")");
        EmitInstanceRegistration(name);
        AppendLine("return self");
        G.Indent--;
        AppendLine("end");
        AppendLine("");

        // __eq: 実行時の型 (EqualityContract) の一致を先に見て、宣言した field を値で比べる
        info.DefinitionKeys.Add("__eq");
        var eqParts = new List<string>();
        eqParts.Add("getmetatable(a) == getmetatable(b)");
        foreach (var p in ValueEqualityPartsShallow(symbol.SelfType())) eqParts.Add(p);
        AppendLine("function " + name + ".__eq(a, b)");
        G.Indent++;
        AppendLine("return " + string.Join(" and ", eqParts));
        G.Indent--;
        AppendLine("end");
        AppendLine("");

        var staticFieldInits = new List<FieldInit>();
        if (decl.List != null)
        {
            foreach (var member in decl.List)
            {
                if (member.Kind == NodeKind.Field && IsStaticField(member) && member.List != null)
                {
                    foreach (var v in member.List)
                    {
                        var t = v.Sym != null ? v.Sym.Type : TypeSym.ErrorType;
                        staticFieldInits.Add(new FieldInit(v.Text, v.A, t));
                    }
                }
                else if (member.Kind == NodeKind.Property && member.Has(Mods.Static) && IsAutoProperty(member))
                {
                    var t = member.Sym != null ? member.Sym.Type : TypeSym.ErrorType;
                    staticFieldInits.Add(new FieldInit(member.Text, member.D, t));
                }
            }
        }
        EmitStaticPreZero(name, info, staticFieldInits);

        var operators = new List<Node>();
        if (decl.List != null)
        {
            foreach (var member in decl.List)
            {
                switch (member.Kind)
                {
                    case NodeKind.Method:
                        VisitMethod(name, member, false);
                        break;
                    case NodeKind.OperatorDecl:
                        operators.Add(member);
                        break;
                    case NodeKind.Field:
                        if (!IsStaticField(member)) Internal("record instance field not ported");
                        break;
                    case NodeKind.Property:
                        if (!(member.Has(Mods.Static) && IsAutoProperty(member))) Internal("record property not ported");
                        break;
                }
            }
        }
        EmitOperators(name, operators);
        DeferStaticFieldInitializers(name, info, staticFieldInits);
        _currentType = null;
        CurrentDecl = null;
    }

    // record の __eq の各 member の比較 (record の値の member は positional な property だけ)
    private static List<string> ValueEqualityPartsShallow(TypeSym t)
    {
        var r = new List<string>();
        foreach (var m in ValueMembers(t))
        {
            foreach (var p in MemberEqualityParts(m.Type, "a." + m.Name, "b." + m.Name)) r.Add(p);
        }
        return r;
    }

    private void EmitOperators(string name, List<Node> operators)
    {
        if (operators.Count > 0) Internal("operators not ported");
    }

    // `is I` / `as I` が引く、実装する interface の名前の集合
    private void EmitImplementedInterfaces(string name, EmittedTypeInfo info, TypeDeclSym symbol)
    {
        if (symbol.Interfaces.Count > 0) Internal("interfaces not ported");
    }

    // ---- static ----

    // C# は static field を default 値で先に初期化してから初期化子を宣言の順に実行する (循環の参照が nil にならない)
    private void EmitStaticPreZero(string name, EmittedTypeInfo info, List<FieldInit> staticFieldInits)
    {
        int start = G.Chunks.Count;
        foreach (var f in staticFieldInits)
        {
            var defaultValue = StaticDefaultLua(f.Type);
            if (defaultValue != "nil") AppendLine(name + "." + f.Name + " = " + defaultValue);
        }
        if (G.Chunks.Count == start) return;
        AppendLine("");
    }

    // static の pre-zero は型の定義より前に評価されるので、struct の zero 値は `S.new()` を呼ばず table の literal で書く
    public string StaticDefaultLua(TypeSym type)
    {
        if (IlTypes.IsUserStruct(type))
        {
            var entries = StructTypeArgEntries(type);
            foreach (var m in ValueMembers(type)) entries.Add(m.Name + " = " + StaticDefaultLua(m.Type));
            return "{" + string.Join(", ", entries) + "}";
        }
        return GetDefaultValueForType(type);
    }

    // generic な struct の zero 値が持つ型引数の既定値 (EmitStructNew と同じ key。可変な struct は記述子で、型の定義より前でも評価できる closure の形)
    private List<string> StructTypeArgEntries(TypeSym type)
    {
        var r = new List<string>();
        List<string>? names = null;
        if (type.Kind == TypeKind.Tuple)
        {
            names = new List<string>();
            for (int i = 0; i < type.Args!.Count; i++) names.Add("T" + (i + 1));
        }
        else if (type.Decl != null && PassesTypeArgDefaults(type.Decl) && type.Args != null) names = type.Decl.TypeParams;
        if (names == null) return r;
        for (int i = 0; i < names.Count; i++)
        {
            var a = type.Args![i];
            string value;
            if (NeedsTypeArgDescriptor(a))
                value = G.RenderStructDescriptor(new IlStructDescriptor(B.TypeName(a), B.StructTypeArgDefaults(a), B.StructCopiers(a, false), IlBuild.HasToStringOverride(a), null), true);
            else value = StaticDefaultLua(a);
            r.Add("__tcs_dc_" + names[i] + " = " + value);
        }
        return r;
    }

    public string GetDefaultValueForType(TypeSym? type)
    {
        if (type == null) return "nil";
        if (type.Kind == TypeKind.TypeParam)
        {
            if (B.TypeArgDefaultIl(type) != null) return G.RenderIl(B.DefaultIl(type));
            return "nil";
        }
        if (IlTypes.IsUserStruct(type))
        {
            var args = new List<string>();
            foreach (var a in B.StructTypeArgDefaults(type)) args.Add(G.RenderIl(a));
            return B.TypeName(type) + ".new(" + string.Join(", ", args) + ")";
        }
        if (type.Kind == TypeKind.Utf8Bytes) return "TinySystem.Utf8.Default()";
        // enum の default は member の値に依らず 0 (C#: default(E) == 0)
        if (type.Kind == TypeKind.Enum) return "0";
        switch (type.Kind)
        {
            case TypeKind.Bool: return "false";
            case TypeKind.Char: return "0";
            case TypeKind.Int:
            case TypeKind.Long:
            case TypeKind.UInt:
                return "0";
            // float は Lua でも float (integer の 0 は int と混ぜた演算が integer のまま進み、f32 へ丸まらない)
            case TypeKind.Float:
            case TypeKind.Double:
                return "0.0";
        }
        return "nil";
    }

    private void DeferStaticFieldInitializers(string name, EmittedTypeInfo info, List<FieldInit> staticFieldInits)
    {
        if (staticFieldInits.Count > 0) _pendingStaticInits.Add(new PendingStaticInit(name, info, staticFieldInits));
    }

    // 型の static の初期化子は、chunk の中の全型を定義してから型ごとにまとめて出す。順は StaticInitOrder
    private void FlushStaticInitializers()
    {
        if (_pendingStaticInits.Count == 0) return;
        var names = new List<string>();
        foreach (var p in _pendingStaticInits) names.Add(p.Name);
        var order = StaticInitOrder.Order(names, StaticInitDependencies);
        var pending = _pendingStaticInits;
        _pendingStaticInits = new List<PendingStaticInit>();
        foreach (var name in order)
        {
            PendingStaticInit? p = null;
            foreach (var x in pending)
            {
                if (x.Name == name) p = x;
            }
            if (p == null) continue;
            foreach (var i in p.Inits)
            {
                if (i.Init != null)
                {
                    SetSource(i.Init);
                    break;
                }
            }
            EmitStaticFieldInitializers(p.Name, p.Inits);
        }
    }

    // static field の初期化子は constructor / method / property / operator の後に宣言の順で実行する (C# の static constructor 相当)。
    // 初期化子の無い field は pre-zero の default だけで、ここでは代入しない
    private void EmitStaticFieldInitializers(string name, List<FieldInit> staticFieldInits)
    {
        foreach (var f in staticFieldInits)
        {
            if (f.Init != null) AppendLine(name + "." + f.Name + " = " + RenderExprViaIl(f.Init, true));
        }
        if (staticFieldInits.Count > 0) AppendLine("");
    }

    // 式 1 個 (field の初期化子 / parameter の既定値 / base の引数) の Lua の表記。
    // field / property の初期化子と base の引数は copyStruct (il-spec §10)
    public string RenderExprViaIl(Node expr, bool copyStruct)
    {
        var built = B.BuildExpr(expr);
        if (built != null) return G.RenderIl(copyStruct ? B.WrapStructCopy(expr, built) : built);
        ReportUnsupportedBody(expr);
        return "nil";
    }

    // ---- constructor ----

    private void EmitConstructor(string className, TypeDeclSym classSymbol, Node? ctor, List<FieldInit> fieldInits)
    {
        var ctorParams = new List<string>();
        if (ctor != null && ctor.List != null)
        {
            foreach (var p in ctor.List) ctorParams.Add(ParamLuaName(p));
        }
        var ctorName = ctor != null && ctor.Sym != null ? CtorName(ctor.Sym) : "new";
        AddDefinitionKey(ctorName);
        // generic な class の instance は型引数の既定値を self に持つ
        var typeArgParams = new List<string>();
        if (PassesTypeArgDefaults(classSymbol))
        {
            foreach (var tp in classSymbol.TypeParams) typeArgParams.Add("__tcs_dc_" + tp);
        }

        var all = new List<string>();
        foreach (var p in ctorParams) all.Add(p);
        foreach (var p in typeArgParams) all.Add(p);
        B.ScopeRoots.Clear();
        if (ctor != null) B.ScopeRoots.Add(ctor);
        AppendLine("function " + className + "." + ctorName + "(" + string.Join(", ", all) + ")");
        G.Indent++;
        BeginTraceFunction();
        if (ctor != null)
        {
            EmitParameterDefaults(ctor);
            EmitParameterEntryCopies(ctor);
        }
        List<IlStat>? remainingBody = null;
        if (ctor != null && ctor.D != null && ctor.D.Text == "base") Internal("base initializer not ported");
        remainingBody = EmitTableConstructedSelf(className, ctor, ctorParams, fieldInits, typeArgParams);
        EmitInstanceRegistration(className);

        if (remainingBody != null)
        {
            G.EmitIlBlock(new IlBlock(remainingBody), null);
        }
        else if (ctor != null && ctor.B != null)
        {
            EmitStatsViaIl(ctor.B);
        }
        else if (ctor != null && ctor.C != null)
        {
            EmitExprStatViaIl(ctor.C);
        }
        AppendLine("return self");
        EndTraceFunction();
        G.Indent--;
        AppendLine("end");
        AppendLine("");
    }

    // reload の migration 用の登録 (opt-in)
    private void EmitInstanceRegistration(string className)
    {
        if (InstanceRegistry) AppendLine("__tcs_instances[self] = " + className);
    }

    // 基底の無い class の instance は `setmetatable({f = v, ...}, C)` の 1 つの table constructor で作る。
    // ctor の本文の先頭にある `this.F = E` は、E が他の評価と順序を入れ替えても観測できない式なら F の既定値の代わりに畳む
    private List<IlStat>? EmitTableConstructedSelf(string className, Node? ctor, List<string> ctorParams, List<FieldInit> fieldInits, List<string> typeArgParams)
    {
        List<IlStat>? remainingBody = null;
        var foldedNames = new List<string>();
        var foldedValues = new List<IlExpr>();
        if (ctor != null && ctor.B != null)
        {
            var body = new List<IlStat>();
            if (B.BuildStatsInto(ctor.B.List, body))
            {
                var ps = new HashSet<string>();
                foreach (var p in ctorParams) ps.Add(p);
                int n = TakeFoldableAssignments(fieldInits, ps, PrimitiveParams(ctor), body, foldedNames, foldedValues);
                var rest = new List<IlStat>();
                for (int i = n; i < body.Count; i++) rest.Add(body[i]);
                remainingBody = rest;
            }
        }
        var entries = new List<string>();
        foreach (var p in typeArgParams) entries.Add(p + " = " + p);
        foreach (var f in fieldInits)
        {
            int k = foldedNames.IndexOf(f.Name);
            string value;
            if (k >= 0) value = G.RenderIl(foldedValues[k]);
            else if (f.Init != null) value = RenderExprViaIl(f.Init, true);
            else value = GetDefaultValueForType(f.Type);
            entries.Add(f.Name + " = " + value);
        }
        AppendLine("local self = setmetatable({" + string.Join(", ", entries) + "}, " + className + ")");
        return remainingBody;
    }

    // 先頭から連続する畳める代入の個数を返し、folded に field の名前 → 値を入れる
    private int TakeFoldableAssignments(List<FieldInit> fieldInits, HashSet<string> ctorParams, HashSet<string> primitiveParams,
        List<IlStat> body, List<string> foldedNames, List<IlExpr> foldedValues)
    {
        int count = 0;
        foreach (var stat in body)
        {
            if (!(stat is IlAssign a) || !(a.Target is IlField tf) || !(tf.Recv is IlVar rv) || rv.Name != "self") break;
            var name = tf.Name;
            int index = -1;
            for (int i = 0; i < fieldInits.Count; i++)
            {
                if (fieldInits[i].Name == name)
                {
                    index = i;
                    break;
                }
            }
            if (index < 0 || foldedNames.Contains(name) || !IsOrderIndependent(a.Value, ctorParams, primitiveParams)) break;
            // 畳むと既定値 / 初期化子の評価が消えるので、初期化子は副作用の無い式のときだけ捨てる
            var init = fieldInits[index].Init;
            if (init != null)
            {
                var initIl = B.BuildExpr(init);
                if (initIl == null || !IsOrderIndependent(initIl, new HashSet<string>(), new HashSet<string>())) break;
            }
            foldedNames.Add(name);
            foldedValues.Add(a.Value);
            count++;
        }
        return count;
    }

    // 副作用・field / static の読み書き・例外・this の参照を持たず、ctor の parameter と定数だけから決まる式。
    // 整数の除算 / 剰余は 0 除算の fault があるので除く。演算子の operand は primitiveParams (数値 / bool の parameter) と定数に限る
    private static bool IsOrderIndependent(IlExpr expr, HashSet<string> ctorParams, HashSet<string> primitiveParams)
    {
        switch (expr)
        {
            case IlLit: return true;
            case IlVar v: return ctorParams.Contains(v.Name);
            case IlParen p: return IsOrderIndependent(p.E, ctorParams, primitiveParams);
            case IlUn u: return IsOrderIndependent(u.E, primitiveParams, primitiveParams);
            case IlBin b:
                if (b.Op == IlBinOp.DivNum || b.Op == IlBinOp.RemNum) return false;
                return IsOrderIndependent(b.L, primitiveParams, primitiveParams) && IsOrderIndependent(b.R, primitiveParams, primitiveParams);
            case IlNumericConvert c: return IsOrderIndependent(c.Value, ctorParams, primitiveParams);
            case IlNullableWrap w: return IsOrderIndependent(w.E, ctorParams, primitiveParams);
            case IlStructCopy sc: return IsOrderIndependent(sc.E, ctorParams, primitiveParams);
            case IlTable t:
                foreach (var e in t.Entries)
                {
                    if (e.Key != null && !IsNonFaultingKey(e.Key)) return false;
                    if (!IsOrderIndependent(e.Value, ctorParams, primitiveParams)) return false;
                }
                return true;
        }
        return false;
    }

    private static HashSet<string> PrimitiveParams(Node ctor)
    {
        var r = new HashSet<string>();
        if (ctor.Sym == null || ctor.Sym.Params == null) return r;
        foreach (var p in ctor.Sym.Params)
        {
            switch (p.Type.Kind)
            {
                case TypeKind.Bool:
                case TypeKind.Byte:
                case TypeKind.Short:
                case TypeKind.Int:
                case TypeKind.UInt:
                case TypeKind.Long:
                case TypeKind.Float:
                case TypeKind.Double:
                    r.Add(p.Name);
                    break;
            }
        }
        return r;
    }

    // [k]=v は k が nil / NaN だと table の生成の途中で fault するので、そうならないと分かる文字列・有限の数・bool の literal だけ許す
    private static bool IsNonFaultingKey(IlExpr key)
    {
        if (!(key is IlLit lit)) return false;
        var text = lit.LuaText;
        if (text.StartsWith("\"") || text == "true" || text == "false") return true;
        return LuaNumbers.IsFiniteNumberText(text);
    }

    // C# の呼び出しは省略した引数を呼び出し側で埋める。host など C# の外から nil で届く呼び出しのため関数の冒頭でも補う
    private void EmitParameterDefaults(Node fn)
    {
        if (fn.List == null) return;
        foreach (var parameter in fn.List)
        {
            if (parameter.B == null) continue;
            var value = RenderExprViaIl(parameter.B, false);
            if (value == "nil") continue;
            var pn = ParamLuaName(parameter);
            AppendLine("if " + pn + " == nil then " + pn + " = " + value + " end");
        }
    }

    // 型引数の既定値を受ける型 (source の generic な class / struct)
    public static bool PassesTypeArgDefaults(TypeDeclSym? t)
    {
        if (t == null || t.TypeParams.Count == 0) return false;
        return t.DeclKind == DeclKinds.Class || t.DeclKind == DeclKinds.Struct || t.DeclKind == DeclKinds.RecordClass || t.DeclKind == DeclKinds.RecordStruct;
    }

    // 宣言の側の隠れ引数 (型の型引数 (ctor と static の member)、method の型引数の順)
    public static List<string> TypeArgDefaultParams(Symbol method)
    {
        var r = new List<string>();
        if ((method.Kind == SymKind.Ctor || method.IsStatic) && PassesTypeArgDefaults(method.Owner))
        {
            foreach (var tp in method.Owner!.TypeParams) r.Add("__tcs_dc_" + tp);
        }
        if (method.TypeParams != null)
        {
            foreach (var tp in method.TypeParams) r.Add("__tcs_dm_" + tp);
        }
        return r;
    }

    public static string ParamLuaName(Node p)
    {
        return p.Local != null ? p.Local.LuaName : p.Text;
    }

    private void EmitParameterEntryCopies(Node fn)
    {
        B.EmitParameterEntryCopies(fn);
    }

    // ---- method / property ----

    // explicitSelf: struct の member (metatable が無いので `:` の定義でなく明示の self 引数の自由関数)
    private void VisitMethod(string className, Node method, bool explicitSelf)
    {
        SetSource(method);
        var declared = method.Sym;
        var methodName = declared != null ? MethodName(declared) : method.Text;
        CurrentFunction = declared;
        bool isStatic = method.Has(Mods.Static);
        bool hasOut = false;
        var paramNames = new List<string>();
        if (method.List != null)
        {
            foreach (var p in method.List)
            {
                if (p.Has(Mods.Out)) hasOut = true;
                else paramNames.Add(ParamLuaName(p));
            }
        }
        if (declared != null)
        {
            foreach (var hp in TypeArgDefaultParams(declared)) paramNames.Add(hp);
        }
        var outerOnSelf = TypeArgDefaultsOnSelf;
        TypeArgDefaultsOnSelf = !isStatic;
        var sep = isStatic || explicitSelf ? "." : ":";
        if (explicitSelf && !isStatic) paramNames.Insert(0, "self");
        AddDefinitionKey(methodName);
        B.ScopeRoots.Clear();
        B.ScopeRoots.Add(method);
        AppendLine("function " + className + sep + methodName + "(" + string.Join(", ", paramNames) + ")");
        G.Indent++;
        BeginTraceFunction();
        EmitParameterDefaults(method);
        EmitParameterEntryCopies(method);
        if (hasOut)
        {
            Internal("out method not ported");
        }
        else if (method.B != null)
        {
            var body = new List<IlStat>();
            if (method.B.List == null || B.BuildStatsInto(method.B.List, body)) G.EmitIlBlock(new IlBlock(body), null);
            else ReportUnsupportedBody(method.B);
        }
        else if (method.C != null)
        {
            EmitReturnViaIl(method.C);
        }
        EndTraceFunction();
        G.Indent--;
        AppendLine("end");
        AppendLine("");
        TypeArgDefaultsOnSelf = outerOnSelf;
        CurrentFunction = null;
    }

    // user の型の method の Lua の名前 (参照実装の LuaNaming.MethodName)。同じ型に同名の method が複数あるときだけ
    // parameter の型を `__` でつないで付ける。interface の method を実装する method は interface の method の名前
    public string MethodName(Symbol m)
    {
        if (m.Kind != SymKind.Method) return m.Name;
        if (m.Owner != null && m.Owner.IsRef) return m.LuaName;
        var implemented = ImplementedInterfaceMethod(m);
        if (implemented != null) return MethodName(implemented);
        var owner = m.Owner;
        if (owner == null) return m.Name;
        int same = 0;
        foreach (var x in owner.Members)
        {
            if (x.Kind == SymKind.Method && x.Name == m.Name) same++;
        }
        if (same < 2) return m.Name;
        var parts = new List<string>();
        parts.Add(m.Name);
        if (m.Params != null)
        {
            foreach (var p in m.Params)
            {
                parts.Add("__");
                if (p.IsOut) parts.Add("out_");
                parts.Add(LuaNaming.TypeToken(p.Type));
            }
        }
        return string.Join("", parts);
    }

    // instance method が実装する interface の method (宣言の型の interface から)
    private Symbol? ImplementedInterfaceMethod(Symbol m)
    {
        if (m.IsStatic || m.Owner == null || m.Owner.IsInterface) return null;
        var ifaces = new List<TypeSym>();
        CollectAllInterfaces(m.Owner, ifaces);
        foreach (var iface in ifaces)
        {
            var decl = iface.Decl!;
            foreach (var cand in decl.Members)
            {
                if (cand.Kind != SymKind.Method || cand.Name != m.Name) continue;
                if (SameSignature(m, cand, iface)) return cand;
            }
        }
        return null;
    }

    public static void CollectAllInterfaces(TypeDeclSym t, List<TypeSym> into)
    {
        foreach (var i in t.Interfaces)
        {
            if (i.Decl == null) continue;
            bool seen = false;
            foreach (var x in into)
            {
                if (x.Decl == i.Decl) seen = true;
            }
            if (seen) continue;
            into.Add(i);
            CollectAllInterfaces(i.Decl, into);
        }
    }

    private static bool SameSignature(Symbol impl, Symbol iface, TypeSym ifaceType)
    {
        if (impl.ParamCount != iface.ParamCount) return false;
        for (int i = 0; i < impl.ParamCount; i++)
        {
            var it = iface.Params![i].Type.Subst(ifaceType.Args, null);
            if (!TypeSym.Same(impl.Params![i].Type, it)) return false;
        }
        return true;
    }

    private void EmitStatsViaIl(Node block)
    {
        var acc = new List<IlStat>();
        if (block.List == null || B.BuildStatsInto(block.List, acc)) G.EmitIlBlock(new IlBlock(acc), null);
        else ReportUnsupportedBody(block);
    }

    private void EmitReturnViaIl(Node expr)
    {
        var acc = new List<IlStat>();
        if (B.BuildReturnInto(expr, acc)) G.EmitIlBlock(new IlBlock(acc), null);
        else ReportUnsupportedBody(expr);
    }

    private void EmitExprStatViaIl(Node expr)
    {
        var acc = new List<IlStat>();
        if (B.BuildExprStatInto(expr, expr, acc))
        {
            foreach (var s in acc) G.EmitIlStat(s, true);
        }
        else ReportUnsupportedBody(expr);
    }

    private void VisitCustomProperty(string className, Node prop, bool explicitSelf)
    {
        Internal("custom property not ported");
    }

    private void VisitExpressionBodiedProperty(string className, Node prop, bool explicitSelf)
    {
        Internal("expression-bodied property not ported");
    }

    // ---- enum ----

    private void VisitEnum(Node enumDecl)
    {
        SetSource(enumDecl);
        var symbol = enumDecl.TypeDecl!;
        var name = TypeName(symbol);
        var info = BeginType(name, "enum", symbol);
        if (enumDecl.List != null)
        {
            foreach (var member in enumDecl.List)
            {
                int value = 0;
                if (member.Sym != null && member.Sym.ConstVal != null) value = member.Sym.ConstVal.I;
                var text = G.RenderIl(IlBuild.LitFromConst("" + value, false));
                AppendLine(name + "." + member.Text + " = " + text);
                info.DefinitionKeys.Add(member.Text);
                info.EnumMemberNames.Add(member.Text);
                info.EnumMemberValues.Add(value);
            }
        }
        AppendLine("");
        _currentType = null;
    }

    // ---- constructor の名前 ----

    // user の型の constructor の Lua 名。class は `new`、struct は `ctor`。constructor が複数ある型は、
    // 引数のあるものに method の overload と同じ規則で parameter の型を付ける (`new__int__float`)
    public string CtorName(Symbol ctor)
    {
        var owner = ctor.Owner!;
        var name = owner.IsStruct ? "ctor" : "new";
        int explicitCtors = 0;
        foreach (var c in owner.Ctors)
        {
            if (c.Decl != null && c.Decl.Kind == NodeKind.Ctor) explicitCtors++;
        }
        if (explicitCtors < 2 || ctor.ParamCount == 0) return name;
        var parts = new List<string>();
        parts.Add(name);
        foreach (var p in ctor.Params!) parts.Add("__" + LuaNaming.TypeToken(p.Type));
        return string.Join("", parts);
    }

    // ---- 仕上げ ----

    private string Finish()
    {
        FlushStaticInitializers();
        var text = TrimEndText(G.Text()) + "\n";
        return ApplyTypeLocals(text);
    }

    private static string TrimEndText(string s)
    {
        var u = new TinySystem.Text.Utf8Bytes(s);
        int n = u.Length;
        while (n > 0)
        {
            int c = u[n - 1];
            if (c == ' ' || c == '\n' || c == '\r' || c == '\t') n--;
            else break;
        }
        return u.Slice(0, n).ToString();
    }

    private string ApplyTypeLocals(string text)
    {
        if (_typeLocals.Count == 0 || _typeLocalsChunk < 0) return text;
        // 印の行は prelude の中にあり、その前の出力の長さは変わらない
        var before = new List<string>();
        for (int i = 0; i < _typeLocalsChunk; i++) before.Add(G.Chunks[i]);
        var prefix = string.Join("", before);
        int start = Strings.ByteLength(prefix);
        int skip = Strings.ByteLength(LuaPrelude.TypeLocalsPlaceholder);
        return prefix + "local " + string.Join(", ", _typeLocals) + Strings.From(text, start + skip);
    }
}
