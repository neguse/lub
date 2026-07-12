using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
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
        context.RegisterOperationAction(AnalyzeOperation,
            OperationKind.Invocation,
            OperationKind.PropertyReference,
            OperationKind.FieldReference,
            OperationKind.ObjectCreation);
        context.RegisterOperationAction(AnalyzeUnsupportedOperationSyntax,
            OperationKind.NameOf);
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

        if (!TinyCsComplianceFacts.TryGetUnsupportedApi(symbol, out var apiName))
            return;

        context.ReportDiagnostic(Diagnostic.Create(UnsupportedApiRule,
            context.Operation.Syntax.GetLocation(), apiName));
    }
}
