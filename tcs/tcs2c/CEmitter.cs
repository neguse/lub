using System.Globalization;
using System.Text;
using TinyCs;

namespace TinyCs.Tcs2c;

internal sealed partial class CEmitter
{
    private readonly IlExportResult _program;
    private readonly ContractFacts _facts;
    private readonly Dictionary<string, IlClassInfo> _classes;
    private readonly bool _digestF32;
    private readonly StringBuilder _output = new();
    private readonly Stack<Dictionary<string, Variable>> _scopes = new();
    private readonly Stack<string?> _continueTargets = new();
    // break の飛び先: ループは null (`break;`)、IlBreakScope は label (`goto`)
    private readonly Stack<string?> _breakTargets = new();
    private IlClassInfo _currentClass = null!;
    private IlMethodInfo _currentMethod = null!;
    private MethodFact _currentMethodFact = null!;
    private int _indent;
    private int _serial;

    private readonly List<string> _pendingClosures = [];
    private readonly List<string> _closureDecls = [];
    private const string ClosureDeclMarker = "/*__TCS2C_CLOSURE_DECLS__*/";
    private readonly HashSet<string> _capturedNames = [];
    private int _closureSerial;

    private sealed class Variable(string cName, CType type)
    {
        public bool Boxed { get; init; }
        public string CName { get; } = cName;
        public CType Type { get; set; } = type;
    }

    public CEmitter(IlExportResult program, bool digestF32)
    {
        // ctor と top-level 文を合成 method として注入し、
        // facts / prototype / EmitMethod の既存機構をそのまま通す
        _program = Normalize(program);
        _digestF32 = digestF32;
        _facts = new ContractFacts(_program);
        _classes = new Dictionary<string, IlClassInfo>(_facts.Classes);
        BuildHierarchy();
    }

    internal const string CtorMethodName = "__ctor";

    // ---- 継承: DFS 範囲型 ID と chain 解決 ----
    private readonly Dictionary<string, (int First, int Last)> _typeRange = new();

    private void BuildHierarchy()
    {
        var children = new Dictionary<string, List<string>>();
        var roots = new List<string>();
        foreach (var cls in _program.Classes)
        {
            if (cls.BaseName is { } b)
            {
                if (!_classes.ContainsKey(b))
                    throw new Tcs2cException($"unknown base class: {cls.Name} : {b}");
                (children.TryGetValue(b, out var list)
                    ? list : children[b] = []).Add(cls.Name);
            }
            else
            {
                roots.Add(cls.Name);
            }
        }
        var next = 1;
        void Assign(string name)
        {
            if (_typeRange.ContainsKey(name))
                throw new Tcs2cException($"inheritance cycle at {name}");
            var first = next++;
            _typeRange[name] = (first, first);
            foreach (var child in children.TryGetValue(name, out var cs)
                ? cs : [])
                Assign(child);
            _typeRange[name] = (first, next - 1);
        }
        foreach (var root in roots) Assign(root);
        if (_typeRange.Count != _program.Classes.Length)
            throw new Tcs2cException("inheritance cycle detected");
    }

    private IEnumerable<IlClassInfo> ChainRootFirst(string cls)
    {
        var chain = new List<IlClassInfo>();
        for (string? cur = cls; cur != null; cur = _classes[cur].BaseName)
            chain.Add(_classes[cur]);
        chain.Reverse();
        return chain;
    }

    // ancestor は base chain 上の class か、chain のどこかが実装する interface
    internal bool IsAncestorOrSame(string ancestor, string derived)
    {
        for (string? cur = derived; cur != null; cur = _classes[cur].BaseName)
        {
            if (cur == ancestor) return true;
            if (!_classes[cur].Interfaces.IsDefault
                && _classes[cur].Interfaces.Any(i => SimpleTypeName(i) == ancestor))
                return true;
        }
        return false;
    }

    private static string SimpleTypeName(string displayName)
    {
        var text = displayName.StartsWith("global::", StringComparison.Ordinal)
            ? displayName[8..] : displayName;
        var dot = text.LastIndexOf('.');
        return dot < 0 ? text : text[(dot + 1)..];
    }

    private bool IsInterface(string cls) => _classes[cls].IsInterface;

