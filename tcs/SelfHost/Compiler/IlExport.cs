using System.Collections.Generic;
using TinySystem.Text;

namespace TcsSelf;

// IL を読む backend (tcs c と tcs vm) への入力。
// 検査済みのプログラムの型ごとの metadata と、method の本文の IL を渡す

public sealed class ExField
{
    public string Name;
    public string Type;
    public bool IsStatic;
    public IlExpr? Init;
    public string? InitHash;

    public ExField(string name, string type, bool isStatic, IlExpr? init, string? initHash)
    {
        Name = name;
        Type = type;
        IsStatic = isStatic;
        Init = init;
        InitHash = initHash;
    }
}

public sealed class ExMethod
{
    public string Name = "";
    public bool IsStatic;
    public List<string> Parameters = new List<string>();
    public IlBlock? Body;
    public string ReturnType = "void";
    public List<string> ParameterTypes = new List<string>();
    public List<IlExpr?> ParameterDefaults = new List<IlExpr?>();
    public bool IsAbstract;
    public List<string> OutParameters = new List<string>();
}

public sealed class ExCtor
{
    public List<string> Parameters = new List<string>();
    public List<string> ParameterTypes = new List<string>();
    public IlBlock? Body;
    public List<IlExpr> BaseArgs = new List<IlExpr>();
    public List<IlExpr?> ParameterDefaults = new List<IlExpr?>();
    public string Name = "new";
}

// class / record class / interface / (--ref の) 外部の class
public sealed class ExClass
{
    public string Name = "";
    public string? BaseName;
    public List<ExField> Fields = new List<ExField>();
    public string LayoutHash = "";
    public List<ExMethod> Methods = new List<ExMethod>();
    public List<ExCtor> Ctors = new List<ExCtor>();
    public bool IsRecord;
    public List<string> Interfaces = new List<string>();
    public bool IsInterface;
    public bool IsExternal;
    public string? DisplayName;
    // 宣言のファイル (--trace がどの class を記録するかを決める)
    public string File = "";
}

public sealed class ExStruct
{
    public string Name = "";
    public List<ExField> Fields = new List<ExField>();
    public string LayoutHash = "";
    public List<ExMethod> Methods = new List<ExMethod>();
    public List<ExCtor> Ctors = new List<ExCtor>();
    public bool IsRecord;
    public string? DisplayName;
    public List<ExField> StaticFields = new List<ExField>();
}

public sealed class ExEnum
{
    public string Name = "";
    public List<string> MemberNames = new List<string>();
    public List<int> MemberValues = new List<int>();
    public string? DisplayName;
}

public sealed class ExForeignParameter
{
    public string Name;
    public string Type;
    public bool IsOut;
    public IlExpr? Default;

    public ExForeignParameter(string name, string type, bool isOut, IlExpr? dflt)
    {
        Name = name;
        Type = type;
        IsOut = isOut;
        Default = dflt;
    }
}

public sealed class ExForeignMethod
{
    public string Name = "";
    public string ReturnType = "";
    public List<ExForeignParameter> Parameters = new List<ExForeignParameter>();
    public string? Receiver;
    // C# の method の名前 (Name は host の名前)。interface の method (tcs VM が --ref の interface の method を呼ぶ) は Receiver が interface の表示名
    public string MethodName = "";
}

public sealed class ExForeignValue
{
    public string Name;
    public string Type;
    public bool HasConstant;
    public int Constant;

    public ExForeignValue(string name, string type, bool hasConstant, int constant)
    {
        Name = name;
        Type = type;
        HasConstant = hasConstant;
        Constant = constant;
    }
}

public sealed class ExportResult
{
    public List<ExClass> Classes = new List<ExClass>();
    public List<string> Diagnostics = new List<string>();
    public IlBlock? TopLevel;
    public List<ExStruct> Structs = new List<ExStruct>();
    public List<string> Enums = new List<string>();
    public List<ExEnum> EnumTypes = new List<ExEnum>();
    public List<ExForeignMethod> ForeignMethods = new List<ExForeignMethod>();
    public List<ExForeignValue> ForeignValues = new List<ExForeignValue>();
    // IL にできなかった本文の理由 (本文は null になる。調べるため)
    public List<string> InternalErrors = new List<string>();
    // --ref で読んだ型の host の関数の一覧 (HostSurface。module 単位の混在で C の module が VM に公開する)
    public List<ExHostFunction> HostFunctions = new List<ExHostFunction>();
}

// --ref の型の host の関数: Name は host の名前、TypeDisplay は型の表示名、Method は C# の method の名前
public sealed class ExHostFunction
{
    public string Name;
    public string TypeDisplay;
    public string Method;

    public ExHostFunction(string name, string typeDisplay, string method)
    {
        Name = name;
        TypeDisplay = typeDisplay;
        Method = method;
    }
}

// 束縛済みの compilation から export を作る。IL は引数の copy を残す設定で組み立てる
public sealed class IlExporter
{
    private readonly Compilation _c;
    private readonly IlContext Ctx = new IlContext();
    private readonly List<int> _emitted = new List<int>();
    // struct の型 (表示の名前) → instance の field の (名前, 型)。layout の hash で struct の中へ展開する
    private readonly Dictionary<string, List<ExField>> _structLayouts = new Dictionary<string, List<ExField>>();
    // IL にできなかった本文と式 (文の並びの block、式の本文、初期化子、既定値、base の引数、top-level の文)。tcs check が TCS1001 にする
    public readonly List<Node> UnsupportedBodies = new List<Node>();

    public IlExporter(Compilation c)
    {
        _c = c;
        Ctx.KeepsArgumentCopies = true;
    }

