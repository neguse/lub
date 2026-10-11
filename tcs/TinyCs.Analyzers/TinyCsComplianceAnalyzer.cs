using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace TinyCs.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TinyCsComplianceAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor UnsupportedSyntaxRule = new(
        TinyCsDiagnosticIds.UnsupportedSyntax,
        "Unsupported TinyC# syntax",
        "TinyC# does not support syntax '{0}'",
        "TinyCSharp",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Reports C# syntax that is outside the TinyC# baseline.");

    private static readonly DiagnosticDescriptor UnsupportedApiRule = new(
        TinyCsDiagnosticIds.UnsupportedApi,
        "Unsupported TinyC# API",
        "TinyC# does not support API '{0}'",
        "TinyCSharp",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Reports BCL APIs that are outside the TinyC# runtime baseline.");

    private static readonly DiagnosticDescriptor UnsupportedCollectionNullRule = new(
        TinyCsDiagnosticIds.UnsupportedCollectionNull,
        "Unsupported TinyC# collection null",
        "TinyC# does not support null storage here: {0}",
        "TinyCSharp",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Reports null values that cannot be represented in Lua collection tables.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(UnsupportedSyntaxRule, UnsupportedApiRule,
            UnsupportedCollectionNullRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(AnalyzeSyntax,
            TinyCsComplianceFacts.UnsupportedSyntaxKinds);
        context.RegisterSyntaxNodeAction(AnalyzeCollectionNull,
            TinyCsComplianceFacts.CollectionNullSyntaxKinds);
        context.RegisterSyntaxNodeAction(AnalyzeDictionaryKey,
            SyntaxKind.ObjectCreationExpression,
            SyntaxKind.ImplicitObjectCreationExpression,
            SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeClassInheritance,
            SyntaxKind.SimpleBaseType,
            SyntaxKind.PrimaryConstructorBaseType);
        context.RegisterOperationAction(AnalyzeOperation,
            OperationKind.Invocation,
            OperationKind.PropertyReference,
            OperationKind.FieldReference,
            OperationKind.ObjectCreation);
        context.RegisterOperationAction(AnalyzeUnsupportedOperationSyntax,
            OperationKind.NameOf);
        context.RegisterCompilationStartAction(RegisterLuaTypeNameCollisions);
    }

    // 型の Lua global 名の衝突は compilation 全体を見て決まるので、compilation
    // ごとに 1 回だけ求めて型ごとの symbol action で報告する
    private static void RegisterLuaTypeNameCollisions(
        CompilationStartAnalysisContext context)
    {
        var compilation = context.Compilation;
        var collisions = new Lazy<ILookup<ISymbol, (Location Location,
            string SyntaxName)>>(() => TinyCsComplianceFacts
            .FindLuaTypeNameCollisions(compilation, [.. compilation.SyntaxTrees])
            .ToLookup(c => (ISymbol)c.Type, c => (c.Location, c.SyntaxName),
                SymbolEqualityComparer.Default));
        context.RegisterSymbolAction(symbolContext =>
        {
            foreach (var (location, syntaxName) in collisions.Value[symbolContext.Symbol])
                symbolContext.ReportDiagnostic(Diagnostic.Create(
                    UnsupportedSyntaxRule, location, syntaxName));
        }, SymbolKind.NamedType);
    }

    private static void AnalyzeSyntax(SyntaxNodeAnalysisContext context)
    {
        if (!TinyCsComplianceFacts.TryGetUnsupportedSyntax(context.Node,
            out var syntaxName))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(UnsupportedSyntaxRule,
            context.Node.GetLocation(), syntaxName));
    }

    private static void AnalyzeCollectionNull(SyntaxNodeAnalysisContext context)
    {
        if (!TinyCsComplianceFacts.TryGetUnsupportedCollectionNull(context.Node,
            context.SemanticModel, out var description))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            UnsupportedCollectionNullRule,
            context.Node.GetLocation(), description));
    }

    // 値で比べる key の Dictionary (#185)。transpiler は build を止める error、
    // analyzer は他の build を止める診断と同じく warning
    private static void AnalyzeDictionaryKey(SyntaxNodeAnalysisContext context)
    {
        if (!TinyCsComplianceFacts.TryGetUnsupportedDictionaryKey(context.Node,
            context.SemanticModel, out var syntaxName))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(UnsupportedSyntaxRule,
            context.Node.GetLocation(), syntaxName));
    }

    // class / record の基底 class (q.md Q15)。interface の実装は対象外
    private static void AnalyzeClassInheritance(SyntaxNodeAnalysisContext context)
    {
        if (!TinyCsComplianceFacts.TryGetClassInheritance(context.Node,
            context.SemanticModel, out var syntaxName))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(UnsupportedSyntaxRule,
            context.Node.GetLocation(), syntaxName));
    }

    private static void AnalyzeUnsupportedOperationSyntax(
        OperationAnalysisContext context)
    {
        if (!TinyCsComplianceFacts.TryGetUnsupportedSyntax(context.Operation,
            out var syntaxName))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(UnsupportedSyntaxRule,
            context.Operation.Syntax.GetLocation(), syntaxName));
    }

    private static void AnalyzeOperation(OperationAnalysisContext context)
    {
        if (TinyCsComplianceFacts.IsWithinNameOf(context.Operation)) return;

        ISymbol? symbol = context.Operation switch
        {
            IInvocationOperation invocation => invocation.TargetMethod,
            IPropertyReferenceOperation property => property.Property,
            IFieldReferenceOperation field => field.Field,
            IObjectCreationOperation creation => creation.Constructor,
            _ => null,
        };

        var explicitArgs = TinyCsComplianceFacts.CountExplicitArguments(
            context.Operation.Syntax);
        var format = context.Operation is IInvocationOperation { Arguments.Length: > 0 } call
            ? call.Arguments[0].Value.ConstantValue : default;
        if (!TinyCsComplianceFacts.TryGetUnsupportedEnumToString(symbol, format,
                out var apiName)
            && !TinyCsComplianceFacts.TryGetUnsupportedApi(symbol, explicitArgs,
                out apiName))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(UnsupportedApiRule,
            context.Operation.Syntax.GetLocation(), apiName));
    }
}
