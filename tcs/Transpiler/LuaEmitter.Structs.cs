using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// struct の emit (T219b)。instance は metatable 無しの plain table で、
// member は静的自由関数として同名 table に載る。
public partial class LuaEmitter
{
    // struct (T219b(a): field + instance member)。instance は metatable 無しの
    // plain table で copy (__tcs_scopy) と両立する。member は静的自由関数
    // (`S.M(self, ...)`) — struct は継承がなく呼び出しサイトの静的型が確定
    // するため動的ディスパッチ不要で、呼び出し側 (IlBuild) が直接呼ぶ。
    private void VisitStruct(SemanticModel model, StructDeclarationSyntax structDecl)
    {
        SetSource(structDecl);
        var name = structDecl.Identifier.ValueText;
        AppendLine($"{name} = {{}}");
        AppendLine();
        _currentType?.DefinitionKeys.Add("new");
        AppendLine($"function {name}.new()");
        _indent++;
        AppendLine("local self = {}");
        foreach (var field in structDecl.Members.OfType<FieldDeclarationSyntax>())
        {
            foreach (var v in field.Declaration.Variables)
            {
                var type = (model.GetDeclaredSymbol(v) as IFieldSymbol)?.Type;
                AppendLine($"self.{v.Identifier.ValueText} = " +
                    $"{GetDefaultValueForType(type)}");
            }
        }
        foreach (var prop in structDecl.Members.OfType<PropertyDeclarationSyntax>()
            .Where(p => IsAutoProperty(p)
                && !p.Modifiers.Any(SyntaxKind.StaticKeyword)))
        {
            var type = (model.GetDeclaredSymbol(prop) as IPropertySymbol)?.Type;
            AppendLine($"self.{prop.Identifier.ValueText} = " +
                $"{GetDefaultValueForType(type)}");
        }
        AppendLine("return self");
        _indent--;
        AppendLine("end");
        AppendLine();

        var ctor = structDecl.Members.OfType<ConstructorDeclarationSyntax>()
            .FirstOrDefault(c => !c.Modifiers.Any(SyntaxKind.StaticKeyword)
                && c.ParameterList.Parameters.Count > 0);
        if (ctor != null)
            EmitStructCtor(model, name, structDecl, ctor);

        foreach (var member in structDecl.Members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax method
                    when !method.Modifiers.Any(SyntaxKind.StaticKeyword):
                    VisitMethod(model, name, method, explicitSelf: true);
                    break;
                case PropertyDeclarationSyntax prop
                    when !IsAutoProperty(prop) && prop.AccessorList != null
                        && !prop.Modifiers.Any(SyntaxKind.StaticKeyword):
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

    // 明示 ctor は zero 初期化 (S.new) の上で field initializer → 本文の順で
    // 実行する (C# の struct ctor 意味論)。`new S()` は ctor を通らない zero
    // 値なので、呼び出しは builder が S.ctor へ振り分ける
    private void EmitStructCtor(SemanticModel model, string name,
        StructDeclarationSyntax structDecl, ConstructorDeclarationSyntax ctor)
    {
        SetSource(ctor);
        var ctorParams = ctor.ParameterList.Parameters
            .Select(p => p.Identifier.ValueText).ToList();
        _currentType?.DefinitionKeys.Add("ctor");
        AppendLine($"function {name}.ctor({string.Join(", ", ctorParams)})");
        _indent++;
        EmitParameterDefaults(model, ctor.ParameterList);
        AppendLine($"local self = {name}.new()");
        foreach (var field in structDecl.Members.OfType<FieldDeclarationSyntax>())
            foreach (var v in field.Declaration.Variables)
                if (v.Initializer != null)
                    AppendLine($"self.{v.Identifier.ValueText} = " +
                        $"{VisitExpression(model, v.Initializer.Value)}");
        foreach (var prop in structDecl.Members.OfType<PropertyDeclarationSyntax>())
            if (prop.Initializer != null)
                AppendLine($"self.{prop.Identifier.ValueText} = " +
                    $"{VisitExpression(model, prop.Initializer.Value)}");
        if (ctor.Body != null && !TryEmitStatsViaIl(model, ctor.Body.Statements))
        {
            LegacyBodies++;
            WarnIfStructInLegacyBody(model, ctor.Body);
            foreach (var stmt in ctor.Body.Statements)
                VisitStatement(model, stmt);
        }
        AppendLine("return self");
        _indent--;
        AppendLine("end");
        AppendLine();
    }

    // struct 値が legacy fallback 経路に流れると copy 意味論が消えるため、
    // silent wrong-code にせず診断する (M5 v1 の安全網)
    private void WarnIfStructInLegacyBody(SemanticModel model, SyntaxNode body)
    {
        var offender = body.DescendantNodesAndSelf()
            .FirstOrDefault(n =>
                (n is ObjectCreationExpressionSyntax or VariableDeclarationSyntax
                    or ParameterSyntax)
                && n switch
                {
                    ObjectCreationExpressionSyntax oc =>
                        IsUserStruct(model.GetTypeInfo(oc).Type),
                    VariableDeclarationSyntax vd =>
                        IsUserStruct(model.GetTypeInfo(vd.Type).Type),
                    ParameterSyntax { Type: { } pt } =>
                        IsUserStruct(model.GetTypeInfo(pt).Type),
                    _ => false,
                });
        if (offender != null)
            _ = WarnUnsupported(offender, "struct value in legacy-emitted body");
    }

    internal static bool IsUserStruct(ITypeSymbol? type) =>
        type is { TypeKind: TypeKind.Struct, SpecialType: SpecialType.None }
        && type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T
        && type.TypeKind != TypeKind.Enum
        && type.Locations.Any(l => l.IsInSource);

    // record struct (T219b(b))。struct と同じ plain table + 自由関数の上に
    // positional primary ctor と値等価 (op_Equality) を合成する。== の
    // 呼び出しサイトは IlBuild が静的型から直接 op_Equality へ振り分ける
    private void VisitRecordStruct(SemanticModel model,
        RecordDeclarationSyntax rec)
    {
        SetSource(rec);
        var name = rec.Identifier.ValueText;
        var symbol = model.GetDeclaredSymbol(rec);
        AppendLine($"{name} = {{}}");
        AppendLine();
        _currentType?.DefinitionKeys.Add("new");
        AppendLine($"function {name}.new()");
        _indent++;
        AppendLine("local self = {}");
        foreach (var (memberName, memberType) in ValueMembers(symbol))
            AppendLine($"self.{memberName} = " +
                $"{GetDefaultValueForType(memberType)}");
        AppendLine("return self");
        _indent--;
        AppendLine("end");
        AppendLine();

        // positional primary ctor: zero → positional 代入 → initializer の順
        // (initializer は primary ctor param を参照できる — param 名で解決)
        if (rec.ParameterList is { Parameters.Count: > 0 } parameterList)
        {
            var paramNames = parameterList.Parameters
                .Select(p => p.Identifier.ValueText).ToList();
            _currentType?.DefinitionKeys.Add("ctor");
            AppendLine($"function {name}.ctor({string.Join(", ", paramNames)})");
            _indent++;
            AppendLine($"local self = {name}.new()");
            foreach (var p in paramNames)
                AppendLine($"self.{p} = {p}");
            foreach (var field in rec.Members.OfType<FieldDeclarationSyntax>())
                foreach (var v in field.Declaration.Variables)
                    if (v.Initializer != null)
                        AppendLine($"self.{v.Identifier.ValueText} = " +
                            $"{VisitExpression(model, v.Initializer.Value)}");
            foreach (var prop in rec.Members.OfType<PropertyDeclarationSyntax>())
                if (prop.Initializer != null)
                    AppendLine($"self.{prop.Identifier.ValueText} = " +
                        $"{VisitExpression(model, prop.Initializer.Value)}");
            AppendLine("return self");
            _indent--;
            AppendLine("end");
            AppendLine();
        }

        // 値等価。ネスト struct 値は推移的に field 展開する (struct は
        // 循環できないので停止する)
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

        foreach (var member in rec.Members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax method
                    when !method.Modifiers.Any(SyntaxKind.StaticKeyword)
                        && !method.Modifiers.Any(SyntaxKind.OverrideKeyword):
                    VisitMethod(model, name, method, explicitSelf: true);
                    break;
                case PropertyDeclarationSyntax prop
                    when !IsAutoProperty(prop) && prop.AccessorList != null
                        && !prop.Modifiers.Any(SyntaxKind.StaticKeyword):
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

    // 値を構成する member (positional prop の backing field 込み、
    // EqualityContract のような合成 property は field を持たないので除外される)
    private static IEnumerable<(string Name, ITypeSymbol Type)> ValueMembers(
        INamedTypeSymbol? type)
    {
        if (type == null) yield break;
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst) continue;
            yield return field.AssociatedSymbol is IPropertySymbol prop
                ? (prop.Name, field.Type)
                : (field.Name, field.Type);
        }
    }

    private static IEnumerable<string> ValueEqualityParts(ITypeSymbol type,
        string a, string b)
    {
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst) continue;
            var n = field.AssociatedSymbol is IPropertySymbol prop
                ? prop.Name : field.Name;
            if (IsUserStruct(field.Type))
            {
                foreach (var inner in ValueEqualityParts(field.Type,
                    $"{a}.{n}", $"{b}.{n}"))
                    yield return inner;
            }
            else
            {
                yield return $"{a}.{n} == {b}.{n}";
            }
        }
    }
}