    public ExportResult Export()
    {
        var result = new ExportResult();
        HostSurface(result);
        // library は user の型より先
        for (int i = 0; i < _c.Files.Count; i++)
        {
            if (_c.Files[i].IsLibrary) _emitted.Add(i);
        }
        for (int i = 0; i < _c.Files.Count; i++)
        {
            if (!_c.Files[i].IsLibrary && !_c.Files[i].IsRef) _emitted.Add(i);
        }
        // IL の local の名前を決める (予約語と同じ名前の local の改名、LocalNames)
        var reserved = LocalNames.ReservedTypeNames(_c);
        foreach (var i in _emitted) LocalNames.AssignUnit(_c.Units[i], reserved);

        // struct / record struct の契約。layout は owner の layout hash へ推移的に展開するので先に全ての struct を集める
        var structDecls = new List<TypeDeclSym>();
        foreach (var i in _emitted) CollectTypes(_c.Units[i], structDecls, true, false, false);
        foreach (var t in structDecls)
        {
            var layout = new List<ExField>();
            foreach (var f in CollectFields(t, false))
            {
                if (!f.IsStatic) layout.Add(f);
            }
            _structLayouts[IlTypes.Display(t.SelfType())] = layout;
        }
        foreach (var t in structDecls) result.Structs.Add(ExportStruct(t));

        // enum の定数の表 (値と名前)
        foreach (var i in _emitted)
        {
            var enums = new List<TypeDeclSym>();
            CollectTypes(_c.Units[i], enums, false, false, true);
            foreach (var t in enums)
            {
                var info = new ExEnum();
                info.Name = t.IlName;
                info.DisplayName = IlTypes.Display(t.SelfType());
                foreach (var m in t.Members)
                {
                    if (m.Kind != SymKind.EnumMember) continue;
                    info.MemberNames.Add(m.Name);
                    info.MemberValues.Add(m.ConstVal != null ? m.ConstVal.I : 0);
                }
                result.Enums.Add(info.Name);
                result.EnumTypes.Add(info);
            }
        }

        // class と record class (ファイルの中の順)、次にそのファイルの interface
        var topLevel = new List<Node>();
        Node? topLevelUnit = null;
        foreach (var i in _emitted)
        {
            var classes = new List<TypeDeclSym>();
            CollectTypes(_c.Units[i], classes, false, true, false);
            foreach (var t in classes)
            {
                if (t.IsInterface) continue;
                result.Classes.Add(ExportClass(t));
            }
            foreach (var t in classes)
            {
                if (t.IsInterface) result.Classes.Add(ExportInterface(t));
            }
            var unit = _c.Units[i];
            if (unit.List2 != null && unit.List2.Count > 0)
            {
                foreach (var st in unit.List2) topLevel.Add(st);
                topLevelUnit = unit;
            }
        }
        if (topLevelUnit != null)
        {
            Enter(null, null, topLevelUnit, false);
            result.TopLevel = TopLevelStats(topLevel);
        }
        ExportForeign(result);
        foreach (var ie in Ctx.InternalErrors) result.InternalErrors.Add(ie);
        return result;
    }

    // ---- --ref (host) の宣言 ----

    // user のコードが使う host の method / static の値 / enum / data class。C は host の関数を extern で宣言する
    private readonly Dictionary<string, int> _methodKeys = new Dictionary<string, int>();
    // 同じ host の method の 2 回目からの使用は書き出し直さない (owner の番号と host の名前 (IlName) → 最初に見た symbol)
    private readonly Dictionary<string, Symbol> _methodSeen = new Dictionary<string, Symbol>();
    private readonly List<string> _methodSignatures = new List<string>();
    private readonly Dictionary<string, int> _valueKeys = new Dictionary<string, int>();
    private readonly Dictionary<string, int> _enumKeys = new Dictionary<string, int>();
    private readonly HashSet<int> _visited = new HashSet<int>();
    // 表に書いた host の enum (同じ型は同じ表なので 1 度だけ作る)
    private readonly HashSet<int> _enumsDone = new HashSet<int>();
    private readonly List<TypeDeclSym> _pending = new List<TypeDeclSym>();

    private void ExportForeign(ExportResult result)
    {
        bool anyRef = false;
        foreach (var f in _c.Files)
        {
            if (f.IsRef) anyRef = true;
        }
        if (!anyRef) return;
        var enums = new List<ExEnum>();
        foreach (var i in _emitted)
        {
            // ファイルの中の位置の順 (同じ位置は外側の式が先)
            var nodes = new List<Node>();
            var depths = new List<int>();
            CollectUses(_c.Units[i], 0, nodes, depths);
            SortByPosition(nodes, depths);
            foreach (var n in nodes)
            {
                if (!UseForeign(n, result, enums)) return;
            }
        }
        int k = 0;
        while (k < _pending.Count)
        {
            var t = _pending[k];
            k++;
            if (t.IsStruct)
            {
                // host の struct は field の layout だけ (C の struct の値として host と受け渡す。doc/object-model.md §5)
                var st = new ExStruct();
                st.Name = GlobalName(t);
                foreach (var f in CollectFields(t, false))
                {
                    if (!f.IsStatic) st.Fields.Add(f);
                }
                st.LayoutHash = LayoutHash(st.Fields);
                st.IsRecord = t.DeclKind == DeclKinds.RecordStruct;
                st.DisplayName = IlTypes.Display(t.SelfType());
                result.Structs.Add(st);
                foreach (var m in t.Members)
                {
                    if (m.Kind == SymKind.Field || m.Kind == SymKind.Property) ForeignType(m.Type, result, enums);
                }
                continue;
            }
            if (t.DeclKind != DeclKinds.Class) continue;
            // host の data class は field の layout だけ (本文は host が持つ。--ref の本文は束縛しないので IL にしない)
            var cls = new ExClass();
            cls.Name = GlobalName(t);
            cls.BaseName = t.BaseClass != null ? GlobalName(t.BaseClass) : null;
            cls.Fields = CollectFields(t, true);
            cls.LayoutHash = LayoutHash(cls.Fields);
            cls.IsExternal = true;
            foreach (var x in IlTypes.AllInterfaces(t)) cls.Interfaces.Add(IlTypes.Display(x));
            cls.DisplayName = IlTypes.Display(t.SelfType());
            result.Classes.Add(cls);
            if (t.BaseClass != null) ForeignType(t.BaseClass.SelfType(), result, enums);
            foreach (var m in t.Members)
            {
                if (m.Kind == SymKind.Field) ForeignType(m.Type, result, enums);
            }
        }
        foreach (var e in enums) result.EnumTypes.Add(e);
    }