    // name を宣言する最も近い chain 上の class (自身含む)
    private string? FindDeclaringClass(string cls, string method)
    {
        for (string? cur = cls; cur != null; cur = _classes[cur].BaseName)
            if (_classes[cur].Methods.Any(m => m.Name == method)) return cur;
        return null;
    }

    private FieldFact FieldInChain(string cls, string name)
    {
        for (string? cur = cls; cur != null; cur = _classes[cur].BaseName)
            if (_classes[cur].Fields.Any(f => f.Name == name))
                return _facts.Field(cur, name);
        throw new Tcs2cException($"unknown field: {cls}.{name}");
    }

    // declaring で宣言された method を strict 子孫が再宣言しているか。
    // interface の method は常に実装側へ dispatch する
    private bool IsPolymorphic(string declaring, string method) =>
        IsInterface(declaring)
        || _program.Classes.Any(c => c.Name != declaring
            && IsAncestorOrSame(declaring, c.Name)
            && c.Methods.Any(m => m.Name == method));

    private static IlExportResult Normalize(IlExportResult program)
    {
        var classes = program.Classes.ToList();
        if (program.TopLevel is { } topLevel)
        {
            classes.Add(new IlClassInfo("TopLevel", null, [], "0",
                [new IlMethodInfo(LuaNaming.Member("Main"), true, [], topLevel,
                    "void", [])]));
        }
        return program with { Classes = [.. classes.Select(RenameOperatorOverloads)] };
    }

    public string Emit(string? requestedEntry, bool lib = false)
    {
        ValidateProgram();
        var entry = lib ? null : FindEntry(requestedEntry);

        _output.Append(RuntimePrelude);
        _output.Append(RuntimeGc);
        _output.Append(RuntimeCollections);
        _output.Append(RuntimeStrings);
        _output.Append(RuntimeLib);
        Line(LiteralsMarker);
        EmitClassDeclarations();
        EmitInterfaceChecks();
        EmitStaticFields();
        EmitStaticRoots();
        EmitMethodPrototypes();
        EmitAllocators();
        foreach (var cls in _program.Classes)
        foreach (var method in cls.Methods)
        {
            try { EmitMethod(cls, method); }
            catch (Tcs2cException ex)
            {
                throw new Tcs2cException($"{ex.Message} (in {cls.Name}.{method.Name})");
            }
        }
        EmitStructMembers();
        EmitDispatchers();
        EmitRecordEquality();
        EmitStaticInitializer();
        if (lib)
            EmitLibEntryPoints();
        else
            EmitEntryPoint(entry);
        return _output.ToString()
            .Replace(ClosureDeclMarker, string.Join("\n", _closureDecls))
            .Replace(LiteralsMarker, string.Join("\n", _literalDecls));
    }

    private void ValidateProgram()
    {
        if (_program.Diagnostics.Length > 0)
            throw new Tcs2cException("cannot emit a program with TinyC# diagnostics");
        ValidateStructs();
        foreach (var cls in _program.Classes)
        {
            Names.Id(cls.Name);
            foreach (var field in cls.Fields)
            {
                Names.Id(field.Name);
                var fact = _facts.Field(cls.Name, field.Name);
                EnsureSupportedStorageType(fact.Type,
                    $"field {cls.Name}.{field.Name}");
            }
            var methodNames = new HashSet<string>();
            foreach (var method in cls.Methods)
            {
                Names.Id(method.Name);
                if (!methodNames.Add(method.Name))
                    throw new Tcs2cException($"method overloads are not supported: " +
                        $"{cls.Name}.{method.Name}");
                if (method.Body is null && !method.IsAbstract && !cls.IsInterface)
                    throw new Tcs2cException($"method has no IL body: {cls.Name}.{method.Name}");
                var fact = _facts.Method(cls.Name, method.Name);
                EnsureSupportedStorageType(fact.ReturnType,
                    $"return type of {cls.Name}.{method.Name}", allowVoid: true);
                foreach (var parameter in fact.Parameters)
                    EnsureSupportedStorageType(parameter.Type,
                        $"parameter {cls.Name}.{method.Name}.{parameter.Name}");
            }
        }
    }

