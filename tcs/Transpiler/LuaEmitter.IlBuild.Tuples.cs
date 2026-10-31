using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// IL builder: タプルと分解。タプルの値は struct (TinySystem/ValueTuple.cs)。
// Lua はタプルを返す method を multi-return にし、値として保持する位置でだけ table にする。
// C の経路 (StructuralTypeTests) はタプルを単相化した struct の値で返すので multi-return にしない。
public partial class LuaEmitter
{
    private int _deconstructTemp;

    // multi-return の method。戻り値の宣言がタプルの method だけで、呼び出し側は宣言の型で判る。
    // 型引数 T の値は 1 つの値で渡るので、`I<T>.Get` の実装の `(int, int) Get()` は対象外。
    private bool IsMultiReturnMethod(IMethodSymbol method)
    {
        var definition = (method.ReducedFrom ?? method).OriginalDefinition;
        if (StructuralTypeTests
            || definition.MethodKind != MethodKind.Ordinary
            || !definition.ReturnType.IsTupleType
            || definition.Parameters.Any(p => p.RefKind == RefKind.Out)
            || !definition.DeclaringSyntaxReferences.Any(r => !ReferenceTrees.Contains(r.SyntaxTree)))
            return false;
        var owner = definition.ContainingType;
        return owner.AllInterfaces.All(iface => iface.GetMembers(definition.Name)
            .Where(member => SymbolEqualityComparer.Default.Equals(
                owner.FindImplementationForInterfaceMember(member), definition))
            .All(member => member is IMethodSymbol implemented
                && implemented.OriginalDefinition.ReturnType.IsTupleType));
    }

    private IMethodSymbol? MultiReturnMethod(SemanticModel model, InvocationExpressionSyntax invocation) =>
        model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method && IsMultiReturnMethod(method)
            ? method : null;

    private bool IsInMultiReturnMethod(SemanticModel model, ExpressionSyntax value) =>
        model.GetEnclosingSymbol(value.SpanStart) is IMethodSymbol method && IsMultiReturnMethod(method);

    private static int TupleArity(ITypeSymbol type) =>
        ((INamedTypeSymbol)type).TupleElements.Length;

    private static IlExpr PackTuple(IMethodSymbol method, IlExpr call) =>
        new IlCall($"__tcs_tuple{TupleArity(method.ReturnType)}", [call]);

    private static ExpressionSyntax WithoutParens(ExpressionSyntax expr)
    {
        while (expr is ParenthesizedExpressionSyntax paren) expr = paren.Expression;
        return expr;
    }

    private IlExpr? BuildTupleLiteral(SemanticModel model, TupleExpressionSyntax tuple)
    {
        var entries = ImmutableArray.CreateBuilder<IlTableEntry>();
        var index = 1;
        foreach (var argument in tuple.Arguments)
        {
            if (BuildExpr(model, argument.Expression) is not { } value) return null;
            entries.Add(new IlTableEntry(null, WrapStructCopy(model, argument.Expression, value),
                $"Item{index++}"));
        }
        return new IlTable(entries.ToImmutable());
    }

    // `return (a, b)` / `return F()` は値のまま返し、それ以外は local から要素を並べる
    private bool BuildTupleReturnInto(SemanticModel model, ExpressionSyntax value, SyntaxNode? origin,
        List<IlStat> acc)
    {
        var core = WithoutParens(value);
        if (core is TupleExpressionSyntax literal)
        {
            var items = ImmutableArray.CreateBuilder<IlExpr>();
            foreach (var argument in literal.Arguments)
            {
                if (BuildExpr(model, argument.Expression) is not { } item) return false;
                items.Add(WrapStructCopy(model, argument.Expression, item));
            }
            acc.Add(new IlReturn(new IlValues(items.ToImmutable())) { Origin = origin });
            return true;
        }
        if (core is InvocationExpressionSyntax call && MultiReturnMethod(model, call) != null)
        {
            if (BuildInvocationCall(model, call) is not { } forwarded) return false;
            acc.Add(new IlReturn(forwarded) { Origin = origin });
            return true;
        }
        if (BuildExpr(model, value) is not { } built) return false;
        var tuple = (INamedTypeSymbol)model.GetTypeInfo(value).Type!;
        var fresh = IsFreshStructIl(built) || IsFreshStructValue(value);
        var fields = tuple.TupleElements.Select((element, i) =>
        {
            IlExpr field = new IlField(new IlVar("__tcs_ret"), $"Item{i + 1}");
            return fresh ? field : CopyIfMutableStruct(element.Type, field);
        }).ToImmutableArray();
        acc.Add(new IlDo(new IlBlock([
                new IlLocal("__tcs_ret", built),
                new IlReturn(new IlValues(fields))]))
            { Origin = origin });
        return true;
    }

