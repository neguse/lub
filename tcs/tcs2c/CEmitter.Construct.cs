using System.Collections.Immutable;
using System.Text;
using TinyCs;

namespace TinyCs.Tcs2c;

internal sealed partial class CEmitter
{
    // Lua backend の Class.new と同順で構築する:
    // base ctor → type_id (setmetatable 相当) → 自 class field init → ctor body
    private void EmitAllocators()
    {
        foreach (var cls in _program.Classes)
        {
            var cType = Names.Class(cls.Name);
            var ctor = cls.Ctor;
            _currentClass = cls;
            _scopes.Clear();
            _continueTargets.Clear();
            PushScope();
            var paramFacts = CtorParamFacts(cls);
            for (var i = 0; i < paramFacts.Count; i++)
                AddVariable(paramFacts[i].Name,
                    new Variable($"v_{Names.Id(paramFacts[i].Name)}_{i}",
                        paramFacts[i].Type));
            var parameters = paramFacts.Count == 0
                ? "void"
                : string.Join(", ", paramFacts.Select((p, i) =>
                    $"{p.Type.CName} v_{Names.Id(p.Name)}_{i}"));
            Line($"static {cType} *");
            Line($"{Names.New(cls.Name)}({parameters})");
            Line("{");
            _indent++;
            if (cls.BaseName is { } baseName)
            {
                var baseParams = CtorParamFacts(_classes[baseName]);
                var baseArgs = ctor?.BaseArgs.IsDefault == false
                    ? ctor.BaseArgs : [];
                if (baseArgs.Length != baseParams.Count)
                    throw new Tcs2cException(
                        $"base constructor arity mismatch: {cls.Name}");
                var rendered = new List<string>();
                for (var i = 0; i < baseArgs.Length; i++)
                {
                    RequireAssignable(baseParams[i].Type, TypeOf(baseArgs[i]),
                        $"base ctor argument {i} of {cls.Name}");
                    var temp = Temp("base_arg");
                    Line($"{baseParams[i].Type.CName} {temp} = " +
                        $"{RenderCoerced(baseArgs[i], baseParams[i].Type)};");
                    rendered.Add(temp);
                }
                Line($"{cType} *object = ({cType} *)" +
                    $"{Names.New(baseName)}({string.Join(", ", rendered)});");
            }
            else
            {
                Line($"{cType} *object = tcs_alloc(sizeof(*object));");
            }
            Line($"object->type_id = {Names.TypeId(cls.Name)};");
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
            if (ctor?.Body is { } body)
                EmitStats(body.Stats);
            else if (ctor is { Body: null })
                throw new Tcs2cException(
                    $"constructor body is not IL-exportable: {cls.Name}");
            Line("return object;");
            PopScope();
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
        return ctor.Parameters.Select((name, i) => new ParameterFact(
            name, _facts.MapType(ctor.ParameterTypes[i]))).ToList();
    }


    // 実行時型 dispatch (T218-m4): 「chain 最上位で宣言され、strict 子孫が
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
                .Where(c => IsAncestorOrSame(cls.Name, c.Name)))
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