    // 格納型 (field / parameter / local / 要素): スカラ・string・class 参照・
    // データ struct・Dictionary・closure と、それらを要素とする array / List
    private static void EnsureSupportedStorageType(CType type, string where,
        bool allowVoid = false)
    {
        if (type.Kind == CTypeKind.Void && allowVoid) return;
        if (IsStorageType(type)) return;
        throw new Tcs2cException($"unsupported {where}: {type}");
    }

    private static bool IsStorageType(CType type) => type.Kind switch
    {
        CTypeKind.I32 or CTypeKind.F32 or CTypeKind.Bool or CTypeKind.String
            or CTypeKind.Ref or CTypeKind.StructVal or CTypeKind.Closure
            or CTypeKind.Nullable or CTypeKind.Random or CTypeKind.Object => true,
        CTypeKind.Dict => type.Key is not null && type.Element is not null
            && IsStorageType(type.Element),
        CTypeKind.Array or CTypeKind.List => type.Element is not null
            && IsStorageType(type.Element),
        _ => false,
    };

    private (IlClassInfo Class, IlMethodInfo Method)? FindEntry(string? requested)
    {
        var candidates = _program.Classes
            .SelectMany(c => c.Methods.Select(m => (Class: c, Method: m)))
            // IL の名前は Lua 出力の規則 (snake_case) で写像済み
            .Where(x => x.Method.Name == LuaNaming.Member("Main") && x.Method.IsStatic
                && x.Method.Parameters.Length == 0
                && _facts.Method(x.Class.Name, x.Method.Name).ReturnType == CType.Void)
            .Where(x => requested is null || x.Class.Name == requested)
            .ToArray();
        return candidates.Length switch
        {
            1 => candidates[0],
            0 when requested is null && !_digestF32 => null,
            0 => throw new Tcs2cException(requested is null
                ? "--digest-f32 requires a static void Main() entry point"
                : $"no static void Main() entry point in {requested}"),
            _ => throw new Tcs2cException("multiple Main entry points; pass --entry CLASS"),
        };
    }

    private void EmitClassDeclarations()
    {
        // class の前方 typedef を先に出す (struct の field が class 参照を持てる)
        foreach (var cls in _program.Classes)
            Line($"typedef struct {Names.Class(cls.Name)} {Names.Class(cls.Name)};");
        Line();
        EmitStructTypedefs();
        Line("typedef struct TcsObjectHeader {");
        _indent++;
        Line("uint32_t type_id;");
        _indent--;
        Line("} TcsObjectHeader;");
        Line();
        Line("enum {");
        _indent++;
        foreach (var cls in _program.Classes)
        {
            var (first, last) = _typeRange[cls.Name];
            Line($"{Names.TypeId(cls.Name)} = {first},");
            Line($"{Names.TypeIdMax(cls.Name)} = {last},");
        }
        _indent--;
        Line("};");
        Line();
        foreach (var cls in _program.Classes)
        {
            Line($"struct {Names.Class(cls.Name)} {{");
            _indent++;
            Line("uint32_t type_id;");
            // host と共有する外部 data class: host 側の handle (GC は見ない)
            if (ChainRootFirst(cls.Name).Any(c => c.IsExternal))
                Line("uint64_t host_value;");
            // 継承 chain を root から平坦化 (先頭 layout 一致で upcast 可能)
            foreach (var link in ChainRootFirst(cls.Name))
            foreach (var field in link.Fields.Where(f => !f.IsStatic))
            {
                var fact = _facts.Field(link.Name, field.Name);
                Line($"{fact.Type.CName} {Names.Field(field.Name)};");
            }
            _indent--;
            Line("};");
            Line();
        }
        EmitLayouts();
    }

    private void EmitStaticFields()
    {
        foreach (var cls in _program.Classes)
        foreach (var field in cls.Fields.Where(f => f.IsStatic))
        {
            var fact = _facts.Field(cls.Name, field.Name);
            Line($"static {fact.Type.CName} {Names.StaticField(cls.Name, field.Name)};");
        }
        Line();
        // 遅延初期化 class の static は accessor (lvalue) 経由で触る
        foreach (var cls in _program.Classes.Where(HasLazyStatics))
        {
            Line($"static void {Names.StaticInit(cls.Name)}(void);");
            foreach (var field in cls.Fields.Where(f => f.IsStatic))
            {
                var fact = _facts.Field(cls.Name, field.Name);
                Line($"static inline {fact.Type.CName} *" +
                    $"{Names.StaticPlace(cls.Name, field.Name)}(void) " +
                    $"{{ {Names.StaticInit(cls.Name)}(); " +
                    $"return &{Names.StaticField(cls.Name, field.Name)}; }}");
            }
        }
        Line();
    }

