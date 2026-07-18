using System.Globalization;
using System.Text;
using TinyCs;

namespace TinyCs.Luoc;

internal sealed partial class CEmitter
{
    private readonly IlExportResult _program;
    private readonly ContractFacts _facts;
    private readonly Dictionary<string, IlClassInfo> _classes;
    private readonly bool _digestF32;
    private readonly StringBuilder _output = new();
    private readonly Stack<Dictionary<string, Variable>> _scopes = new();
    private readonly Stack<string?> _continueTargets = new();
    private IlClassInfo _currentClass = null!;
    private IlMethodInfo _currentMethod = null!;
    private MethodFact _currentMethodFact = null!;
    private int _indent;
    private int _serial;

    private sealed class Variable(string cName, CType type)
    {
        public string CName { get; } = cName;
        public CType Type { get; set; } = type;
    }

    public CEmitter(IlExportResult program, bool digestF32)
    {
        // T218-m3: ctor と top-level 文を合成 method として注入し、
        // facts / prototype / EmitMethod の既存機構をそのまま通す
        _program = Normalize(program);
        _digestF32 = digestF32;
        _facts = new ContractFacts(_program);
        _classes = new Dictionary<string, IlClassInfo>(_facts.Classes);
    }

    internal const string CtorMethodName = "__ctor";

    private static IlExportResult Normalize(IlExportResult program)
    {
        var classes = program.Classes.Select(cls => cls.Ctor is { } ctor
            ? cls with
            {
                Methods = [.. cls.Methods,
                    new IlMethodInfo(CtorMethodName, false, ctor.Parameters,
                        ctor.Body, "void", ctor.ParameterTypes)],
            }
            : cls).ToList();
        if (program.TopLevel is { } topLevel)
        {
            classes.Add(new IlClassInfo("TopLevel", null, [], "0",
                [new IlMethodInfo("Main", true, [], topLevel, "void", [])]));
        }
        return program with { Classes = [.. classes] };
    }

    public string Emit(string? requestedEntry)
    {
        ValidateProgram();
        var entry = FindEntry(requestedEntry);

        _output.Append(RuntimePrelude);
        EmitClassDeclarations();
        EmitStaticFields();
        EmitMethodPrototypes();
        EmitAllocators();
        foreach (var cls in _program.Classes)
        foreach (var method in cls.Methods)
            EmitMethod(cls, method);
        EmitStaticInitializer();
        EmitEntryPoint(entry);
        return _output.ToString();
    }