    // ---- 分解。C# の順: 左辺の受け手と添字 → 右辺 → 左から順に代入 ----

    private abstract record DeconstructTarget;
    private sealed record DeclaredTarget(string Name, ITypeSymbol Type) : DeconstructTarget;
    private sealed record DiscardTarget : DeconstructTarget;
    private sealed record PlaceTarget(ExpressionSyntax Place, ITypeSymbol Type) : DeconstructTarget;
    private sealed record NestedTarget(ImmutableArray<DeconstructTarget> Items) : DeconstructTarget;

    private static DeconstructTarget? DeconstructTargetOf(SemanticModel model, ExpressionSyntax target)
    {
        switch (target)
        {
            case DeclarationExpressionSyntax declaration:
                return DesignationTarget(model, declaration.Designation);
            case TupleExpressionSyntax tuple:
            {
                var items = tuple.Arguments.Select(a => DeconstructTargetOf(model, a.Expression)).ToList();
                return items.Any(i => i == null) ? null : new NestedTarget([.. items.Select(i => i!)]);
            }
            case IdentifierNameSyntax discard when model.GetSymbolInfo(discard).Symbol is IDiscardSymbol:
                return new DiscardTarget();
            case IdentifierNameSyntax or MemberAccessExpressionSyntax or ElementAccessExpressionSyntax:
                return model.GetTypeInfo(target).Type is { } type ? new PlaceTarget(target, type) : null;
            default:
                return null;
        }
    }

    private static DeconstructTarget? DesignationTarget(SemanticModel model, VariableDesignationSyntax designation)
    {
        switch (designation)
        {
            case DiscardDesignationSyntax:
                return new DiscardTarget();
            case SingleVariableDesignationSyntax single:
                return model.GetDeclaredSymbol(single) is ILocalSymbol local
                    ? new DeclaredTarget(single.Identifier.ValueText, local.Type) : null;
            case ParenthesizedVariableDesignationSyntax nested:
            {
                var items = nested.Variables.Select(v => DesignationTarget(model, v)).ToList();
                return items.Any(i => i == null) ? null : new NestedTarget([.. items.Select(i => i!)]);
            }
            default:
                return null;
        }
    }