    private void CollectUses(Node n, int depth, List<Node> nodes, List<int> depths)
    {
        if (n.Kind == NodeKind.Call || n.Kind == NodeKind.Member || n.Kind == NodeKind.Name || n.Kind == NodeKind.New || n.Kind == NodeKind.TypeName)
        {
            nodes.Add(n);
            depths.Add(depth);
        }
        if (n.A != null) CollectUses(n.A, depth + 1, nodes, depths);
        if (n.B != null) CollectUses(n.B, depth + 1, nodes, depths);
        if (n.C != null) CollectUses(n.C, depth + 1, nodes, depths);
        if (n.D != null) CollectUses(n.D, depth + 1, nodes, depths);
        if (n.List != null)
        {
            foreach (var x in n.List) CollectUses(x, depth + 1, nodes, depths);
        }
        if (n.List2 != null)
        {
            foreach (var x in n.List2) CollectUses(x, depth + 1, nodes, depths);
        }
        if (n.List3 != null)
        {
            foreach (var x in n.List3) CollectUses(x, depth + 1, nodes, depths);
        }
    }

    // 位置の昇順、同じ位置は浅いものが先 (挿入の整列で同じ key の順を保つ)
    private static void SortByPosition(List<Node> nodes, List<int> depths)
    {
        for (int i = 1; i < nodes.Count; i++)
        {
            var x = nodes[i];
            int d = depths[i];
            int j = i - 1;
            while (j >= 0 && (nodes[j].Pos > x.Pos || nodes[j].Pos == x.Pos && depths[j] > d))
            {
                nodes[j + 1] = nodes[j];
                depths[j + 1] = depths[j];
                j--;
            }
            nodes[j + 1] = x;
            depths[j + 1] = d;
        }
    }

    // false: 失敗 (同じ名前の host の overload) で、診断を足して打ち切る
    private bool UseForeign(Node n, ExportResult result, List<ExEnum> enums)
    {
        // 宣言の型の構文 (field や parameter の型) も host の型を使う
        if (n.Kind == NodeKind.TypeName)
        {
            if (n.Type != null && n.Type.Decl != null && n.Type.Decl.IsRef) ForeignType(n.Type, result, enums);
            return true;
        }
        if (n.Kind == NodeKind.Name || n.Kind == NodeKind.Member)
        {
            if (n.Info == RefKind.Type && n.Type != null && n.Type.Decl != null && n.Type.Decl.IsRef)
            {
                ForeignType(n.Type, result, enums);
                return true;
            }
        }
        var sym = n.Sym;
        if (sym == null || sym.Owner == null || !sym.Owner.IsRef) return true;
        if (sym.Kind == SymKind.Field || sym.Kind == SymKind.EnumMember)
        {
            ForeignType(sym.Type, result, enums);
            if (sym.IsStatic && !sym.Owner.IsEnum && sym.Kind == SymKind.Field)
            {
                var key = RefTypePath(sym.Owner) + "." + sym.IlName;
                AddValue(result, new ExForeignValue(key, IlTypes.Display(sym.Type), false, 0));
            }
            else ForeignType(sym.Owner.SelfType(), result, enums);
            return true;
        }
        if (sym.Kind != SymKind.Method && sym.Kind != SymKind.Ctor) return true;
        ForeignType(sym.Owner.SelfType(), result, enums);
        bool interfaceMethod = !sym.IsStatic && sym.Owner.DeclKind == DeclKinds.Interface;
        if (sym.Kind != SymKind.Method || !sym.IsStatic && sym.Owner.DeclKind != DeclKinds.Class && !interfaceMethod) return true;
        // 書き出した method の 2 回目からの使用 (型も書き出し済み)
        string seenKey = sym.Owner.Order + ":" + sym.IlName;
        Symbol? seen;
        if (_methodSeen.TryGetValue(seenKey, out seen) && seen == sym) return true;
        ForeignType(sym.Type, result, enums);
        if (sym.Params != null)
        {
            foreach (var p in sym.Params) ForeignType(p.Type, result, enums);
        }
        var methodKey = RefTypePath(sym.Owner) + "." + sym.IlName;
        var signature = sym.Owner.FullName + "." + Declarer.Signature(sym);
        int at;
        if (!_methodKeys.TryGetValue(methodKey, out at)) at = -1;
        if (at >= 0 && _methodSignatures[at] != signature)
        {
            result.Diagnostics.Add("C foreign overloads require distinct names: " + methodKey);
            return false;
        }
        var fm = new ExForeignMethod();
        fm.Name = methodKey;
        fm.ReturnType = IlTypes.Display(sym.Type);
        if (sym.Params != null)
        {
            var decl = sym.Decl;
            for (int i = 0; i < sym.Params.Count; i++)
            {
                var p = sym.Params[i];
                IlExpr? dflt = null;
                if (decl != null && decl.List != null && i < decl.List.Count && decl.List[i].B != null && decl.List[i].B!.Type != null)
                {
                    Enter(sym.Owner, null, decl, false);
                    dflt = Expr(decl.List[i].B);
                }
                fm.Parameters.Add(new ExForeignParameter(p.Name, IlTypes.Display(p.Type), p.IsOut, dflt));
            }
        }
        fm.Receiver = sym.IsStatic ? null : interfaceMethod ? IlTypes.Display(sym.Owner.SelfType()) : GlobalName(sym.Owner);
        fm.MethodName = sym.Name;
        if (at >= 0) result.ForeignMethods[at] = fm;
        else
        {
            _methodKeys[methodKey] = result.ForeignMethods.Count;
            _methodSignatures.Add(signature);
            result.ForeignMethods.Add(fm);
        }
        if (!_methodSeen.ContainsKey(seenKey)) _methodSeen[seenKey] = sym;
        return true;
    }

