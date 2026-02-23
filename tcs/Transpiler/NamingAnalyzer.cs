using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs;

/// <summary>
/// Enforces C# naming conventions (Microsoft style):
///   PascalCase: classes, enums, methods, properties, public fields, enum members
///   camelCase:  parameters, local variables
///   _camelCase: private fields (optional underscore prefix)
/// </summary>
public static class NamingAnalyzer
{
    public static List<string> Analyze(SyntaxTree tree)
    {
        var warnings = new List<string>();
        var root = tree.GetCompilationUnitRoot();
        Walk(root, warnings, tree);
        return warnings;
    }

    private static void Walk(SyntaxNode node, List<string> warnings, SyntaxTree tree)
    {
        switch (node)
        {
            case ClassDeclarationSyntax cls:
                Check(warnings, tree, cls.Identifier, "class", NamingStyle.PascalCase);
                break;
            case EnumDeclarationSyntax enumDecl:
                Check(warnings, tree, enumDecl.Identifier, "enum", NamingStyle.PascalCase);
                break;
            case EnumMemberDeclarationSyntax enumMember:
                Check(warnings, tree, enumMember.Identifier, "enum member", NamingStyle.PascalCase);
                break;
            case InterfaceDeclarationSyntax iface:
                var ifaceName = iface.Identifier.Text;
                if (!ifaceName.StartsWith('I') || ifaceName.Length < 2 || !char.IsUpper(ifaceName[1]))
                    Warn(warnings, tree, iface.Identifier, "interface", ifaceName, "IPascalCase");
                break;
            case MethodDeclarationSyntax method:
                Check(warnings, tree, method.Identifier, "method", NamingStyle.PascalCase);
                break;
            case PropertyDeclarationSyntax prop:
                Check(warnings, tree, prop.Identifier, "property", NamingStyle.PascalCase);
                break;
            case FieldDeclarationSyntax field:
            {
                bool isPrivate = !field.Modifiers.Any(SyntaxKind.PublicKeyword)
                    && !field.Modifiers.Any(SyntaxKind.InternalKeyword)
                    && !field.Modifiers.Any(SyntaxKind.ProtectedKeyword);
                foreach (var v in field.Declaration.Variables)
                {
                    if (isPrivate)
                        Check(warnings, tree, v.Identifier, "private field", NamingStyle.CamelCaseOrUnderscore);
                    else
                        Check(warnings, tree, v.Identifier, "public field", NamingStyle.PascalCase);
                }
                break;
            }
            case ParameterSyntax param:
                Check(warnings, tree, param.Identifier, "parameter", NamingStyle.CamelCase);
                break;
            case VariableDeclaratorSyntax varDecl
                when varDecl.Parent?.Parent is LocalDeclarationStatementSyntax:
                Check(warnings, tree, varDecl.Identifier, "local variable", NamingStyle.CamelCase);
                break;
            case ConstructorDeclarationSyntax:
                // Constructor name matches class name, skip
                break;
        }

        foreach (var child in node.ChildNodes())
            Walk(child, warnings, tree);
    }

    private enum NamingStyle
    {
        PascalCase,
        CamelCase,
        CamelCaseOrUnderscore,
    }

    private static void Check(List<string> warnings, SyntaxTree tree,
        SyntaxToken identifier, string kind, NamingStyle style)
    {
        var name = identifier.Text;
        if (name.Length == 0) return;

        bool valid = style switch
        {
            NamingStyle.PascalCase => char.IsUpper(name[0]),
            NamingStyle.CamelCase => char.IsLower(name[0]),
            NamingStyle.CamelCaseOrUnderscore =>
                char.IsLower(name[0]) || (name[0] == '_' && name.Length > 1 && char.IsLower(name[1])),
            _ => true
        };

        if (!valid)
        {
            var expected = style switch
            {
                NamingStyle.PascalCase => "PascalCase",
                NamingStyle.CamelCase => "camelCase",
                NamingStyle.CamelCaseOrUnderscore => "camelCase or _camelCase",
                _ => "?"
            };
            Warn(warnings, tree, identifier, kind, name, expected);
        }
    }

    private static void Warn(List<string> warnings, SyntaxTree tree,
        SyntaxToken identifier, string kind, string name, string expected)
    {
        var loc = identifier.GetLocation().GetLineSpan();
        var line = loc.StartLinePosition.Line + 1;
        var col = loc.StartLinePosition.Character + 1;
        var file = loc.Path;
        var prefix = string.IsNullOrEmpty(file) ? "" : $"{file}";
        warnings.Add($"{prefix}({line},{col}): naming: {kind} '{name}' should be {expected}");
    }
}
