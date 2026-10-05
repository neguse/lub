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
            // ctor の closure (base 引数 / field initializer / 本文) が捕捉する
            // this と引数は method と同じく cell へ box する
            _capturedNames.Clear();
            if (ctor?.BaseArgs.IsDefault == false)
                AddCapturedNames(new IlBlock([.. ctor.BaseArgs.Select(a => new IlCallStat(a))]));
            AddCapturedNames(new IlBlock([.. cls.Fields
                .Where(f => !f.IsStatic && f.Init is not null).Select(f => new IlCallStat(f.Init!))]));
            if (ctor?.Body is { } ctorBody) AddCapturedNames(ctorBody);
            AddVariable("self", new Variable("object", CType.Ref(cls.Name)));
            BoxCapturedParameters();
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


    private MethodFact ResolveInvokeFact(CType receiver, string method)
    {
        if (receiver.Kind != CTypeKind.Ref)
            throw new Tcs2cException("IlInvoke receiver is not a class reference");
        var declaring = FindDeclaringClass(receiver.Name!, method)
            ?? throw new Tcs2cException(
                $"unknown method: {receiver.Name}.{method}");
        return _facts.Method(declaring, method);
    }

    private CType TypeOfInvoke(IlInvoke invoke)
    {
        var receiver = TypeOf(invoke.Recv);
        if (receiver.Kind == CTypeKind.Random)
            return TypeOfRandomMethod(invoke.Method, invoke.Args);
        if (ForeignInstanceMethod(receiver, invoke.Method) is { } foreign)
            return TypeOfForeignCall(foreign, invoke.Args);
        var fact = ResolveInvokeFact(receiver, invoke.Method);
        return ValidateMethodCall(fact, receiver, invoke.Args);
    }

    private string RenderInvoke(IlInvoke invoke)
    {
        _ = TypeOfInvoke(invoke);
        var receiver = TypeOf(invoke.Recv);
        if (receiver.Kind == CTypeKind.Random)
            return RenderRandomMethod($"tcs_nonnull({RenderExpr(invoke.Recv)})",
                invoke.Method, invoke.Args);
        if (ForeignInstanceMethod(receiver, invoke.Method) is { } foreign)
            return RenderForeignCall(foreign, invoke.Args, invoke.Recv);
        var fact = ResolveInvokeFact(receiver, invoke.Method);
        // 子孫に再宣言があれば実行時型で dispatch (il-spec §9)
        // dispatcher は chain 最上位の宣言 class が持つ (receiver が中間 class
        // 型でも同じ dispatcher を通す)
        if (IsPolymorphic(fact.ClassName, fact.Name))
        {
            var root = fact.ClassName;
            for (var cur = _classes[root].BaseName; cur != null; cur = _classes[cur].BaseName)
                if (_classes[cur].Methods.Any(m => m.Name == fact.Name)) root = cur;
            var rootFact = _facts.Method(root, fact.Name);
            return RenderMethodCall(rootFact, invoke.Recv, invoke.Args,
                Names.Dispatch(root, fact.Name));
        }
        return RenderMethodCall(fact, invoke.Recv, invoke.Args);
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
                // 同名でもシグネチャが違う宣言は override ではない (C# の別
                // method)。一致する最寄りの宣言へ飛ばす
                string? impl = null;
                for (string? cur = target.Name; cur != null && impl == null; cur = _classes[cur].BaseName)
                    if (_classes[cur].Methods.Any(m => m.Name == method.Name && !m.IsStatic)
                        && _facts.Method(cur, method.Name).Parameters.Select(p => p.Type)
                            .SequenceEqual(fact.Parameters.Select(p => p.Type)))
                        impl = cur;
                if (impl == null) continue;
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