    private void AddValue(ExportResult result, ExForeignValue v)
    {
        int at;
        if (_valueKeys.TryGetValue(v.Name, out at)) result.ForeignValues[at] = v;
        else
        {
            _valueKeys[v.Name] = result.ForeignValues.Count;
            result.ForeignValues.Add(v);
        }
    }

    // 型が host の型なら、enum は定数の表に、class は外部の class として後で書き出す
    private void ForeignType(TypeSym t, ExportResult result, List<ExEnum> enums)
    {
        if (t.Kind == TypeKind.Array || t.Kind == TypeKind.Nullable)
        {
            if (t.Args != null && t.Args.Count > 0) ForeignType(t.Args[0], result, enums);
            return;
        }
        if (t.Args != null)
        {
            foreach (var a in t.Args) ForeignType(a, result, enums);
        }
        var d = t.Decl;
        if (d == null || !d.IsRef || d.IsStatic) return;
        if (d.IsEnum)
        {
            if (!_enumsDone.Add(d.Order)) return;
            var display = IlTypes.Display(d.SelfType());
            var info = new ExEnum();
            info.Name = display;
            foreach (var m in d.Members)
            {
                if (m.Kind != SymKind.EnumMember) continue;
                int value = m.ConstVal != null ? m.ConstVal.I : 0;
                var key = RefTypePath(d) + "." + m.IlName;
                AddValue(result, new ExForeignValue(key, display, true, value));
                info.MemberNames.Add(LuaNames.HostConst(m.Name));
                info.MemberValues.Add(value);
            }
            int at;
            if (_enumKeys.TryGetValue(display, out at)) enums[at] = info;
            else
            {
                _enumKeys[display] = enums.Count;
                enums.Add(info);
            }
            return;
        }
        if (!_visited.Add(d.Order)) return;
        _pending.Add(d);
    }

    // 型の global の名前 (namespace と外側の型を _ でつなぐ)。host の型も C の側ではこの名前で表す
    private static string GlobalName(TypeDeclSym t) => t.FullName.Replace(".", "_");

    // host の型の path (namespace と入れ子の型を host の名前の規則で . でつなぐ)。enum は入っている型か namespace の下 (global の enum は自分の名前)
    public static string RefTypePath(TypeDeclSym t)
    {
        var parts = new List<string>();
        TypeDeclSym? cur = t;
        bool skipSelf = t.IsEnum && (t.Outer != null || t.Namespace != "");
        if (skipSelf) cur = t.Outer;
        while (cur != null)
        {
            parts.Insert(0, LuaNames.HostPathSegment(cur.Name));
            cur = cur.Outer;
        }
        if (t.Namespace != "")
        {
            var ns = t.Namespace.Split('.');
            for (int i = ns.Length - 1; i >= 0; i--) parts.Insert(0, LuaNames.HostPathSegment(ns[i]));
        }
        return string.Join(".", parts);
    }

    // ---- 型の収集 (ファイルの中の宣言の順、入れ子の型は外側の型の後) ----

    // --ref で読んだ generic でない class と interface の、host の関数 (class の公開 static method と、受け手を先頭の引数に取る interface の method)
    private void HostSurface(ExportResult result)
    {
        for (int i = 0; i < _c.Files.Count; i++)
        {
            if (_c.Files[i].IsRef) HostSurfaceTypes(_c.Units[i], result);
        }
    }

    private void HostSurfaceTypes(Node container, ExportResult result)
    {
        if (container.List == null) return;
        foreach (var n in container.List)
        {
            if (n.Kind == NodeKind.Namespace)
            {
                HostSurfaceTypes(n, result);
                continue;
            }
            var t = n.TypeDecl;
            if (t == null) continue;
            if ((t.DeclKind == DeclKinds.Class || t.DeclKind == DeclKinds.Interface) && t.TypeParams.Count == 0)
            {
                bool iface = t.DeclKind == DeclKinds.Interface;
                foreach (var m in t.Members)
                {
                    if (m.Kind != SymKind.Method || m.Decl == null || m.Decl.Kind != NodeKind.Method) continue;
                    if (iface ? m.IsStatic : !m.IsStatic || !m.Decl.Has(Mods.Public)) continue;
                    result.HostFunctions.Add(new ExHostFunction(RefTypePath(t) + "." + m.IlName, IlTypes.Display(t.SelfType()), m.Name));
                }
            }
            HostSurfaceTypes(n, result);
        }
    }

    private void CollectTypes(Node container, List<TypeDeclSym> into, bool structs, bool classes, bool enums)
    {
        if (container.List == null) return;
        foreach (var n in container.List)
        {
            if (n.Kind == NodeKind.Namespace)
            {
                CollectTypes(n, into, structs, classes, enums);
                continue;
            }
            if (n.Kind != NodeKind.TypeDecl || n.TypeDecl == null) continue;
            var t = n.TypeDecl;
            if (structs && t.IsStruct) into.Add(t);
            if (classes && (t.DeclKind == DeclKinds.Class || t.DeclKind == DeclKinds.RecordClass || t.IsInterface)) into.Add(t);
            if (enums && t.IsEnum) into.Add(t);
            CollectTypes(n, into, structs, classes, enums);
        }
    }

