using System.Collections.Immutable;
using System.Text;
using TinyCs;

namespace TinyCs.Tcs2c;

internal sealed partial class CEmitter
{
    // Lua backend の Class.new と同順で構築する:
    // base init → type_id (setmetatable 相当) → 自 class field init → ctor body。
    // 確保は最派生の tcs_new_C が layout (sizeof(Tcs_C) + pointer map) で 1 回
    // 行い、tcs_init_C(object, ...) が base の init を prefix 互換の upcast で
    // 連鎖呼びする (base 側で sizeof(Base) を確保すると派生 field が溢れる)
    private void EmitAllocators()
    {
        foreach (var cls in _program.Classes.Where(c => !c.IsInterface))
        {
            var cType = Names.Class(cls.Name);
            var ctor = cls.Ctor;
            _currentClass = cls;
            _scopes.Clear();
            _continueTargets.Clear();
            _breakTargets.Clear();
            PushScope();
            var paramFacts = CtorParamFacts(cls);
            for (var i = 0; i < paramFacts.Count; i++)
                AddVariable(paramFacts[i].Name,
                    new Variable($"v_{Names.Id(paramFacts[i].Name)}_{i}",
                        paramFacts[i].Type));
            var parameters = string.Join(", ", new[] { $"{cType} *object" }
                .Concat(paramFacts.Select((p, i) =>
                    $"{p.Type.CName} v_{Names.Id(p.Name)}_{i}")));
            Line("static void");
            Line($"{Names.Init(cls.Name)}({parameters})");
            Line("{");
            _indent++;
            if (cls.BaseName is { } baseName)
            {
                var baseParams = CtorParamFacts(_classes[baseName]);
                var baseArgs = CompleteArguments(baseParams,
                    ctor?.BaseArgs.IsDefault == false ? ctor.BaseArgs : [],
                    $"base constructor of {cls.Name}");
                var rendered = new List<string> { $"({Names.Class(baseName)} *)object" };
                for (var i = 0; i < baseArgs.Count; i++)
                {
                    CheckAssignable(baseParams[i].Type, baseArgs[i],
                        $"base ctor argument {i} of {cls.Name}");
                    var temp = Temp("base_arg");
                    Line($"{baseParams[i].Type.CName} {temp} = " +
                        $"{RenderCoerced(baseArgs[i], baseParams[i].Type)};");
                    rendered.Add(temp);
                }
                Line($"{Names.Init(baseName)}({string.Join(", ", rendered)});");
            }
            Line($"object->type_id = {Names.TypeId(cls.Name)};");
            Line($"TCS_GC_HEADER(object)->type_id = {Names.TypeId(cls.Name)};");
            AddVariable("self", new Variable("object", CType.Ref(cls.Name)));
            foreach (var field in cls.Fields.Where(f => !f.IsStatic))
            {
                var fact = _facts.Field(cls.Name, field.Name);
                if (fact.Init is not null)
                {
                    CheckAssignable(fact.Type, fact.Init,
                        $"initializer of {cls.Name}.{field.Name}");
                    Line($"object->{Names.Field(field.Name)} = " +
                        $"{RenderCoerced(fact.Init, fact.Type)};");
                }
            }
            if (ctor?.Body is { } body)
                EmitStats(body.Stats);
            else if (ctor is { Body: null })
                throw new Tcs2cException(
                    $"constructor body is not IL-exportable: {cls.Name}");
            PopScope();
            _indent--;
            Line("}");
            Line();
            FlushPendingClosures();

            var newParams = paramFacts.Count == 0
                ? "void"
                : string.Join(", ", paramFacts.Select((p, i) =>
                    $"{p.Type.CName} v_{Names.Id(p.Name)}_{i}"));
            var initArgs = string.Join(", ", new[] { "object" }
                .Concat(paramFacts.Select((p, i) => $"v_{Names.Id(p.Name)}_{i}")));
            Line($"static {cType} *");
            Line($"{Names.New(cls.Name)}({newParams})");
            Line("{");
            _indent++;
            Line($"{cType} *object = tcs_new_object(&{Names.ClassLayout(cls.Name)});");
            Line($"{Names.Init(cls.Name)}({initArgs});");
            Line("return object;");
            _indent--;
            Line("}");
            Line();
        }
    }

    private List<ParameterFact> CtorParamFacts(IlClassInfo cls)
    {
        if (cls.Ctor is not { } ctor) return [];
        if (ctor.Parameters.Length != ctor.ParameterTypes.Length)
            throw new Tcs2cException($"ctor metadata mismatch: {cls.Name}");
        return [.. _facts.ParameterFacts(ctor.Parameters, ctor.ParameterTypes,
            ctor.ParameterDefaults)];
    }

    // 末尾の省略引数を既定値 (IL の ParameterDefaults) で補う
    private static IReadOnlyList<IlExpr> CompleteArguments(
        IReadOnlyList<ParameterFact> parameters, IReadOnlyList<IlExpr> supplied,
        string where)
    {
        if (supplied.Count == parameters.Count) return supplied;
        if (supplied.Count > parameters.Count)
            throw new Tcs2cException($"{where}: expected {parameters.Count} arguments, " +
                $"got {supplied.Count}");
        var result = supplied.ToList();
        for (var i = supplied.Count; i < parameters.Count; i++)
            result.Add(parameters[i].Default
                ?? throw new Tcs2cException($"{where}: missing argument {parameters[i].Name}"));
        return result;
    }


    // 実行時型 dispatch: 「chain 最上位で宣言され、strict 子孫が
    // 再宣言している」method ごとに type_id → 最寄り実装の switch を生成
    private void EmitDispatchers()
    {
        foreach (var cls in _program.Classes)
        foreach (var method in cls.Methods.Where(m => !m.IsStatic))
        {
            var declaring = _classes[cls.Name].BaseName is { } b
                ? FindDeclaringClass(b, method.Name) : null;
            if (declaring != null) continue; // 再宣言側は root が担当
            if (!IsPolymorphic(cls.Name, method.Name)) continue;
            var fact = _facts.Method(cls.Name, method.Name);
            var parameters = string.Join(", ",
                new[] { $"{Names.Class(cls.Name)} *v_self" }
                    .Concat(fact.Parameters.Select((p, i) =>
                        $"{p.Type.CName} v_{Names.Id(p.Name)}_{i}")));
            Line($"static {fact.ReturnType.CName}");
            Line($"{Names.Dispatch(cls.Name, method.Name)}({parameters})");
            Line("{");
            _indent++;
            Line("switch (((TcsObjectHeader *)v_self)->type_id) {");
            foreach (var target in _program.Classes
                .Where(c => !c.IsInterface && IsAncestorOrSame(cls.Name, c.Name)))
            {
                var impl = FindDeclaringClass(target.Name, method.Name)!;
                var call = $"{Names.Method(impl, method.Name)}(" +
                    string.Join(", ",
                        new[] { $"({Names.Class(impl)} *)v_self" }
                            .Concat(fact.Parameters.Select((p, i) =>
                                $"v_{Names.Id(p.Name)}_{i}"))) + ")";
                Line($"case {Names.TypeId(target.Name)}: " +
                    (fact.ReturnType == CType.Void
                        ? $"{call}; return;" : $"return {call};"));
            }
            Line("default: tcs_fault(\"dispatch\");");
            Line("}");
            _indent--;
            Line("}");
            Line();
        }
    }
}
