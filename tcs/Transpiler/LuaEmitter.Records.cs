using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

// record class の emit。instance は class と同じ table + metatable で、
// 基底 record とは __index で繋ぐ。positional parameter のうち C# が
// property を合成するもの (基底から同名 member を継承していないもの) が
// 自分の field になる
public partial class LuaEmitter
{
    private void VisitRecord(SemanticModel model, RecordDeclarationSyntax rec)
    {
        SetSource(rec);
        var symbol = model.GetDeclaredSymbol(rec)!;
        var name = TypeName(symbol);
        var baseRecord = symbol.BaseType is { SpecialType: SpecialType.None } b
            ? b : null;

        var info = BeginType(name, "record", symbol);
        AppendLine($"{name}.__index = {name}");
        info.DefinitionKeys.Add("__index");
        if (baseRecord != null)
        {
            var baseName = TypeName(baseRecord);
            info.BaseName = baseName;
            if (_emittedTypeNames.Contains(baseName))
                AppendLine($"setmetatable({name}, {{__index = {baseName}}})");
            else
                _pendingBaseLinks.Add((name, baseName));
        }
        AppendLine();

        // positional parameter は Lua の local としては C# 名のまま、field
        // としては写像後の名前で持つ
        var paramNames = rec.ParameterList?.Parameters
            .Select(p => p.Identifier.ValueText).ToList() ?? [];
        var ownFields = paramNames
            .Where(p => IsSynthesizedPositional(symbol, p))
            .Select(p => $"{N(p)} = {p}").ToList();
        info.InstanceShape = string.Join("\n", (rec.ParameterList?.Parameters.ToList() ?? [])
            .Where(p => IsSynthesizedPositional(symbol, p.Identifier.ValueText))
            .Select(p => N(p.Identifier.ValueText) + ":" + model.GetDeclaredSymbol(p)?.Type
                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        info.DefinitionKeys.Add("new");

        AppendLine($"function {name}.new({string.Join(", ", paramNames)})");
        _indent++;
        if (baseRecord != null)
        {
            // 基底の primary ctor 引数は派生の parameter を読める
            var baseArgs = rec.BaseList?.Types
                .OfType<PrimaryConstructorBaseTypeSyntax>().FirstOrDefault()
                ?.ArgumentList.Arguments
                .Select(a => RenderExprViaIl(model, a.Expression)) ?? [];
            AppendLine($"local self = setmetatable({TypeName(baseRecord)}.new(" +
                $"{string.Join(", ", baseArgs)}), {name})");
            foreach (var field in ownFields)
                AppendLine($"self.{field}");
        }
        else
            AppendLine($"local self = setmetatable({{{string.Join(", ", ownFields)}}}, {name})");
        EmitInstanceRegistration(name);
        AppendLine("return self");
        _indent--;
        AppendLine("end");
        AppendLine();

        // __eq: C# の合成 Equals と同じく実行時型 (EqualityContract) の一致を
        // 先に見て、基底から順に各 record が宣言した field を値等価で比べる。
        // Lua は metamethod を rawget で引くので、派生ごとに定義する
        info.DefinitionKeys.Add("__eq");
        var chain = new List<ITypeSymbol>();
        for (INamedTypeSymbol? t = symbol; t is { SpecialType: SpecialType.None }; t = t.BaseType)
            chain.Insert(0, t);
        var eqParts = chain.SelectMany(ValueMembers)
            .SelectMany(m => MemberEqualityParts(m.Type, $"a.{m.Name}", $"b.{m.Name}"))
            .Prepend("getmetatable(a) == getmetatable(b)");
        AppendLine($"function {name}.__eq(a, b)");
        _indent++;
        AppendLine($"return {string.Join(" and ", eqParts)}");
        _indent--;
        AppendLine("end");
        AppendLine();

        // static field / static auto property は class と同じく pre-zero して
        // initializer を宣言順に後で流す
        var staticFieldInits = new List<(string Name, ExpressionSyntax? Init, ITypeSymbol? Type)>();
        foreach (var member in rec.Members)
        {
            if (member is FieldDeclarationSyntax field && IsStaticField(field))
            {
                var type = model.GetTypeInfo(field.Declaration.Type).Type;
                foreach (var v in field.Declaration.Variables)
                    staticFieldInits.Add((N(v.Identifier.ValueText), v.Initializer?.Value, type));
            }
            else if (member is PropertyDeclarationSyntax prop && IsStaticAutoProperty(prop))
                staticFieldInits.Add((N(prop.Identifier.ValueText), prop.Initializer?.Value,
                    model.GetTypeInfo(prop.Type).Type));
        }
        EmitStaticPreZero(name, info, staticFieldInits);

        // Emit any explicitly defined methods and operator overloads
        var operators = new List<OperatorDeclarationSyntax>();
        foreach (var member in rec.Members)
        {
            switch (member)
            {
                case FieldDeclarationSyntax field when IsStaticField(field):
                case PropertyDeclarationSyntax prop when IsStaticAutoProperty(prop):
                    break;
                case MethodDeclarationSyntax method:
                    VisitMethod(model, name, method);
                    break;
                case OperatorDeclarationSyntax op
                    when TinyCsComplianceFacts.TryGetOperatorMetamethod(op, out _):
                    operators.Add(op);
                    break;
                default:
                    WarnUnsupportedMember(member);
                    break;
            }
        }

        EmitOperators(model, name, operators);
        DeferStaticFieldInitializers(model, name, info, staticFieldInits);
        _currentType = null;
    }

    private static bool IsStaticAutoProperty(PropertyDeclarationSyntax prop) =>
        prop.Modifiers.Any(SyntaxKind.StaticKeyword) && IsAutoProperty(prop);

    // base の primary ctor へ渡すだけの parameter (同名 member を継承して
    // いる) には C# も property を合成しない
    internal static bool IsSynthesizedPositional(INamedTypeSymbol record,
        string parameter) =>
        record.GetMembers(parameter).Any(m =>
            SymbolEqualityComparer.Default.Equals(m.ContainingType, record));
}