    // ---- IL の組み立ての文脈 ----

    private void Enter(TypeDeclSym? type, Symbol? function, Node unitOrMember, bool onSelf)
    {
        Ctx.CurrentDecl = type;
        Ctx.CurrentFunction = function;
        // export は型引数の既定値を常に変数 (constructor と method の引数) から読む。onSelf は使わない
        Ctx.TypeArgDefaultsOnSelf = false;
        Ctx.CurrentLambdaVoid = 0;
        Ctx.B.ScopeRoots.Clear();
        Ctx.B.ScopeRoots.Add(unitOrMember);
    }

    // 文の並びの本文 (block)
    private IlBlock? Block(Node block)
    {
        var acc = new List<IlStat>();
        if (!Ctx.B.BuildStatsInto(block.List, acc))
        {
            UnsupportedBodies.Add(block);
            return null;
        }
        return new IlBlock(acc);
    }

    // top-level の文は 1 つずつ組み立て、IL にできない文をそれぞれ記録する
    private IlBlock? TopLevelStats(List<Node> statements)
    {
        var acc = new List<IlStat>();
        bool built = true;
        foreach (var st in statements)
        {
            if (Ctx.B.BuildStatInto(st, acc)) continue;
            UnsupportedBodies.Add(st);
            built = false;
        }
        return built ? new IlBlock(acc) : null;
    }

    private IlExpr? Expr(Node? e)
    {
        if (e == null) return null;
        var built = Ctx.B.BuildExpr(e);
        if (built == null) UnsupportedBodies.Add(e);
        return built;
    }

    // accessor の式の本文: get は値の return (条件式を文に直さない)、set は文の位置の式
    private IlBlock? AccessorExpr(Node expr, bool isGet)
    {
        var acc = new List<IlStat>();
        if (isGet)
        {
            var built = Expr(expr);
            if (built == null) return null;
            acc.Add(new IlReturn(Ctx.B.WrapStructCopy(expr, built), null));
            return new IlBlock(acc);
        }
        if (Ctx.B.BuildExprStatInto(expr, expr, acc)) return new IlBlock(acc);
        UnsupportedBodies.Add(expr);
        return null;
    }

    // ---- field ----

    // instance / static の field と自動実装の property。positional な record は parameter が先頭
    private List<ExField> CollectFields(TypeDeclSym t, bool withInitializers)
    {
        var fields = new List<ExField>();
        var decl = t.Decl;
        if (t.IsRecord && decl.A != null && decl.A.List != null && t.RecordParams != null)
        {
            for (int i = 0; i < decl.A.List.Count && i < t.RecordParams.Count; i++)
                fields.Add(new ExField(RecordFieldName(t, decl.A.List[i].Text), IlTypes.Display(t.RecordParams[i].Type), false, null, null));
        }
        if (decl.List == null) return fields;
        foreach (var m in decl.List)
        {
            if (m.Kind == NodeKind.Field && m.List != null)
            {
                foreach (var v in m.List)
                {
                    var sym = v.Sym;
                    // struct の const は使う所で値に畳まれ、値の layout に入らない
                    if (sym != null && sym.IsConst && t.IsStruct) continue;
                    bool isStatic = sym != null ? sym.IsStatic || sym.IsConst : m.Has(Mods.Static);
                    IlExpr? init = null;
                    if (withInitializers && v.A != null && v.A.Type != null)
                    {
                        // instance の初期化子は constructor の中で走るので、型引数の既定値は constructor の引数から読む
                        Enter(t, null, m, false);
                        init = Expr(v.A);
                    }
                    fields.Add(new ExField(sym != null ? FieldName(t, sym) : v.Text, sym != null ? IlTypes.Display(sym.Type) : "?", isStatic, init, v.A != null ? InitializerHash(v.A, m.Has(Mods.Const)) : null));
                }
            }
            else if (m.Kind == NodeKind.Property && IsAutoProperty(m))
            {
                var sym = m.Sym;
                bool isStatic = m.Has(Mods.Static);
                IlExpr? init = null;
                if (withInitializers && m.D != null && m.D.Type != null)
                {
                    Enter(t, null, m, false);
                    init = Expr(m.D);
                }
                var initNode = m.D;
                fields.Add(new ExField(sym != null ? FieldName(t, sym) : m.Text, sym != null ? IlTypes.Display(sym.Type) : "?", isStatic, init, initNode != null ? InitializerHash(initNode, false) : null));
            }
        }
        return fields;
    }

    // accessor が本文を持たない property (式の本文の property も除く)。B = get、C = set / init、D = 初期化子
    private static bool IsAutoProperty(Node p)
    {
        if (p.Value == 1) return false;
        if (p.B == null && p.C == null) return false;
        return (p.B == null || p.B.A == null && p.B.B == null) && (p.C == null || p.C.A == null && p.C.B == null);
    }

    // --ref (host) の型の class は field の名前も host の規則
    private static string FieldName(TypeDeclSym t, Symbol sym)
    {
        return t.IsRef ? sym.IlName : sym.Name;
    }

    // positional な record の parameter の field (--ref の型は property の host の名前)
    private static string RecordFieldName(TypeDeclSym t, string name)
    {
        if (!t.IsRef) return name;
        foreach (var m in t.Members)
        {
            if (m.Kind == SymKind.Property && m.Name == name) return m.IlName;
        }
        return name;
    }

    // 初期化子の hash (hot reload が既存の static の初期化子の変更を restart と判定する)。定数は値で、それ以外は字面で決める
    public static string InitializerHash(Node init, bool isConst)
    {
        string kind = isConst ? "const-field " : "";
        string text;
        if (init.HasConst && init.Const != null)
        {
            switch (init.Const.Kind)
            {
                case ConstKind.Null: text = "null"; break;
                case ConstKind.Bool: text = "bool " + (init.Const.B ? "True" : "False"); break;
                case ConstKind.String: text = "string " + init.Const.S; break;
                case ConstKind.Int: text = "int " + init.Const.I; break;
                default: text = NormalizedText(init); break;
            }
        }
        else text = NormalizedText(init);
        return Hash64(kind + text);
    }