    // 分解できる型の成分: タプル、C の単相化したタプル (Item の field と Deconstruct を持つ struct)、record
    private static List<(string Name, ITypeSymbol Type)>? DeconstructMembers(ITypeSymbol? type, int count)
    {
        if (type is not INamedTypeSymbol named) return null;
        if (named.IsTupleType)
            return [.. named.TupleElements.Select((e, i) => ($"Item{i + 1}", e.Type))];
        if (named.GetMembers("Deconstruct").Any()
            && Enumerable.Range(1, count).Select(i => named.GetMembers($"Item{i}").OfType<IFieldSymbol>().FirstOrDefault())
                .ToList() is var fields && fields.All(f => f != null))
            return [.. fields.Select(f => (f!.Name, f.Type))];
        var props = named.GetMembers().OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic && p.DeclaredAccessibility == Accessibility.Public)
            .ToList();
        return props.Count >= count
            ? [.. props.Take(count).Select(p => (p.Name, p.Type))] : null;
    }

    private bool BuildDeconstructInto(SemanticModel model, ExpressionSyntax left, ExpressionSyntax right,
        SyntaxNode? origin, List<IlStat> acc)
    {
        if (DeconstructTargetOf(model, left) is not NestedTarget target) return false;
        var rhs = WithoutParens(right);
        if (TryBuildFlatDeconstruct(model, target, rhs, origin, acc) is { } flat) return flat;
        var state = new DeconstructState(model);
        foreach (var name in DeclaredNames(target))
            acc.Add(new IlLocal(name.Name, null, name.Type.ToDisplayString()) { Origin = origin });
        if (!FixPlaces(state, target)) return false;
        if (!BindValues(state, target, rhs)) return false;
        if (!AddAssigns(state)) return false;
        acc.Add(new IlDo(new IlBlock([.. state.Stats])) { Origin = origin });
        return true;
    }

    private sealed class DeconstructState(SemanticModel model)
    {
        public SemanticModel Model => model;
        public List<IlStat> Stats { get; } = [];
        public Dictionary<PlaceTarget, IlExpr> Places { get; } = new(ReferenceEqualityComparer.Instance);
        public List<(DeconstructTarget Target, IlExpr Value)> Assigns { get; } = [];
    }

    private static IEnumerable<DeclaredTarget> DeclaredNames(DeconstructTarget target) => target switch
    {
        DeclaredTarget declared => [declared],
        NestedTarget nested => nested.Items.SelectMany(DeclaredNames),
        _ => [],
    };

    private string NewDeconstructTemp() => $"__tcs_d{++_deconstructTemp}";

    // 宣言だけの分解 (`var (a, b) = ...`) と local への代入は、右辺の値を一度に束縛できる
    private bool? TryBuildFlatDeconstruct(SemanticModel model, NestedTarget target, ExpressionSyntax rhs,
        SyntaxNode? origin, List<IlStat> acc)
    {
        var declare = target.Items.All(i => i is DeclaredTarget or DiscardTarget);
        var locals = target.Items.All(i => i is PlaceTarget
            {
                Place: IdentifierNameSyntax id,
            } && model.GetSymbolInfo(id).Symbol is ILocalSymbol or IParameterSymbol);
        if (!declare && !locals) return null;
        var names = target.Items.Select(i => i switch
        {
            DeclaredTarget d => d.Name,
            PlaceTarget p => ((IdentifierNameSyntax)p.Place).Identifier.ValueText,
            _ => "_",
        }).ToList();
        if (!declare && names.Distinct().Count() != names.Count) return null;
        ImmutableArray<IlExpr> targets = [.. names.Select(IlExpr (n) => new IlVar(n))];
        var targetTypes = target.Items.Select(TargetType).ToList();
        // 右辺の要素の型
        switch (rhs)
        {
            case InvocationExpressionSyntax call when MultiReturnMethod(model, call) is { } method:
            {
                var elements = ((INamedTypeSymbol)method.ReturnType).TupleElements;
                if (elements.Length != targetTypes.Count
                    || !elements.Zip(targetTypes, (e, t) => t == null
                        || SymbolEqualityComparer.Default.Equals(e.Type, t)).All(same => same))
                    return null;
                if (BuildInvocationCall(model, call) is not { } built) return false;
                acc.Add(new IlMultiAssign(targets, [built], declare) { Origin = origin });
                return true;
            }
            case TupleExpressionSyntax literal when literal.Arguments.Count == targets.Length
                && !literal.Arguments.Any(a => WithoutParens(a.Expression) is TupleExpressionSyntax):
            {
                var values = ImmutableArray.CreateBuilder<IlExpr>();
                for (var i = 0; i < targets.Length; i++)
                {
                    var element = literal.Arguments[i].Expression;
                    if (BuildExpr(model, element) is not { } built) return false;
                    // BuildExpr は ConvertedType までの変換を済ませている
                    var info = model.GetTypeInfo(element);
                    values.Add(ConvertDeconstructed(WrapStructCopy(model, element, built),
                        info.ConvertedType ?? info.Type, targetTypes[i]));
                }
                acc.Add(new IlMultiAssign(targets, values.ToImmutable(), declare) { Origin = origin });
                return true;
            }
            default:
                return null;
        }
    }

    private static ITypeSymbol? TargetType(DeconstructTarget target) => target switch
    {
        DeclaredTarget declared => declared.Type,
        PlaceTarget place => place.Type,
        _ => null,
    };

    // 要素を代入する place の受け手と添字を、右辺の評価の前に固定する
    private bool FixPlaces(DeconstructState state, DeconstructTarget target)
    {
        switch (target)
        {
            case NestedTarget nested:
                return nested.Items.All(item => FixPlaces(state, item));
            case PlaceTarget place:
            {
                var model = state.Model;
                if (IsCustomPropertyTarget(model, place.Place)) return false;
                if (place.Place is IdentifierNameSyntax
                    || place.Place is MemberAccessExpressionSyntax
                        {
                            Expression: var receiver,
                        } && model.GetTypeInfo(receiver).Type is { IsValueType: true }
                        && StructPlaceBase(model, receiver) is var baseExpr
                        && (baseExpr == null || baseExpr == receiver))
                {
                    if (BuildExpr(model, place.Place) is not { } direct) return false;
                    state.Places[place] = direct;
                    return true;
                }
                if (BuildLoweredTarget(model, place.Place, force: true) is not { } lowered) return false;
                state.Places[place] = RenameFixedTemps(lowered.Setup, lowered.Access, state.Stats);
                return true;
            }
            default:
                return true;
        }
    }

    // BuildLoweredTarget の temp は固定の名前なので、place ごとに別の名前にする
    private IlExpr RenameFixedTemps(List<IlStat> setup, IlExpr access, List<IlStat> into)
    {
        var renames = new Dictionary<string, string>();
        IlExpr Rename(IlExpr expr) => expr is IlVar v && renames.TryGetValue(v.Name, out var name)
            ? new IlVar(name)
            : WithChildren(expr, [.. Children(expr).Select(Rename)]);
        foreach (var stat in setup)
        {
            var local = (IlLocal)stat;
            var init = Rename(local.Init!);
            renames[local.Name] = NewDeconstructTemp();
            into.Add(local with { Name = renames[local.Name], Init = init });
        }
        return Rename(access);
    }

    private bool BindValues(DeconstructState state, DeconstructTarget target, ExpressionSyntax rhs)
    {
        var model = state.Model;
        rhs = WithoutParens(rhs);
        if (target is NestedTarget nested)
        {
            if (rhs is TupleExpressionSyntax literal)
            {
                if (literal.Arguments.Count != nested.Items.Length) return false;
                for (var i = 0; i < nested.Items.Length; i++)
                    if (!BindValues(state, nested.Items[i], literal.Arguments[i].Expression)) return false;
                return true;
            }
            var type = model.GetTypeInfo(rhs).Type;
            if (rhs is InvocationExpressionSyntax call && MultiReturnMethod(model, call) is { } method)
            {
                if (BuildInvocationCall(model, call) is not { } multi) return false;
                var temps = nested.Items.Select(_ => NewDeconstructTemp()).ToList();
                state.Stats.Add(new IlMultiAssign([.. temps.Select(IlExpr (t) => new IlVar(t))], [multi], true));
                var elements = ((INamedTypeSymbol)method.ReturnType).TupleElements;
                for (var i = 0; i < temps.Count; i++)
                    if (!BindValue(state, nested.Items[i], new IlVar(temps[i]), elements[i].Type)) return false;
                return true;
            }
            if (BuildExpr(model, rhs) is not { } built) return false;
            return BindValue(state, nested, built, type);
        }
        if (BuildExpr(model, rhs) is not { } value) return false;
        value = WrapStructCopy(model, rhs, value);
        var sourceType = model.GetTypeInfo(rhs);
        var temp = NewDeconstructTemp();
        state.Stats.Add(new IlLocal(temp, value,
            (TargetType(target) ?? sourceType.ConvertedType ?? sourceType.Type)?.ToDisplayString()));
        // BuildExpr は ConvertedType までの変換を済ませている
        state.Assigns.Add((target, ConvertDeconstructed(new IlVar(temp),
            sourceType.ConvertedType ?? sourceType.Type, TargetType(target))));
        return true;
    }

    // 値を temp に束縛済みとして、target へ渡す。入れ子は成分へたどる
    private bool BindValue(DeconstructState state, DeconstructTarget target, IlExpr value, ITypeSymbol? type)
    {
        if (target is not NestedTarget nested)
        {
            state.Assigns.Add((target, ConvertDeconstructed(CopyIfMutableStruct(type, value), type,
                TargetType(target))));
            return true;
        }
        if (DeconstructMembers(type, nested.Items.Length) is not { } members
            || members.Count != nested.Items.Length) return false;
        var holder = value;
        if (value is not IlVar)
        {
            var temp = NewDeconstructTemp();
            state.Stats.Add(new IlLocal(temp, value, type?.ToDisplayString()));
            holder = new IlVar(temp);
        }
        for (var i = 0; i < members.Count; i++)
            if (!BindValue(state, nested.Items[i], new IlField(holder, members[i].Name), members[i].Type))
                return false;
        return true;
    }

    // 分解の成分の暗黙変換 (`float f; (f, g) = (1, 2)`)
    private static IlExpr ConvertDeconstructed(IlExpr value, ITypeSymbol? source, ITypeSymbol? target)
    {
        if (target == null || source == null
            || SymbolEqualityComparer.Default.Equals(source, target)) return value;
        var inner = IsNullableValueType(target) ? ((INamedTypeSymbol)target).TypeArguments[0] : target;
        if (inner.SpecialType == SpecialType.System_Single && IsIntegralSource(source))
            value = new IlNumericConvert(value, "float");
        return IsNullableValueType(target) && !IsNullableValueType(source)
            ? new IlNullableWrap(value, inner.ToDisplayString()) : value;
    }

    private bool AddAssigns(DeconstructState state)
    {
        foreach (var (target, value) in state.Assigns)
        {
            switch (target)
            {
                case DeclaredTarget declared:
                    state.Stats.Add(new IlAssign(new IlVar(declared.Name), value));
                    break;
                case PlaceTarget place:
                    state.Stats.Add(new IlAssign(state.Places[place], value));
                    break;
            }
        }
        return true;
    }

    // `foreach (var (a, b) in list)`: 要素を 1 つの変数で受けて、本体の先頭で分解する
    private bool BuildForeachDeconstructInto(SemanticModel model, ForEachVariableStatementSyntax loop,
        List<IlStat> acc)
    {
        var collectionType = model.GetTypeInfo(loop.Expression).Type;
        var elementType = collectionType switch
        {
            IArrayTypeSymbol array => array.ElementType,
            INamedTypeSymbol { TypeArguments: [var element] } list
                when IsListType(list.OriginalDefinition.ToDisplayString()) => element,
            _ => null,
        };
        if (elementType == null
            || DeconstructTargetOf(model, loop.Variable) is not NestedTarget target
            || BuildExpr(model, loop.Expression) is not { } collection
            || BuildBlock(model, loop.Statement) is not { } body)
            return false;
        var state = new DeconstructState(model);
        List<IlStat> prefix = [.. DeclaredNames(target)
            .Select(IlStat (d) => new IlLocal(d.Name, null, d.Type.ToDisplayString()))];
        var item = NewDeconstructTemp();
        if (!BindValue(state, target, new IlVar(item), elementType) || !AddAssigns(state)) return false;
        acc.Add(new IlForeachList(item, collection,
            new IlBlock([.. prefix, .. state.Stats, .. body.Stats])) { Origin = loop });
        return true;
    }
}