    // 定数式 (literal の組み合わせ) か。定数でない static initializer を持つ
    // class は C# と同じく最初のアクセスで初期化する (前方参照 / 循環対応)
    private static bool IsConstantExpr(IlExpr expr) => expr switch
    {
        IlLit => true,
        IlParen paren => IsConstantExpr(paren.E),
        IlUn unary => IsConstantExpr(unary.E),
        IlBin binary => IsConstantExpr(binary.L) && IsConstantExpr(binary.R),
        _ => false,
    };

    private bool HasLazyStatics(IlClassInfo cls) =>
        cls.Fields.Any(f => f.IsStatic && f.Init is { } init && !IsConstantExpr(init));

    // static field の place 式 (遅延 class なら初期化を保証する accessor)
    private string StaticFieldPlace(IlClassInfo cls, string field) =>
        HasLazyStatics(cls)
            ? $"(*{Names.StaticPlace(cls.Name, field)}())"
            : Names.StaticField(cls.Name, field);

    private void EmitMethodPrototypes()
    {
        foreach (var cls in _program.Classes.Where(c => !c.IsInterface))
        {
            var protoParams = CtorParamFacts(cls);
            var signature = protoParams.Count == 0
                ? "void"
                : string.Join(", ", protoParams.Select(p => p.Type.CName));
            Line($"static {Names.Class(cls.Name)} *" +
                $"{Names.New(cls.Name)}({signature});");
            Line($"static void {Names.Init(cls.Name)}(" +
                string.Join(", ", new[] { $"{Names.Class(cls.Name)} *" }
                    .Concat(protoParams.Select(p => p.Type.CName))) + ");");
        }
        foreach (var cls in _program.Classes)
        foreach (var method in cls.Methods)
        {
            var fact = _facts.Method(cls.Name, method.Name);
            Line($"static {fact.ReturnType.CName} {Names.Method(cls.Name, method.Name)}" +
                $"({ParameterList(fact)});");
        }
        Line();
        // dispatcher の前方宣言
        foreach (var cls in _program.Classes)
        foreach (var method in cls.Methods.Where(m => !m.IsStatic))
        {
            var declaring = _classes[cls.Name].BaseName is { } b
                ? FindDeclaringClass(b, method.Name) : null;
            if (declaring != null) continue;
            if (!IsPolymorphic(cls.Name, method.Name)) continue;
            var fact = _facts.Method(cls.Name, method.Name);
            var parameters = string.Join(", ",
                new[] { $"{Names.Class(cls.Name)} *" }
                    .Concat(fact.Parameters.Select(p => p.Type.CName)));
            Line($"static {fact.ReturnType.CName} " +
                $"{Names.Dispatch(cls.Name, method.Name)}({parameters});");
        }
        EmitStructMemberPrototypes();
        EmitRecordEqualityPrototypes();
        EmitForeignPrototypes();
        Line(ClosureDeclMarker);
    }

    private string ParameterList(MethodFact method)
    {
        var parameters = new List<string>();
        if (!method.IsStatic)
        {
            if (_facts.Structs.ContainsKey(method.ClassName))
            {
                // struct method: 格納場所へのポインタと、その所有 heap object
                // (stack 上なら NULL。self 経由の参照 store のバリア先)
                parameters.Add($"{CType.Struct(method.ClassName).CName} *v_self");
                parameters.Add("void *v_owner");
            }
            else
            {
                parameters.Add($"{CType.Ref(method.ClassName).CName} v_self");
            }
        }
        parameters.AddRange(method.Parameters.Select((p, i) =>
            $"{p.Type.CName} v_{Names.Id(p.Name)}_{i}"));
        return parameters.Count == 0 ? "void" : string.Join(", ", parameters);
    }