    // trivia (空白と comment) を除いた token の列。token の境目は長さで区切る
    private static string NormalizedText(Node init)
    {
        var tokens = new Lexer(init.File, new Diagnostics(), init.Pos, init.End).Tokenize();
        var parts = new List<string>();
        foreach (var t in tokens)
        {
            if (t.Kind == TokenKind.End) continue;
            string piece = init.File.Slice(t.Pos, t.End);
            parts.Add(new Utf8Bytes(piece).Length + ":" + piece);
        }
        return string.Join("", parts);
    }

    // FNV-1a (32 bit を 2 つ。暗号の強さは要らない)。int の演算は 32 bit で wrap する (C backend は uint を持たない)
    private const int FnvOffset = -2128831035;
    private const int FnvPrime = 16777619;

    public static string Hash64(string s)
    {
        var u = new Utf8Bytes(s);
        int a = FnvOffset;
        int b = FnvOffset;
        for (int i = 0; i < u.Length; i++)
        {
            a = (a ^ u[i]) * FnvPrime;
            b = (b ^ u[u.Length - 1 - i]) * FnvPrime;
        }
        return Hex8(a) + Hex8(b);
    }

    private static string Hex8(int v)
    {
        const string digits = "0123456789abcdef";
        var d = new Utf8Bytes(digits);
        var parts = new List<string>();
        for (int shift = 28; shift >= 0; shift -= 4)
        {
            int nibble = (v >> shift) & 15;
            parts.Add(d.Slice(nibble, 1).ToString());
        }
        return string.Join("", parts);
    }

    // ---- layout の hash (il-spec §14) ----

    // instance の field の (名前, 型) の列の FNV-1a。struct の型の field は中の layout へ推移的に展開する
    private string LayoutHash(List<ExField> fields)
    {
        int h = FnvOffset;
        foreach (var f in fields)
        {
            if (f.IsStatic) continue;
            h = HashText(h, f.Name + ":");
            h = HashType(h, f.Type, new List<string>());
            h = (h ^ ';') * FnvPrime;
        }
        return Hex8(h);
    }

    private static int HashText(int h, string s)
    {
        var u = new Utf8Bytes(s);
        for (int i = 0; i < u.Length; i++) h = (h ^ u[i]) * FnvPrime;
        return h;
    }

    private int HashType(int h, string type, List<string> expanding)
    {
        List<ExField>? layout;
        if (!_structLayouts.TryGetValue(type, out layout) || expanding.Contains(type)) return HashText(h, type);
        expanding.Add(type);
        h = (h ^ '{') * FnvPrime;
        foreach (var f in layout)
        {
            h = HashText(h, f.Name + ":");
            h = HashType(h, f.Type, expanding);
            h = (h ^ ';') * FnvPrime;
        }
        h = (h ^ '}') * FnvPrime;
        expanding.Remove(type);
        return h;
    }

    // ---- 型 ----

    private ExClass ExportClass(TypeDeclSym t)
    {
        var info = new ExClass();
        info.Name = t.IlName;
        info.File = t.Decl.File.Path;
        info.BaseName = t.BaseClass != null ? t.BaseClass.IlName : null;
        info.Fields = CollectFields(t, true);
        info.LayoutHash = LayoutHash(info.Fields);
        info.Methods = CollectMethods(t);
        info.Ctors = BuildCtors(t);
        info.IsRecord = t.DeclKind == DeclKinds.RecordClass;
        foreach (var i in IlTypes.AllInterfaces(t)) info.Interfaces.Add(IlTypes.Display(i));
        // record は IEquatable<自分> を実装する (C# が合成する)
        if (info.IsRecord) info.Interfaces.Add("System.IEquatable<" + IlTypes.Display(t.SelfType()) + ">");
        info.DisplayName = IlTypes.Display(t.SelfType());
        return info;
    }

    // struct / record struct: field (positional を含む) と member と constructor
    private ExStruct ExportStruct(TypeDeclSym t)
    {
        var info = new ExStruct();
        info.Name = t.IlName;
        var all = CollectFields(t, true);
        foreach (var f in all)
        {
            if (f.IsStatic) info.StaticFields.Add(f);
            else info.Fields.Add(f);
        }
        info.LayoutHash = LayoutHash(info.Fields);
        info.Methods = CollectMethods(t);
        info.Ctors = BuildCtors(t);
        info.IsRecord = t.DeclKind == DeclKinds.RecordStruct;
        info.DisplayName = IlTypes.Display(t.SelfType());
        return info;
    }

    // 派生した interface の型を通しても継承した member を呼べるように、自分の表に写す
    private ExClass ExportInterface(TypeDeclSym t)
    {
        var info = new ExClass();
        info.Name = t.IlName;
        info.IsInterface = true;
        info.LayoutHash = "0";
        info.DisplayName = IlTypes.Display(t.SelfType());
        var owners = new List<TypeDeclSym>();
        owners.Add(t);
        foreach (var i in IlTypes.AllInterfaces(t))
        {
            if (i.Decl != null) owners.Add(i.Decl);
        }
        var names = new List<string>();
        foreach (var owner in owners)
        {
            bool inherited = owner != t;
            foreach (var member in owner.Members)
            {
                foreach (var m in ExportInterfaceMember(member, inherited))
                {
                    int at = names.IndexOf(m.Name);
                    if (at >= 0) info.Methods[at] = m;
                    else
                    {
                        names.Add(m.Name);
                        info.Methods.Add(m);
                    }
                }
            }
        }
        return info;
    }

