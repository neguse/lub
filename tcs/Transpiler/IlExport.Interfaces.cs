using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

public static partial class IlExport
{
    private static IlClassInfo ExportInterface(LuaEmitter emitter, SemanticModel model,
        InterfaceDeclarationSyntax declaration)
    {
        var type = (INamedTypeSymbol)model.GetDeclaredSymbol(declaration)!;
        // 派生した interface の型を通しても継承した member を呼べるように、自分の表に写す
        var methods = new Dictionary<string, IlMethodInfo>();
        foreach (var owner in type.AllInterfaces.Prepend(type))
            foreach (var member in owner.GetMembers())
                foreach (var method in ExportInterfaceMember(emitter, model, member, inherited: owner != type))
                    methods[method.Name] = method;
        return new IlClassInfo(emitter.TypeName(type), null, [], "0",
            [.. methods.Values], IsInterface: true, DisplayName: type.ToDisplayString());
    }

    private static IEnumerable<IlMethodInfo> ExportInterfaceMember(LuaEmitter emitter,
        SemanticModel model, ISymbol member, bool inherited)
    {
        switch (member)
        {
            case IPropertySymbol { IsIndexer: false, IsStatic: false } property:
                var type = property.Type.ToDisplayString();
                if (property.GetMethod != null)
                    yield return new IlMethodInfo($"get_{property.Name}", false, [], null, type, [],
                        IsAbstract: true);
                if (property.SetMethod != null)
                    yield return new IlMethodInfo($"set_{property.Name}", false, ["value"], null, "void",
                        [type], IsAbstract: true);
                break;
            case IMethodSymbol { MethodKind: MethodKind.Ordinary } symbol
                when !(inherited && symbol.IsStatic)
                    && symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
                        is MethodDeclarationSyntax method:
                var declaringModel = model.Compilation.GetSemanticModel(method.SyntaxTree);
                yield return new IlMethodInfo(LuaNaming.MethodName(symbol), symbol.IsStatic,
                    [.. symbol.Parameters.Select(p => p.Name)], null,
                    symbol.ReturnType.ToDisplayString(),
                    [.. symbol.Parameters.Select(p => p.Type.ToDisplayString())],
                    [.. method.ParameterList.Parameters.Select(p => p.Default is { } d
                        ? emitter.ExportExprIl(declaringModel, d.Value) : null)], IsAbstract: symbol.IsAbstract,
                    OutParameters: [.. OutParameterNames(method.ParameterList)]);
                break;
        }
    }
}
