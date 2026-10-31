using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// struct / record struct の emit。instance は metatable 無しの
// plain table で、member は静的自由関数 (`S.M(self, ...)`) として同名 table に
// 載る — struct は継承がなく呼び出しサイトの静的型が確定するため動的
// ディスパッチ不要で、呼び出し側 (IlBuild) が直接呼ぶ。
public partial class LuaEmitter
{
    private void VisitStruct(SemanticModel model, StructDeclarationSyntax structDecl)
    {
        SetSource(structDecl);
        var symbol = model.GetDeclaredSymbol(structDecl);
        var name = TypeName(symbol!);
        var info = BeginStructType(name, symbol);
        var statics = EmitStructStaticPreZero(model, name, info, structDecl.Members);
        EmitStructNew(name, symbol);
        EmitStructCopyFunction(name, symbol);

        EmitExplicitStructCtors(model, name, structDecl);
        EmitStructEquality(name, symbol);

        EmitStructMembers(model, name, structDecl.Members);
        DeferStaticFieldInitializers(model, name, info, statics);
        _currentType = null;
    }

    // struct の値を構成する member の名前と宣言型が instance shape。`new` が
    // 生成する field 集合そのもので、変更は既存の値と食い違う = restart 境界。
    // 型は Lua の default 値では区別できない (string / int[] はどちらも nil)
    private EmittedTypeInfo BeginStructType(string name, INamedTypeSymbol? symbol)
    {
        var info = BeginType(name, "struct", symbol);
        info.InstanceShape = string.Join("\n", ValueMembers(symbol).Select(m =>
            m.Name + ":" + m.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        AppendLine();
        return info;
    }

    // static field と static auto property は class と同じく型の table に pre-zero し、
    // initializer は全型の定義の後に実行する。const は使用箇所で値に畳むので持たない
    private List<(string Name, ExpressionSyntax? Init, ITypeSymbol? Type)> EmitStructStaticPreZero(
        SemanticModel model, string name, EmittedTypeInfo info,
        IEnumerable<MemberDeclarationSyntax> members)
    {
        var statics = new List<(string Name, ExpressionSyntax? Init, ITypeSymbol? Type)>();
        foreach (var member in members)
        {
            if (!member.Modifiers.Any(SyntaxKind.StaticKeyword)) continue;
            switch (member)
            {
                case FieldDeclarationSyntax field:
                    var fieldType = model.GetTypeInfo(field.Declaration.Type).Type;
                    foreach (var v in field.Declaration.Variables)
                        statics.Add((v.Identifier.ValueText, v.Initializer?.Value, fieldType));
                    break;
                case PropertyDeclarationSyntax prop when IsAutoProperty(prop):
                    statics.Add((prop.Identifier.ValueText, prop.Initializer?.Value,
                        model.GetTypeInfo(prop.Type).Type));
                    break;
            }
        }
        EmitStaticPreZero(name, info, statics);
        return statics;
    }

    // record struct。struct の emit の上に positional primary ctor
    // と値等価 (op_Equality) を合成する。== の呼び出しサイトは IlBuild が
    // 静的型から直接 op_Equality へ振り分ける
    private void VisitRecordStruct(SemanticModel model,
        RecordDeclarationSyntax rec)
    {
        SetSource(rec);
        var symbol = model.GetDeclaredSymbol(rec);
        var name = TypeName(symbol!);
        var info = BeginStructType(name, symbol);
        var statics = EmitStructStaticPreZero(model, name, info, rec.Members);
        EmitStructNew(name, symbol);

        // positional primary ctor: zero → positional 代入 → initializer の順
        // (initializer は primary ctor param を参照できる — param 名で解決)
        if (rec.ParameterList is { Parameters.Count: > 0 } parameterList)
        {
            var paramNames = parameterList.Parameters
                .Select(p => p.Identifier.ValueText).ToList();
            var hidden = TypeArgDefaultParams(symbol);
            _currentType?.DefinitionKeys.Add("ctor");
            AppendLine($"function {name}.ctor({string.Join(", ", paramNames.Concat(hidden))})");
            _indent++;
            AppendLine($"local self = {name}.new({string.Join(", ", hidden)})");
            foreach (var p in paramNames)
                AppendLine($"self.{p} = {p}");
            EmitMemberInitializers(model, rec.Members);
            AppendLine("return self");
            _indent--;
            AppendLine("end");
            AppendLine();
        }

        EmitExplicitStructCtors(model, name, rec);
        EmitStructCopyFunction(name, symbol);
        EmitStructEquality(name, symbol);

        EmitStructMembers(model, name, rec.Members);
        DeferStaticFieldInitializers(model, name, info, statics);
        _currentType = null;
    }

    // 値等価 (memberwise)。record struct の ==/!= と、struct 要素の
    // List.Contains / IndexOf / Remove (C# の EqualityComparer<T>.Default)
    // が使う。ネスト struct 値は推移的に field 展開する (struct は循環
    // できないので停止する)
    private void EmitStructEquality(string name, INamedTypeSymbol? symbol)
    {
        _currentType?.DefinitionKeys.Add("op_Equality");
        AppendLine($"function {name}.op_Equality(a, b)");
        _indent++;
        var parts = symbol == null
            ? []
            : ValueEqualityParts(symbol, "a", "b").ToList();
        AppendLine(parts.Count == 0
            ? "return true"
            : $"return {string.Join(" and ", parts)}");
        _indent--;
        AppendLine("end");
        AppendLine();
    }

    // zero 初期化コンストラクタ。`new S()` / default(S) / field default が通る
    private void EmitStructNew(string name, INamedTypeSymbol? symbol)
    {
        _currentType?.DefinitionKeys.Add("new");
        var hidden = TypeArgDefaultParams(symbol);
        AppendLine($"function {name}.new({string.Join(", ", hidden)})");
        _indent++;
        var zero = hidden.Select(p => $"{p} = {p}").Concat(ValueMembers(symbol).Select(m =>
            $"{m.Name} = {GetDefaultValueForType(m.Type)}"));
        AppendLine($"return {{{string.Join(", ", zero)}}}");
        _indent--;
        AppendLine("end");
        AppendLine();
    }

    // member の emit。override (ToString 等) は診断済み (Shared facts) なので emit しない
    private void EmitStructMembers(SemanticModel model, string name,
        IEnumerable<MemberDeclarationSyntax> members)
    {
        EmitOperators(model, name, [.. members.OfType<OperatorDeclarationSyntax>()]);
        foreach (var member in members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax method
                    when !method.Modifiers.Any(SyntaxKind.OverrideKeyword):
                    VisitMethod(model, name, method, explicitSelf: true);
                    break;
                case PropertyDeclarationSyntax prop
                    when !IsAutoProperty(prop) && prop.AccessorList != null:
                    VisitCustomProperty(model, name, prop, explicitSelf: true);
                    break;
                case PropertyDeclarationSyntax prop
                    when prop.ExpressionBody != null:
                    VisitExpressionBodiedProperty(model, name, prop,
                        explicitSelf: true);
                    break;
            }
        }
    }

    // field / property initializer は明示 ctor 実行時のみ走る (C# 11 意味論)
    private void EmitMemberInitializers(SemanticModel model,
        IEnumerable<MemberDeclarationSyntax> members)
    {
        foreach (var member in members)
        {
            if (member.Modifiers.Any(SyntaxKind.StaticKeyword)
                || member.Modifiers.Any(SyntaxKind.ConstKeyword))
                continue;
            switch (member)
            {
                case FieldDeclarationSyntax field:
                    foreach (var v in field.Declaration.Variables)
                        if (v.Initializer != null)
                            AppendLine($"self.{v.Identifier.ValueText} = " +
                                $"{RenderExprViaIl(model, v.Initializer.Value, copyStruct: true)}");
                    break;
                case PropertyDeclarationSyntax { Initializer: not null } prop:
                    AppendLine($"self.{prop.Identifier.ValueText} = " +
                        $"{RenderExprViaIl(model, prop.Initializer.Value, copyStruct: true)}");
                    break;
            }
        }
    }

    // 明示 ctor は zero 初期化 (S.new) の上で field initializer → 本文の順で
    // 実行する (C# の struct ctor 意味論)。`new S()` は ctor を通らない zero
    // 値なので、呼び出しは builder が S.ctor へ振り分ける
    private void EmitExplicitStructCtors(SemanticModel model, string name,
        TypeDeclarationSyntax structDecl)
    {
        foreach (var ctor in structDecl.Members.OfType<ConstructorDeclarationSyntax>()
            .Where(c => !c.Modifiers.Any(SyntaxKind.StaticKeyword)
                && c.ParameterList.Parameters.Count > 0))
            EmitStructCtor(model, name, structDecl, ctor);
    }

    private void EmitStructCtor(SemanticModel model, string name,
        TypeDeclarationSyntax structDecl, ConstructorDeclarationSyntax ctor)
    {
        SetSource(ctor);
        var ctorParams = ctor.ParameterList.Parameters
            .Select(p => p.Identifier.ValueText).ToList();
        var hidden = TypeArgDefaultParams(model.GetDeclaredSymbol(structDecl));
        var ctorName = LuaNaming.CtorName(model.GetDeclaredSymbol(ctor)!);
        _currentType?.DefinitionKeys.Add(ctorName);
        AppendLine($"function {name}.{ctorName}({string.Join(", ", ctorParams.Concat(hidden))})");
        _indent++;
        BeginTraceFunction(model);
        EmitParameterDefaults(model, ctor.ParameterList);
        AppendLine($"local self = {name}.new({string.Join(", ", hidden)})");
        EmitMemberInitializers(model, structDecl.Members);
        if (ctor.Body != null && !TryEmitStatsViaIl(model, ctor.Body.Statements))
            ReportUnsupportedBody(model, ctor.Body.Statements);
        AppendLine("return self");
        EndTraceFunction();
        _indent--;
        AppendLine("end");
        AppendLine();
    }

    // 型別 copy 関数。struct-in-struct は再帰 copy (shallow だと copy 経由の
    // 部分書き込みが alias する)。readonly struct の member は不変なので共有
    private void EmitStructCopyFunction(string name, INamedTypeSymbol? symbol)
    {
        _currentType?.DefinitionKeys.Add("__copy");
        // generic struct は型引数の値を写す関数を受ける (StructCopiers)
        var copierParams = symbol is { TypeKind: TypeKind.Struct } && PassesTypeArgDefaults(symbol)
            ? string.Concat(symbol.TypeParameters.Select(p => ", " + TypeArgCopierName(p))) : "";
        AppendLine($"function {name}.__copy(s{copierParams})");
        _indent++;
        var copied = TypeArgDefaultParams(symbol).Select(p => $"{p} = s.{p}").Concat(
            ValueMembers(symbol).Select(m => m.Type is ITypeParameterSymbol parameter
                ? $"{m.Name} = {TypeArgCopierName(parameter)} and {TypeArgCopierName(parameter)}(s.{m.Name}) or s.{m.Name}"
                : IsMutableStruct(m.Type)
                    ? $"{m.Name} = {TypeName(m.Type)}.__copy(s.{m.Name}{StructCopiers(m.Type, inCopy: true)})"
                    : $"{m.Name} = s.{m.Name}"));
        AppendLine($"return {{{string.Join(", ", copied)}}}");
        _indent--;
        AppendLine("end");
        AppendLine();
        // 型引数に入れたときの記述子 (IlStructDescriptor。可変な struct だけ)。hot reload で
        // 差し替わる関数を引くので closure
        if (copierParams.Length == 0 && IsMutableStruct(symbol))
        {
            _currentType?.DefinitionKeys.Add("__tcs_td");
            AppendLine($"{name}.__tcs_td = {{__tcs_td = true, n = function() return {name}.new() end, " +
                $"c = function(v) return {name}.__copy(v) end}}");
            AppendLine();
        }
    }

    // 値を構成する member (positional prop の backing field 込み、
    // EqualityContract のような合成 property は field を持たないので除外される)
    private static IEnumerable<(string Name, ITypeSymbol Type)> ValueMembers(
        ITypeSymbol? type)
    {
        if (type == null) yield break;
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst) continue;
            // auto-property の backing field は property の名前で返す
            yield return field.AssociatedSymbol is IPropertySymbol prop
                ? (prop.Name, field.Type)
                : (field.Name, field.Type);
        }
    }

    private static IEnumerable<string> ValueEqualityParts(ITypeSymbol type,
        string a, string b) =>
        ValueMembers(type).SelectMany(m =>
            MemberEqualityParts(m.Type, $"{a}.{m.Name}", $"{b}.{m.Name}"));

    // 値 1 つの等価式 (EqualityComparer<T>.Default)。struct 値は field へ展開し、
    // struct の T? は値の有無が同じで中の値が等しいとき等しい。float / double
    // (とその T?) は float.Equals で NaN 同士も等しい (+0 と -0 は == で等しい)。
    // 型 parameter (とその T?) も float で実体化されうるので同じ比較にする
    // (NaN でない値では == と同じ)。それ以外は Lua の == (record class は __eq) に任せる
    private static IEnumerable<string> MemberEqualityParts(ITypeSymbol type,
        string a, string b)
    {
        if (IsUserStruct(type)) return ValueEqualityParts(type, a, b);
        var nullable = IsNullableValueType(type);
        var inner = nullable ? ((INamedTypeSymbol)type).TypeArguments[0] : type;
        if (nullable && IsUserStruct(inner))
        {
            var parts = ValueEqualityParts(inner, a, b).Prepend($"{b} ~= nil");
            return [$"({a} == nil and {b} == nil or {a} ~= nil and {string.Join(" and ", parts)})"];
        }
        if (inner.SpecialType is SpecialType.System_Single or SpecialType.System_Double
            || inner is ITypeParameterSymbol)
            return [$"({a} == {b} or {a} ~= {a} and {b} ~= {b})"];
        return [$"{a} == {b}"];
    }

    // List / 配列の要素が false でありうる (bool、型引数、object。`t[i] or fault` の形にできない)
    private static bool HasBoolElements(ITypeSymbol? type)
    {
        var element = type switch
        {
            IArrayTypeSymbol array => array.ElementType,
            INamedTypeSymbol { TypeArguments.Length: 1 } list => list.TypeArguments[0],
            _ => null,
        };
        if (element?.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            element = ((INamedTypeSymbol)element).TypeArguments[0];
        return element is ITypeParameterSymbol
            || element?.SpecialType is SpecialType.System_Boolean or SpecialType.System_Object;
    }

    // 要素の default が nil の配列 (`new T[n]` が穴を持つ。TCS1003)
    private static bool HasNilableElements(ITypeSymbol? type) =>
        type is IArrayTypeSymbol { ElementType: var element }
        && (!element.IsValueType || element is ITypeParameterSymbol
            || element.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T);

    internal static bool IsUserStruct(ITypeSymbol? type) =>
        type is { TypeKind: TypeKind.Struct, SpecialType: SpecialType.None }
        && type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T
        && type.TypeKind != TypeKind.Enum
        // タプルの型の Locations は宣言ではなく構文の位置なので、OriginalDefinition で見る
        && type.OriginalDefinition.Locations.Any(l => l.IsInSource);

    // default 値の IL: source 宣言の struct は zero 値の IlNewObj (backend が
    // 型付けできる)、型引数は呼び出し側が渡した既定値 (struct の記述子なら新しい zero 値。
    // __tcs_dv)、それ以外は変換済みリテラル
    private IlExpr DefaultIl(ITypeSymbol? type) =>
        type is ITypeParameterSymbol parameter && TypeArgDefaultIl(parameter) is { } passed
            ? new IlCall("__tcs_dv", [passed])
            : IsUserStruct(type)
                ? new IlNewObj(TypeName(type!), [.. StructTypeArgDefaults(type)])
                : new IlLit(GetDefaultValueForType(type));

    // 型引数の既定値として渡す値: 型引数は受け取ったまま、可変な struct は zero 値と copy の
    // 作り方 (IlStructDescriptor。値を 1 つ渡すと、既定値と代入した値がその table を共有する)
    // enum は member 名で文字列化する記述子 (__tcs_etd が名前の表ごとに 1 つ作る。__tcs_tstr)
    private IlExpr TypeArgDefaultValue(ITypeSymbol type) =>
        type is ITypeParameterSymbol parameter && TypeArgDefaultIl(parameter) is { } passed
            ? passed
            : IsMutableStruct(type)
                ? new IlStructDescriptor(TypeName(type), [.. StructTypeArgDefaults(type)],
                    StructCopiers(type))
                : type is { TypeKind: TypeKind.Enum } && EnumFormatSpec(type) is { } spec
                    ? new IlCall("__tcs_etd", [new IlLit(ModuleArtifactText.EscapeLuaString(spec))])
                    : DefaultIl(type);

    // 値で決まる文字列化 (T? / object / 型引数)。型引数は受け取った記述子が名前の引き方を持てば使う
    private IlExpr StringifyByValue(ITypeSymbol? type, IlExpr value) =>
        type is ITypeParameterSymbol parameter && TypeArgDefaultIl(parameter) is { } passed
            ? new IlCall("__tcs_tstr", [passed, value])
            : new IlCall("__tcs_nstr", [value]);

    // 型引数の型の field / 配列の要素への代入は、struct の記述子なら copy する (__tcs_tc)
    private IlExpr TypeArgStoreCopy(ITypeSymbol? storage, IlExpr value) =>
        storage is ITypeParameterSymbol parameter && TypeArgDefaultIl(parameter) is { } passed
            && !IsFreshStructIl(value)
            ? new IlCall("__tcs_tc", [passed, value])
            : value;

    // generic struct の zero 値 (`S.new`) に渡す型引数の既定値
    private IEnumerable<IlExpr> StructTypeArgDefaults(ITypeSymbol? type) =>
        type is INamedTypeSymbol named && PassesTypeArgDefaults(named.OriginalDefinition)
            ? named.TypeArguments.Select(TypeArgDefaultValue) : [];

    // Lua は型を消すので、型引数の既定値は呼び出し側が隠れ引数で渡す: generic method は
    // 自分の型引数を、generic な class / record / struct は new (struct は zero 値の new と
    // ctor) と static member が型の型引数を引数で受け、instance は new が self に持たせる。
    // C backend は型を特殊化するので使わない
    private bool _typeArgDefaultsOnSelf;

    private static string TypeArgDefaultName(ITypeParameterSymbol parameter) =>
        (parameter.TypeParameterKind == TypeParameterKind.Method
            ? "__tcs_dm_" : "__tcs_dc_") + parameter.Name;

    private static bool PassesTypeArgDefaults(INamedTypeSymbol? type) =>
        type is { TypeKind: TypeKind.Class or TypeKind.Struct, TypeParameters.Length: > 0 }
        && type.Locations.Any(l => l.IsInSource);

    // 型の型引数の既定値を受ける隠れ引数の名前 (new / ctor / static member)
    private static List<string> TypeArgDefaultParams(INamedTypeSymbol? type) =>
        PassesTypeArgDefaults(type) ? [.. type!.TypeParameters.Select(TypeArgDefaultName)] : [];

    private IlExpr? TypeArgDefaultIl(ITypeParameterSymbol parameter)
    {
        var name = TypeArgDefaultName(parameter);
        if (parameter.TypeParameterKind == TypeParameterKind.Method)
            return new IlVar(name);
        if (!PassesTypeArgDefaults(parameter.ContainingType)) return null;
        return _typeArgDefaultsOnSelf ? new IlField(new IlVar("self"), name) : new IlVar(name);
    }

    // 宣言側の隠れ引数 (class の型引数、method の型引数の順)
    private static IEnumerable<string> TypeArgDefaultParams(IMethodSymbol method)
    {
        var classParams = method.MethodKind == MethodKind.Constructor || method.IsStatic
            ? PassesTypeArgDefaults(method.ContainingType)
                ? method.ContainingType.TypeParameters : []
            : [];
        return classParams.Concat(method.TypeParameters).Select(TypeArgDefaultName);
    }

    // 呼び出し側の隠れ引数。宣言側と同じ順で、型引数ごとの既定値
    private IEnumerable<IlExpr> TypeArgDefaultArgs(IMethodSymbol method)
    {
        var classArgs = method.MethodKind == MethodKind.Constructor || method.IsStatic
            ? PassesTypeArgDefaults(method.ContainingType?.OriginalDefinition)
                ? method.ContainingType!.TypeArguments : []
            : [];
        return classArgs.Concat(MethodTypeArguments(method)).Select(TypeArgDefaultValue);
    }

    // 拡張 method を受け手の形で呼ぶと、受け手から推論した型引数は TypeArguments から外れる
    private static IEnumerable<ITypeSymbol> MethodTypeArguments(IMethodSymbol method)
    {
        if (method.ReducedFrom is not { } original) return method.TypeArguments;
        return original.TypeParameters.Select(p =>
            method.GetTypeInferredDuringReduction(p)
            ?? method.TypeArguments[method.TypeParameters.IndexOf(
                method.TypeParameters.First(r =>
                    SymbolEqualityComparer.Default.Equals(r.ReducedFrom, p)))]);
    }

    // 値型の copy 地点 (il-spec §10): 代入 / 引数 / return / 値文脈読み。
    // fresh な値 (IsFreshStructValue) は他から参照されないので copy 不要。
    // readonly (record) struct は不変で alias が観測不能なため copy を全省略する。
    // struct の T? (`c ? h.F : null` / `h?.F`) も格納先の値でありうるので、
    // 値があれば中の struct を copy する
    private IlExpr WrapStructCopy(SemanticModel model, ExpressionSyntax src,
        IlExpr built)
    {
        if (IsFreshStructIl(built) || IsFreshStructValue(src))
            return built;
        // target-typed な `c ? h.F : null` は自然な型を持たず、変換先の型 (S?) で決まる
        var info = model.GetTypeInfo(src);
        var type = info.Type ?? info.ConvertedType;
        return IsNullableValueType(type)
            && UnwrapNullable(type) is var inner && IsMutableStruct(inner)
            ? new IlStructCopy(built, TypeName(inner!), Nullable: true, StructCopiers(inner))
            : CopyIfMutableStruct(type, built);
    }

    // IL の形で新しい値と判る値。T? の値なし (nil) と、新しい値の T? への変換も含む
    private static bool IsFreshStructIl(IlExpr built) => built switch
    {
        IlNewObj or IlStructCopy or IlWith or IlTable or IlLit { LuaText: "nil" }
            or IlCall { Callee: "__tcs_dv" or "__tcs_tc" } => true,
        IlCall call when call.Callee.StartsWith("__tcs_tuple", StringComparison.Ordinal) => true,
        IlNullableWrap wrap => IsFreshStructIl(wrap.E),
        IlParen paren => IsFreshStructIl(paren.E),
        _ => false,
    };

    // 値が他から参照されない式か。IIFE かどうかでは決まらない (switch 式や
    // GetValueOrDefault(x) の IIFE は格納先の table をそのまま返し得る) ので
    // 式の形で判定する。object creation (`new S(args)` は S.ctor の IlCall、
    // initializer 付きは IIFE に降りる) と with 式は新しい値、代入式の値と
    // switch 式の arm の値は builder が copy 済み
    private static bool IsFreshStructValue(ExpressionSyntax src) => src switch
    {
        ParenthesizedExpressionSyntax paren => IsFreshStructValue(paren.Expression),
        BaseObjectCreationExpressionSyntax or WithExpressionSyntax or TupleExpressionSyntax
            or AssignmentExpressionSyntax or SwitchExpressionSyntax => true,
        _ => false,
    };

    // 元の式を持たない値 (分解の成分 / foreach 変数 / lambda の parameter) の copy
    private IlExpr CopyIfMutableStruct(ITypeSymbol? type, IlExpr value) =>
        IsMutableStruct(type)
            ? new IlStructCopy(value, TypeName(type!), Copiers: StructCopiers(type))
            : value;

    // generic struct の __copy に渡す型引数の copier (`, M.__copy` の形)。型引数が可変な
    // struct ならその copy (入れ子の generic struct は closure)、__copy の中の型 parameter は
    // 受け取った copier、それ以外は nil。generic なコードの中の型 parameter は copier を
    // 持たないが、値を受け取った具体的な型の側が copy 地点で写し直す
    private string StructCopiers(ITypeSymbol? type, bool inCopy = false)
    {
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Struct } named
            || !PassesTypeArgDefaults(named.OriginalDefinition))
            return "";
        var copiers = named.TypeArguments.Select(a => Copier(a, inCopy)).ToList();
        while (copiers.Count > 0 && copiers[^1] == "nil") copiers.RemoveAt(copiers.Count - 1);
        return string.Concat(copiers.Select(c => ", " + c));
    }

    // 型の値を写す関数 (Lua の式)。写さなくてよい型は nil
    private string Copier(ITypeSymbol type, bool inCopy = false) => type switch
    {
        ITypeParameterSymbol parameter when inCopy => TypeArgCopierName(parameter),
        _ when IsMutableStruct(type) => StructCopiers(type, inCopy) is { Length: > 0 } inner
            ? $"function(v) return {TypeName(type)}.__copy(v{inner}) end"
            : $"{TypeName(type)}.__copy",
        _ => "nil",
    };

    private static string TypeArgCopierName(ITypeParameterSymbol parameter) =>
        "__tcs_cc_" + parameter.Name;

    private static bool IsMutableStruct(ITypeSymbol? type) =>
        IsUserStruct(type) && type is not INamedTypeSymbol { IsReadOnly: true };

    // struct method/accessor の receiver 規則: C# の「変数」
    // (local / param / field / 配列要素 / this) なら直渡しで変異が変数に残り、
    // rvalue (property / List indexer / 呼び出し結果等) はコピーへの変異 =
    // 捨てられる。どちらも C# と一致する
    private static bool IsStructVariableReceiver(SemanticModel model,
        ExpressionSyntax expr) => expr switch
    {
        ThisExpressionSyntax => true,
        IdentifierNameSyntax id => model.GetSymbolInfo(id).Symbol
            is ILocalSymbol or IParameterSymbol or IFieldSymbol,
        MemberAccessExpressionSyntax ma =>
            model.GetSymbolInfo(ma).Symbol is IFieldSymbol,
        ElementAccessExpressionSyntax ea =>
            model.GetSymbolInfo(ea).Symbol is not IPropertySymbol,
        ParenthesizedExpressionSyntax paren =>
            IsStructVariableReceiver(model, paren.Expression),
        MemberBindingExpressionSyntax mb => model.GetSymbolInfo(mb).Symbol
            is IFieldSymbol && ConditionalReceiverIsReference(model, mb),
        ElementBindingExpressionSyntax eb => model.GetSymbolInfo(eb).Symbol
            is not IPropertySymbol && ConditionalReceiverIsReference(model, eb),
        _ => false,
    };

    // `x?.F` / `x?[i]` の binding は x が参照型なら変数。`T?` の x は値の
    // copy (.Value) なので変数ではない
    private static bool ConditionalReceiverIsReference(SemanticModel model,
        ExpressionSyntax binding)
    {
        var access = binding.Ancestors().OfType<ConditionalAccessExpressionSyntax>()
            .FirstOrDefault(ca => ca.WhenNotNull.Span.Contains(binding.Span));
        return access != null
            && model.GetTypeInfo(access.Expression).Type is { IsReferenceType: true };
    }

    private IlExpr StructReceiverArg(SemanticModel model,
        ExpressionSyntax recvSyntax, IlExpr recv) =>
        IsStructVariableReceiver(model, recvSyntax)
            ? recv
            : WrapStructCopy(model, recvSyntax, recv);
}