    private List<ExMethod> ExportInterfaceMember(Symbol member, bool inherited)
    {
        var r = new List<ExMethod>();
        if (member.Kind == SymKind.Property && !member.IsStatic)
        {
            var type = IlTypes.Display(member.Type);
            if (member.HasGetter)
            {
                var g = new ExMethod();
                g.Name = "get_" + member.Name;
                g.ReturnType = type;
                g.IsAbstract = true;
                r.Add(g);
            }
            if (member.HasSetter || member.IsInit)
            {
                var s = new ExMethod();
                s.Name = "set_" + member.Name;
                s.Parameters.Add("value");
                s.ParameterTypes.Add(type);
                s.IsAbstract = true;
                r.Add(s);
            }
            return r;
        }
        if (member.Kind == SymKind.Method && !(inherited && member.IsStatic) && member.Decl != null && member.Decl.Kind == NodeKind.Method)
        {
            var decl = member.Decl;
            var m = new ExMethod();
            m.Name = IlNames.MethodName(member);
            m.IsStatic = member.IsStatic;
            m.ReturnType = IlTypes.Display(member.Type);
            m.IsAbstract = member.IsStatic ? decl.B == null && decl.C == null : true;
            if (member.Params != null)
            {
                for (int i = 0; i < member.Params.Count; i++)
                {
                    m.Parameters.Add(member.Params[i].Name);
                    m.ParameterTypes.Add(IlTypes.Display(member.Params[i].Type));
                    if (member.Params[i].IsOut) m.OutParameters.Add(member.Params[i].Name);
                }
            }
            Enter(member.Owner, member, decl, !member.IsStatic);
            m.ParameterDefaults = ParameterDefaults(decl);
            r.Add(m);
        }
        return r;
    }

    private List<IlExpr?> ParameterDefaults(Node fn)
    {
        var r = new List<IlExpr?>();
        if (fn.List == null) return r;
        foreach (var p in fn.List) r.Add(p.B != null ? Expr(p.B) : null);
        return r;
    }

    private List<ExMethod> CollectMethods(TypeDeclSym t)
    {
        var methods = new List<ExMethod>();
        var decl = t.Decl;
        // interface の property を自動実装の property か field で実装する: accessor を足す (interface 経由の読み書き)
        if (!t.IsInterface && !t.IsStruct)
        {
            // 済みの accessor は "get_X" / "set_X" で数える (getter と setter を別々の interface が求めることがある)
            var done = new List<string>();
            foreach (var i in IlTypes.AllInterfaces(t))
            {
                foreach (var p in i.Decl!.Members)
                {
                    if (p.Kind != SymKind.Property) continue;
                    var impl = ImplementingMember(t, p.Name);
                    if (impl == null || IlBuild.IsCustomProperty(impl)) continue;
                    var type = IlTypes.Display(impl.Type);
                    if (p.HasGetter && !done.Contains("get_" + p.Name))
                    {
                        done.Add("get_" + p.Name);
                        var g = new ExMethod();
                        g.Name = "get_" + p.Name;
                        g.ReturnType = type;
                        var body = new List<IlStat>();
                        body.Add(new IlReturn(new IlField(new IlVar("self", null), impl.Name, null), null));
                        g.Body = new IlBlock(body);
                        methods.Add(g);
                    }
                    if ((p.HasSetter || p.IsInit) && !done.Contains("set_" + p.Name))
                    {
                        done.Add("set_" + p.Name);
                        var s = new ExMethod();
                        s.Name = "set_" + p.Name;
                        s.Parameters.Add("value");
                        s.ParameterTypes.Add(type);
                        var body = new List<IlStat>();
                        body.Add(new IlAssign(new IlField(new IlVar("self", null), impl.Name, null), new IlVar("value", null), null));
                        s.Body = new IlBlock(body);
                        methods.Add(s);
                    }
                }
            }
        }
        if (decl.List == null) return methods;
        // accessor が本文を持つ property は get_ / set_ の method
        foreach (var m in decl.List)
        {
            if (m.Kind != NodeKind.Property || IsAutoProperty(m) || m.Value == 1 || (m.B == null && m.C == null)) continue;
            bool isStatic = m.Has(Mods.Static);
            var type = m.Sym != null ? IlTypes.Display(m.Sym.Type) : "?";
            var accessors = new List<Node>();
            if (m.B != null) accessors.Add(m.B);
            if (m.C != null) accessors.Add(m.C);
            if (m.B != null && m.C != null && m.C.Pos < m.B.Pos)
            {
                accessors.Clear();
                accessors.Add(m.C);
                accessors.Add(m.B);
            }
            foreach (var acc in accessors)
            {
                bool isGet = acc == m.B;
                var x = new ExMethod();
                x.Name = (isGet ? "get_" : "set_") + m.Text;
                x.IsStatic = isStatic;
                if (!isGet)
                {
                    x.Parameters.Add("value");
                    x.ParameterTypes.Add(type);
                }
                x.ReturnType = isGet ? type : "void";
                Enter(t, isGet ? m.Sym : null, acc, !isStatic);
                if (acc.A != null) x.Body = Block(acc.A);
                else if (acc.B != null) x.Body = AccessorExpr(acc.B, isGet);
                methods.Add(x);
            }
        }
        // 式の本文の property (`int X => ...`) は getter だけ
        foreach (var m in decl.List)
        {
            if (m.Kind != NodeKind.Property || m.Value != 1 || m.B == null || m.B.B == null) continue;
            bool isStatic = m.Has(Mods.Static);
            var x = new ExMethod();
            x.Name = "get_" + m.Text;
            x.IsStatic = isStatic;
            x.ReturnType = m.Sym != null ? IlTypes.Display(m.Sym.Type) : "?";
            Enter(t, m.Sym, m.B, !isStatic);
            x.Body = AccessorExpr(m.B.B, true);
            methods.Add(x);
        }
        // user の演算子は IL の名前 (`__add`、overload は `__mul_1` …) の static な method
        foreach (var m in decl.List)
        {
            if (m.Kind != NodeKind.OperatorDecl) continue;
            var meta = IlNames.OperatorName(m);
            if (meta == null) continue;
            var x = new ExMethod();
            x.Name = meta;
            x.IsStatic = true;
            if (m.List != null)
            {
                foreach (var p in m.List) x.Parameters.Add(IlNames.ParamName(p));
            }
            x.ReturnType = m.Sym != null ? IlTypes.Display(m.Sym.Type) : "?";
            if (m.Sym != null && m.Sym.Params != null)
            {
                foreach (var p in m.Sym.Params) x.ParameterTypes.Add(IlTypes.Display(p.Type));
            }
            Enter(t, m.Sym, m, false);
            if (m.B != null) x.Body = Block(m.B);
            else if (m.C != null) x.Body = AccessorExpr(m.C, true);
            methods.Add(x);
        }
        foreach (var m in decl.List)
        {
            if (m.Kind != NodeKind.Method) continue;
            var sym = m.Sym;
            var x = new ExMethod();
            x.Name = sym != null ? IlNames.MethodName(sym) : m.Text;
            x.IsStatic = m.Has(Mods.Static);
            if (m.List != null)
            {
                foreach (var p in m.List)
                {
                    x.Parameters.Add(IlNames.ParamName(p));
                    if (p.Has(Mods.Out)) x.OutParameters.Add(IlNames.ParamName(p));
                }
            }
            x.ReturnType = sym != null ? IlTypes.Display(sym.Type) : "void";
            if (sym != null && sym.Params != null)
            {
                foreach (var p in sym.Params) x.ParameterTypes.Add(IlTypes.Display(p.Type));
            }
            x.IsAbstract = m.Has(Mods.Abstract) || t.IsInterface && m.B == null && m.C == null;
            Enter(t, sym, m, !x.IsStatic);
            x.Body = MethodBody(m, sym);
            x.ParameterDefaults = ParameterDefaults(m);
            methods.Add(x);
        }
        return methods;
    }