    private void EmitMethod(IlClassInfo cls, IlMethodInfo method)
    {
        _currentClass = cls;
        var fact = _facts.Method(cls.Name, method.Name);
        EmitMethodCore(method, fact,
            fact.IsStatic ? null : new Variable("v_self", CType.Ref(cls.Name)));
    }

    // class / struct 共通の method 本体。self は class なら参照、struct なら
    // 格納場所へのポインタ (Boxed = `(*v_self)` で place として現れる)
    private void EmitMethodCore(IlMethodInfo method, MethodFact fact, Variable? self)
    {
        _currentMethod = method;
        _currentMethodFact = fact;
        _scopes.Clear();
        _continueTargets.Clear();
        _breakTargets.Clear();
        if (method.Body is not null) CollectCapturedNames(method.Body);
        PushScope();
        if (self is not null) AddVariable("self", self);
        for (var i = 0; i < _currentMethodFact.Parameters.Count; i++)
        {
            var parameter = _currentMethodFact.Parameters[i];
            AddVariable(parameter.Name,
                new Variable($"v_{Names.Id(parameter.Name)}_{i}", parameter.Type));
        }

        Line($"static {_currentMethodFact.ReturnType.CName}");
        Line($"{Names.Method(fact.ClassName, method.Name)}({ParameterList(_currentMethodFact)})");
        Line("{");
        _indent++;
        BoxCapturedParameters();
        // abstract / interface method: 実体は dispatcher が実装へ飛ばすので
        // 本体には到達しない
        if (method.Body is null) Line("tcs_fault(\"abstract-method\");");
        else EmitStats(method.Body.Stats);
        _indent--;
        Line("}");
        Line();
        PopScope();
        FlushPendingClosures();
    }

    private void EmitStats(IEnumerable<IlStat> stats)
    {
        foreach (var stat in stats) EmitStat(stat);
    }

    private void EmitStat(IlStat stat)
    {
        switch (stat)
        {
            case IlLocal local: EmitLocal(local); break;
            case IlAssign assign: EmitAssign(assign); break;
            case IlCallStat { Call: IlIife iife } when TryEmitClearIife(iife): break;
            case IlCallStat call: Line($"{RenderExpr(call.Call)};"); break;
            case IlIf conditional: EmitIf(conditional); break;
            case IlWhile loop: EmitWhile(loop); break;
            case IlRepeat repeat: EmitRepeat(repeat); break;
            case IlNumericFor loop: EmitNumericFor(loop); break;
            case IlForeachList loop: EmitForeachList(loop); break;
            case IlForeachDict loop: EmitForeachDict(loop); break;
            case IlForeachRunes loop: EmitForeachRunes(loop); break;
            case IlBreak:
                Line(_breakTargets.Count > 0 && _breakTargets.Peek() is { } breakLabel
                    ? $"goto {breakLabel};" : "break;");
                break;
            case IlBreakScope scope: EmitBreakScope(scope); break;
            case IlComment: break;
            case IlContinue: EmitContinue(); break;
            case IlMultiAssign multi: EmitMultiAssign(multi); break;
            case IlReturn ret: EmitReturn(ret); break;
            case IlDo block: EmitDo(block); break;
            default:
                throw Unsupported(stat);
        }
    }