    private void ValidateProgram()
    {
        if (_program.Diagnostics.Length > 0)
            throw new LuocException("cannot emit a program with TinyC# diagnostics");
        foreach (var cls in _program.Classes)
        {
            Names.Id(cls.Name);
            if (cls.BaseName is not null)
                throw new LuocException($"inheritance is not supported yet: {cls.Name}");
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
                    throw new LuocException($"method overloads are not supported: " +
                        $"{cls.Name}.{method.Name}");
                if (method.Body is null)
                    throw new LuocException($"method has no IL body: {cls.Name}.{method.Name}");
                var fact = _facts.Method(cls.Name, method.Name);
                EnsureSupportedStorageType(fact.ReturnType,
                    $"return type of {cls.Name}.{method.Name}", allowVoid: true);
                foreach (var parameter in fact.Parameters)
                    EnsureSupportedStorageType(parameter.Type,
                        $"parameter {cls.Name}.{method.Name}.{parameter.Name}");
            }
        }
    }

    private static void EnsureSupportedStorageType(CType type, string where,
        bool allowVoid = false)
    {
        if (type.Kind == CTypeKind.Void && allowVoid) return;
        if (type.Kind is CTypeKind.I32 or CTypeKind.F32 or CTypeKind.Bool
            or CTypeKind.String or CTypeKind.Ref) return;
        if (type.Kind == CTypeKind.Array
            && type.Element!.Kind is CTypeKind.I32 or CTypeKind.F32 or CTypeKind.Bool
                or CTypeKind.Ref) return;
        if (type.Kind == CTypeKind.List
            && type.Element!.Kind is CTypeKind.I32 or CTypeKind.F32) return;
        throw new LuocException($"unsupported {where}: {type}");
    }

    private (IlClassInfo Class, IlMethodInfo Method)? FindEntry(string? requested)
    {
        var candidates = _program.Classes
            .SelectMany(c => c.Methods.Select(m => (Class: c, Method: m)))
            .Where(x => x.Method.Name == "Main" && x.Method.IsStatic
                && x.Method.Parameters.Length == 0
                && _facts.Method(x.Class.Name, x.Method.Name).ReturnType == CType.Void)
            .Where(x => requested is null || x.Class.Name == requested)
            .ToArray();
        return candidates.Length switch
        {
            1 => candidates[0],
            0 when requested is null && !_digestF32 => null,
            0 => throw new LuocException(requested is null
                ? "--digest-f32 requires a static void Main() entry point"
                : $"no static void Main() entry point in {requested}"),
            _ => throw new LuocException("multiple Main entry points; pass --entry CLASS"),
        };
    }

    private void EmitClassDeclarations()
    {
        foreach (var cls in _program.Classes)
            Line($"typedef struct {Names.Class(cls.Name)} {Names.Class(cls.Name)};");
        Line();
        Line("typedef struct TcsObjectHeader {");
        _indent++;
        Line("uint32_t type_id;");
        _indent--;
        Line("} TcsObjectHeader;");
        Line();
        Line("enum {");
        _indent++;
        for (var i = 0; i < _program.Classes.Length; i++)
            Line($"{Names.TypeId(_program.Classes[i].Name)} = {i + 1},");
        _indent--;
        Line("};");
        Line();
        foreach (var cls in _program.Classes)
        {
            Line($"struct {Names.Class(cls.Name)} {{");
            _indent++;
            Line("uint32_t type_id;");
            foreach (var field in cls.Fields.Where(f => !f.IsStatic))
            {
                var fact = _facts.Field(cls.Name, field.Name);
                Line($"{fact.Type.CName} {Names.Field(field.Name)};");
            }
            _indent--;
            Line("};");
            Line();
        }
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
    }

    private void EmitMethodPrototypes()
    {
        foreach (var cls in _program.Classes)
            Line($"static {Names.Class(cls.Name)} *{Names.New(cls.Name)}(void);");
        foreach (var cls in _program.Classes)
        foreach (var method in cls.Methods)
        {
            var fact = _facts.Method(cls.Name, method.Name);
            Line($"static {fact.ReturnType.CName} {Names.Method(cls.Name, method.Name)}" +
                $"({ParameterList(fact)});");
        }
        Line();
    }

    private static string ParameterList(MethodFact method)
    {
        var parameters = new List<string>();
        if (!method.IsStatic)
            parameters.Add($"{CType.Ref(method.ClassName).CName} v_self");
        parameters.AddRange(method.Parameters.Select((p, i) =>
            $"{p.Type.CName} v_{Names.Id(p.Name)}_{i}"));
        return parameters.Count == 0 ? "void" : string.Join(", ", parameters);
    }

    private void EmitAllocators()
    {
        foreach (var cls in _program.Classes)
        {
            var cType = Names.Class(cls.Name);
            Line($"static {cType} *");
            Line($"{Names.New(cls.Name)}(void)");
            Line("{");
            _indent++;
            Line($"{cType} *object = tcs_alloc(sizeof(*object));");
            Line($"object->type_id = {Names.TypeId(cls.Name)};");
            _currentClass = cls;
            _scopes.Clear();
            PushScope();
            AddVariable("self", new Variable("object", CType.Ref(cls.Name)));
            foreach (var field in cls.Fields.Where(f => !f.IsStatic))
            {
                var fact = _facts.Field(cls.Name, field.Name);
                if (fact.Init is not null)
                {
                    RequireAssignable(fact.Type, TypeOf(fact.Init),
                        $"initializer of {cls.Name}.{field.Name}");
                    Line($"object->{Names.Field(field.Name)} = " +
                        $"{RenderExpr(fact.Init)};");
                }
            }
            PopScope();
            Line("return object;");
            _indent--;
            Line("}");
            Line();
        }
    }

    private void EmitMethod(IlClassInfo cls, IlMethodInfo method)
    {
        _currentClass = cls;
        _currentMethod = method;
        _currentMethodFact = _facts.Method(cls.Name, method.Name);
        _scopes.Clear();
        _continueTargets.Clear();
        PushScope();
        if (!_currentMethodFact.IsStatic)
            AddVariable("self", new Variable("v_self", CType.Ref(cls.Name)));
        for (var i = 0; i < _currentMethodFact.Parameters.Count; i++)
        {
            var parameter = _currentMethodFact.Parameters[i];
            AddVariable(parameter.Name,
                new Variable($"v_{Names.Id(parameter.Name)}_{i}", parameter.Type));
        }

        Line($"static {_currentMethodFact.ReturnType.CName}");
        Line($"{Names.Method(cls.Name, method.Name)}({ParameterList(_currentMethodFact)})");
        Line("{");
        _indent++;
        EmitStats(method.Body!.Stats);
        _indent--;
        Line("}");
        Line();
        PopScope();
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
            case IlCallStat call: Line($"{RenderExpr(call.Call)};"); break;
            case IlIf conditional: EmitIf(conditional); break;
            case IlWhile loop: EmitWhile(loop); break;
            case IlRepeat repeat: EmitRepeat(repeat); break;
            case IlNumericFor loop: EmitNumericFor(loop); break;
            case IlForeachList loop: EmitForeachList(loop); break;
            case IlBreak: Line("break;"); break;
            case IlContinue: EmitContinue(); break;
            case IlReturn ret: EmitReturn(ret); break;
            case IlDo block: EmitDo(block); break;
            default:
                throw Unsupported(stat);
        }
    }

    private void EmitLocal(IlLocal local)
    {
        if (local.Init is null)
            throw new LuocException($"local has no initializer and IlExport does not expose " +
                $"its type: {_currentClass.Name}.{_currentMethod.Name}.{local.Name}");
        var type = TypeOf(local.Init);
        if (type.Kind is CTypeKind.Void or CTypeKind.Null)
            throw new LuocException($"cannot infer storage type of local {local.Name}: {type}");
        var variable = new Variable($"v_{Names.Id(local.Name)}_{_serial++}", type);
        AddVariable(local.Name, variable);
        Line($"{type.CName} {variable.CName} = {RenderExpr(local.Init)};");
    }

    private void EmitAssign(IlAssign assign)
    {
        var targetType = TypeOfPlace(assign.Target);
        var valueType = TypeOf(assign.Value);
        RequireAssignable(targetType, valueType, "assignment");
        var value = RenderExpr(assign.Value);

        switch (assign.Target)
        {
            case IlVar variable:
                Line($"{Resolve(variable.Name).CName} = {value};");
                return;
            case IlField field when TryStaticField(field, out var staticName, out _):
                Line($"{staticName} = {value};");
                return;
            case IlField field:
            {
                var receiverType = TypeOf(field.Recv);
                if (receiverType.Kind != CTypeKind.Ref)
                    throw new LuocException("field receiver is not a class reference");
                var temp = Temp("object");
                Line($"{receiverType.CName} {temp} = ({receiverType.CName})" +
                    $"tcs_nonnull({RenderExpr(field.Recv)});");
                Line($"{temp}->{Names.Field(field.Name)} = {value};");
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
                return;
            }
            default:
                throw new LuocException($"unsupported assignment place: " +
                    assign.Target.GetType().Name);
        }
    }

    private void EmitIf(IlIf conditional)
    {
        for (var i = 0; i < conditional.Arms.Length; i++)
        {
            var (condition, body) = conditional.Arms[i];
            RequireType(CType.Bool, TypeOf(condition), "if condition");
            Line($"{(i == 0 ? "if" : "else if")} ({RenderExpr(condition)}) {{");
            _indent++;
            PushScope();
            EmitStats(body.Stats);
            PopScope();
            _indent--;
            Line("}");
        }
        if (conditional.Else is not null)
        {
            Line("else {");
            _indent++;
            PushScope();
            EmitStats(conditional.Else.Stats);
            PopScope();
            _indent--;
            Line("}");
        }
    }

    private void EmitNumericFor(IlNumericFor loop)
    {
        RequireType(CType.I32, TypeOf(loop.Start), "numeric for start");
        RequireType(CType.I32, TypeOf(loop.Limit), "numeric for limit");
        var start = Temp("for_start");
        var limit = Temp("for_limit");
        var variable = new Variable($"v_{Names.Id(loop.Var)}_{_serial++}", CType.I32);
        Line("{");
        _indent++;
        Line($"int32_t {start} = {RenderExpr(loop.Start)};");
        Line($"int32_t {limit} = {RenderExpr(loop.Limit)};");
        Line($"for (int32_t {variable.CName} = {start}; {variable.CName} <= {limit}; " +
            $"{variable.CName} = {variable.CName} + INT32_C(1)) {{");
        _indent++;
        PushScope();
        AddVariable(loop.Var, variable);
        _continueTargets.Push(null);
        EmitStats(loop.Body.Stats);
        _continueTargets.Pop();
        PopScope();
        _indent--;
        Line("}");
        _indent--;
        Line("}");
    }

    private void EmitForeachList(IlForeachList loop)
    {
        var sequenceType = TypeOf(loop.Coll);
        if (sequenceType.Kind is not (CTypeKind.Array or CTypeKind.List)
            || sequenceType.Element is null)
            throw new LuocException($"IlForeachList requires a typed array/List, got " +
                sequenceType);
        var sequence = Temp("foreach_sequence");
        var length = Temp("foreach_length");
        var index = Temp("foreach_index");
        var variable = new Variable($"v_{Names.Id(loop.Var)}_{_serial++}",
            sequenceType.Element);
        var lengthFunction = sequenceType.Kind == CTypeKind.Array
            ? "tcs_array_length" : "tcs_list_length";
        var atFunction = sequenceType.Kind == CTypeKind.Array
            ? "tcs_array_at" : "tcs_list_at";

        Line("{");
        _indent++;
        Line($"{sequenceType.CName} {sequence} = {RenderExpr(loop.Coll)};");
        Line($"int32_t {length} = {lengthFunction}({sequence});");
        Line($"for (int32_t {index} = 0; {index} < {length}; {index}++) {{");
        _indent++;
        PushScope();
        AddVariable(loop.Var, variable);
        _continueTargets.Push(null);
        Line($"{sequenceType.ElementCName} {variable.CName} = " +
            $"*({sequenceType.ElementCName} *){atFunction}({sequence}, {index});");
        EmitStats(loop.Body.Stats);
        _continueTargets.Pop();
        PopScope();
        _indent--;
        Line("}");
        _indent--;
        Line("}");
    }

    private void EmitWhile(IlWhile loop)
    {
        RequireType(CType.Bool, TypeOf(loop.Cond), "while condition");
        var label = loop.Trailer is null ? null : Temp("continue");
        Line($"while ({RenderExpr(loop.Cond)}) {{");
        _indent++;
        _continueTargets.Push(label);
        PushScope();
        if (label is not null) Line("{");
        if (label is not null) _indent++;
        EmitStats(loop.Body.Stats);
        if (label is not null) _indent--;
        if (label is not null) Line("}");
        PopScope();
        if (label is not null)
        {
            Line($"{label}:");
            PushScope();
            EmitStats(loop.Trailer!.Stats);
            PopScope();
            Line(";");
        }
        _continueTargets.Pop();
        _indent--;
        Line("}");
    }

    private void EmitRepeat(IlRepeat repeat)
    {
        RequireType(CType.Bool, TypeOf(repeat.Cond), "repeat condition");
        Line("do {");
        _indent++;
        _continueTargets.Push(null);
        PushScope();
        EmitStats(repeat.Body.Stats);
        PopScope();
        _continueTargets.Pop();
        _indent--;
        Line($"}} while ({RenderExpr(repeat.Cond)});");
    }

    private void EmitContinue()
    {
        if (_continueTargets.Count == 0)
            throw new LuocException("continue outside a loop");
        var target = _continueTargets.Peek();
        Line(target is null ? "continue;" : $"goto {target};");
    }

    private void EmitReturn(IlReturn ret)
    {
        if (ret.Value is null)
        {
            RequireType(CType.Void, _currentMethodFact.ReturnType, "return");
            Line("return;");
            return;
        }
        RequireAssignable(_currentMethodFact.ReturnType, TypeOf(ret.Value), "return");
        Line($"return {RenderExpr(ret.Value)};");
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
        Line("static void");
        Line("tcs_init_statics(void)");
        Line("{");
        _indent++;
        foreach (var cls in _program.Classes)
        {
            _currentClass = cls;
            _scopes.Clear();
            PushScope();
            foreach (var field in cls.Fields.Where(f => f.IsStatic))
            {
                var fact = _facts.Field(cls.Name, field.Name);
                if (fact.Init is null) continue;
                RequireAssignable(fact.Type, TypeOf(fact.Init),
                    $"initializer of {cls.Name}.{field.Name}");
                Line($"{Names.StaticField(cls.Name, field.Name)} = " +
                    $"{RenderExpr(fact.Init)};");
            }
            PopScope();
        }
        _indent--;
        Line("}");
        Line();
    }

    private void EmitEntryPoint((IlClassInfo Class, IlMethodInfo Method)? entry)
    {
        Line("int");
        Line("main(void)");
        Line("{");
        _indent++;
        Line("tcs_init_statics();");
        if (_digestF32) Line("tcs_digest = UINT32_C(2166136261);");
        if (entry is { } selected)
            Line($"{Names.Method(selected.Class.Name, selected.Method.Name)}();");
        if (_digestF32)
            Line("printf(\"%08\" PRIx32 \"\\n\", tcs_digest);");
        Line("return 0;");
        _indent--;
        Line("}");
    }

    private void Line(string text = "") =>
        _output.Append(' ', _indent * 4).AppendLine(text);

}
