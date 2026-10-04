using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

public static partial class IlExport
{
    private static IlClassInfo ExportInterface(LuaEmitter emitter, SemanticModel model,
        InterfaceDeclarationSyntax declaration)
    {
        var methods = declaration.Members.OfType<MethodDeclarationSyntax>().Select(method =>
        {
            var symbol = (IMethodSymbol)model.GetDeclaredSymbol(method)!;
            return new IlMethodInfo(LuaNaming.MemberName(symbol), symbol.IsStatic,
                [.. symbol.Parameters.Select(p => p.Name)], null,
                symbol.ReturnType.ToDisplayString(),
                [.. symbol.Parameters.Select(p => p.Type.ToDisplayString())],
                [.. method.ParameterList.Parameters.Select(p => p.Default is { } d
                    ? emitter.ExportExprIl(model, d.Value) : null)], IsAbstract: symbol.IsAbstract);
        });
        var type = (INamedTypeSymbol)model.GetDeclaredSymbol(declaration)!;
        return new IlClassInfo(emitter.TypeName(type), null, [], "0",
            [.. methods], IsInterface: true, DisplayName: type.ToDisplayString());
    }
}