    private void EmitLocal(IlLocal local)
    {
        // discard の前宣言 (`out _`): 受け手は呼び出し側が一時変数で用意する
        if (local.Name == "_" && local.Init is null && local.Type is null) return;
        if (local.Init is null)
        {
            // 型は契約 (IlLocal.Type) から。C の zero 初期化 = default 値
            if (local.Type is null)
                throw new Tcs2cException($"local has no initializer/type: " +
                    $"{_currentMethodFact.ClassName}.{_currentMethod.Name}.{local.Name}");
            var declared = _facts.MapType(local.Type);
            var zero = ZeroInit(declared);
            if (_capturedNames.Contains(local.Name))
            {
                var cell0 = new Variable(
                    $"c_{Names.Id(local.Name)}_{_serial++}", declared)
                    { Boxed = true };
                AddVariable(local.Name, cell0);
                Line($"{declared.CName} *{cell0.CName} = " +
                    $"tcs_new_cell(sizeof(*{cell0.CName}), {LayoutRef(declared)});");
                Line($"*{cell0.CName} = {zero};");
                return;
            }
            var declaredVar = new Variable(
                $"v_{Names.Id(local.Name)}_{_serial++}", declared);
            AddVariable(local.Name, declaredVar);
            Line($"{declared.CName} {declaredVar.CName} = {zero};");
            return;
        }
        // 宣言型が closure なら契約型を使う (IlClosure / method group は
        // 単独で型付けできない)
        CType? declaredClosure = null;
        if (local.Type != null)
        {
            var mapped = _facts.TryMapType(local.Type);
            if (mapped is { Kind: CTypeKind.Closure }) declaredClosure = mapped;
        }
        var type = declaredClosure ?? TypeOf(local.Init);
        // 宣言型 (契約) が初期化子の型を受けられるならそちらを local の型に
        // する (`float f = 2` / `string s = null` / `Shape s = new Rect()`)
        if (local.Type != null && _facts.TryMapType(local.Type) is { } declaredType
            && declaredType != type
            && (declaredType.CanAssignFrom(type)
                || declaredType.Kind == CTypeKind.Ref && type.Kind == CTypeKind.Ref
                    && IsAncestorOrSame(declaredType.Name!, type.Name!)))
            type = declaredType;
        if (type.Kind is CTypeKind.Void or CTypeKind.Null)
            throw new Tcs2cException($"cannot infer storage type of local {local.Name}: {type}");
        var rendered = RenderCoerced(local.Init, type);
        if (_capturedNames.Contains(local.Name))
        {
            var cell = new Variable($"c_{Names.Id(local.Name)}_{_serial++}",
                type) { Boxed = true };
            AddVariable(local.Name, cell);
            Line($"{type.CName} *{cell.CName} = " +
                $"tcs_new_cell(sizeof(*{cell.CName}), {LayoutRef(type)});");
            Line($"*{cell.CName} = {rendered};");
            return;
        }
        var variable = new Variable($"v_{Names.Id(local.Name)}_{_serial++}", type);
        AddVariable(local.Name, variable);
        Line($"{type.CName} {variable.CName} = {rendered};");
    }

    private void EmitAssign(IlAssign assign)
    {
        var targetType = TypeOfPlace(assign.Target);
        CheckAssignable(targetType, assign.Value, "assignment");
        var value = RenderCoerced(assign.Value, targetType);

        switch (assign.Target)
        {
            case IlVar variable:
            {
                var target = Resolve(variable.Name);
                Line(target.Boxed
                    ? $"(*{target.CName}) = {value};"
                    : $"{target.CName} = {value};");
                // 捕捉 cell は heap object: 参照の store は owner を dirty に
                if (target.Boxed && NeedsBarrier(targetType))
                    Line($"tcs_wb({target.CName});");
                return;
            }
            case IlField field when TryStaticField(field, out var staticName, out _):
                Line($"{staticName} = {value};");
                return;
            // struct place への field 書き込み (配列要素・ローカル・class field
            // 経由)。C の lvalue 連鎖でそのまま書ける
            case IlField field
                when TypeOf(field.Recv).Kind == CTypeKind.StructVal:
                Line($"{RenderStructPlace(field.Recv)}." +
                    $"{Names.Field(field.Name)} = {value};");
                if (NeedsBarrier(targetType) && RenderStructOwner(field.Recv) is { } owner)
                    Line($"tcs_wb({owner});");
                return;
            case IlField field:
            {
                var receiverType = TypeOf(field.Recv);
                if (receiverType.Kind != CTypeKind.Ref)
                    throw new Tcs2cException("field receiver is not a class reference");
                var temp = Temp("object");
                Line($"{receiverType.CName} {temp} = ({receiverType.CName})" +
                    $"tcs_nonnull({RenderExpr(field.Recv)});");
                Line($"{temp}->{Names.Field(field.Name)} = {value};");
                if (NeedsBarrier(targetType)) Line($"tcs_wb({temp});");
                return;
            }
            case IlIndex index when !index.PlusOne
                && TypeOf(index.Recv).Kind == CTypeKind.Dict:
            {
                var slotType = RequireDict(index.Recv, out var dictType);
                CheckAssignable(slotType, assign.Value, "dict store");
                var dictTemp = Temp("dict");
                Line($"TcsDict *{dictTemp} = {RenderExpr(index.Recv)};");
                Line($"*({slotType.CName} *)tcs_dict_put({dictTemp}, " +
                    $"{DictKeyArgs(dictType.Key!, index.Idx)}) = {value};");
                return;
            }
            case IlIndex index:
            {
                var sequenceType = RequireSequence(index);
                var sequence = Temp("sequence");
                var idx = Temp("index");
                var place = Temp("place");
                Line($"{sequenceType.CName} {sequence} = {RenderExpr(index.Recv)};");
                Line($"int32_t {idx} = {RenderExpr(index.Idx)};");
                var at = sequenceType.Kind == CTypeKind.Array
                    ? "tcs_array_at" : "tcs_list_at";
                Line($"{sequenceType.ElementCName} *{place} = " +
                    $"({sequenceType.ElementCName} *){at}({sequence}, {idx});");
                Line($"*{place} = {value};");
                if (NeedsBarrier(targetType)) Line($"tcs_wb({sequence});");
                return;
            }
            default:
                throw new Tcs2cException($"unsupported assignment place: " +
                    assign.Target.GetType().Name);
        }
    }