    // 型が自分で宣言した、名前の同じ property か field (interface の property の暗黙の実装)
    private static Symbol? ImplementingMember(TypeDeclSym t, string name)
    {
        var list = t.Lookup(name);
        if (list == null) return null;
        foreach (var s in list)
        {
            if (s.Kind == SymKind.Property || s.Kind == SymKind.Field) return s;
        }
        return null;
    }

    private IlBlock? MethodBody(Node m, Symbol? sym)
    {
        if (m.B != null) return Block(m.B);
        if (m.C == null) return null;
        bool hasOut = false;
        if (m.List != null)
        {
            foreach (var p in m.List)
            {
                if (p.Has(Mods.Out)) hasOut = true;
            }
        }
        if (hasOut && sym != null && sym.Type.Kind == TypeKind.Void) return AccessorExpr(m.C, false);
        var acc = new List<IlStat>();
        if (Ctx.B.BuildReturnInto(m.C, acc)) return new IlBlock(acc);
        UnsupportedBodies.Add(m.C);
        return null;
    }

    private List<ExCtor> BuildCtors(TypeDeclSym t)
    {
        var r = new List<ExCtor>();
        var decl = t.Decl;
        if (t.IsRecord && decl.A != null && decl.A.List != null && decl.A.List.Count > 0)
        {
            // positional な constructor: 宣言の順に field へ代入する
            var ctor = new ExCtor();
            var body = new List<IlStat>();
            for (int i = 0; i < decl.A.List.Count; i++)
            {
                var p = decl.A.List[i];
                ctor.Parameters.Add(p.Text);
                ctor.ParameterTypes.Add(t.RecordParams != null && i < t.RecordParams.Count ? IlTypes.Display(t.RecordParams[i].Type) : "?");
                body.Add(new IlAssign(new IlField(new IlVar("self", null), p.Text, null), new IlVar(p.Text, null), null));
            }
            ctor.Body = new IlBlock(body);
            Enter(t, null, decl, false);
            foreach (var p in decl.A.List) ctor.ParameterDefaults.Add(p.B != null ? Expr(p.B) : null);
            ctor.Name = t.IsStruct ? "ctor" : "new";
            r.Add(ctor);
            return r;
        }
        if (decl.List == null) return r;
        // struct の引数の無い constructor は準拠検査が止める。引数のある instance の constructor だけ
        foreach (var m in decl.List)
        {
            if (m.Kind != NodeKind.Ctor || m.Has(Mods.Static)) continue;
            var ctor = new ExCtor();
            if (m.List != null)
            {
                foreach (var p in m.List) ctor.Parameters.Add(IlNames.ParamName(p));
            }
            if (m.Sym != null && m.Sym.Params != null)
            {
                foreach (var p in m.Sym.Params) ctor.ParameterTypes.Add(IlTypes.Display(p.Type));
            }
            Enter(t, m.Sym, m, true);
            if (m.B != null) ctor.Body = Block(m.B);
            // 式の本文 (`C(int x) => X = x;`) は文の位置の式
            else if (m.C != null) ctor.Body = AccessorExpr(m.C, false);
            if (m.D != null && m.D.Text == "base" && m.D.List != null)
            {
                var args = new List<IlExpr>();
                bool ok = true;
                foreach (var a in m.D.List)
                {
                    var built = a.A != null ? Expr(a.A) : null;
                    if (built == null) ok = false;
                    else args.Add(built);
                }
                if (ok) ctor.BaseArgs = args;
            }
            ctor.ParameterDefaults = ParameterDefaults(m);
            ctor.Name = m.Sym != null ? IlNames.CtorName(m.Sym) : "new";
            r.Add(ctor);
        }
        return r;
    }
}