    private void EmitReturn(IlReturn ret)
    {
        if (_iifes.Count > 0)
        {
            var (result, label, type) = _iifes.Peek();
            if (ret.Value is null || type == CType.Void)
            {
                if (ret.Value is not null) Line($"{RenderExpr(ret.Value)};");
                Line($"goto {label};");
                return;
            }
            CheckAssignable(type, ret.Value, "iife return");
            Line($"{{ {result} = {RenderCoerced(ret.Value, type)}; goto {label}; }}");
            return;
        }
        if (ret.Value is null)
        {
            if (_ctorReturnValue is { } ctorValue)
            {
                Line($"return {ctorValue};");
                return;
            }
            RequireType(CType.Void, _currentMethodFact.ReturnType, "return");
            Line("return;");
            return;
        }
        CheckAssignable(_currentMethodFact.ReturnType, ret.Value, "return");
        Line($"return {RenderCoerced(ret.Value, _currentMethodFact.ReturnType)};");
    }

    private void EmitDo(IlDo block)
    {
        Line("{");
        _indent++;
        PushScope();
        EmitStats(block.Body.Stats);
        PopScope();
        _indent--;
        Line("}");
    }

    private void EmitStaticInitializer()
    {
        // 定数でない initializer を持つ class: 最初のアクセスで 1 回だけ。
        // flag を先に立てるので循環参照は C# と同じく初期化中の値 (default)
        // を読む
        foreach (var cls in _program.Classes.Where(HasLazyStatics))
        {
            Line("static void");
            Line($"{Names.StaticInit(cls.Name)}(void)");
            Line("{");
            _indent++;
            Line("static bool initialized;");
            Line("if (initialized) return;");
            Line("initialized = true;");
            EmitStaticFieldInits(cls);
            _indent--;
            Line("}");
            Line();
            FlushPendingClosures();
        }
        Line("static void");
        Line("tcs_init_statics(void)");
        Line("{");
        _indent++;
        foreach (var cls in _program.Classes)
        {
            if (HasLazyStatics(cls)) Line($"{Names.StaticInit(cls.Name)}();");
            else EmitStaticFieldInits(cls);
        }
        _indent--;
        Line("}");
        Line();
        FlushPendingClosures();
    }

    private void EmitStaticFieldInits(IlClassInfo cls)
    {
        _currentClass = cls;
        _scopes.Clear();
        PushScope();
        foreach (var field in cls.Fields.Where(f => f.IsStatic))
        {
            var fact = _facts.Field(cls.Name, field.Name);
            if (fact.Init is null) continue;
            CheckAssignable(fact.Type, fact.Init,
                $"initializer of {cls.Name}.{field.Name}");
            Line($"{Names.StaticField(cls.Name, field.Name)} = " +
                $"{RenderCoerced(fact.Init, fact.Type)};");
        }
        PopScope();
    }

    private void Line(string text = "") =>
        _output.Append(' ', _indent * 4).AppendLine(text);

}
