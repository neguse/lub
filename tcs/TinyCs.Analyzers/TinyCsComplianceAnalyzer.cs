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

    private static readonly DiagnosticDescriptor DotnetMathRule = new(
        TinyCsDiagnosticIds.DotnetMath,
        "System.Math transcendental runs the .NET implementation",
        "'{0}' runs the .NET implementation and can differ from tcs2c / Lua; call TinySystem.Math (TinySystem.props maps Math to it)",
        "TinyCSharp",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Reports System.Math transcendental functions, whose results on .NET differ from the TinyC# runtime/math implementation.");

    // 結果が実装で変わる関数。Abs / Min / Max / Floor / Sqrt / Round などは
    // IEEE-754 で結果が決まるので対象外
    private static readonly ImmutableHashSet<string> DotnetMathNames =
        ImmutableHashSet.Create("Sin", "Cos", "Tan", "Exp", "Log", "Atan2", "Pow");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(UnsupportedSyntaxRule, UnsupportedApiRule,
            UnsupportedCollectionNullRule, DotnetMathRule);

    public override void Initialize(AnalysisContext context)
    {
        // 生成されたコードも transpiler は変換するので診断する
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze
            | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(AnalyzeSyntax,
            TinyCsComplianceFacts.UnsupportedSyntaxKinds);
        context.RegisterSyntaxNodeAction(AnalyzeCollectionNull,
            TinyCsComplianceFacts.CollectionNullSyntaxKinds);
        context.RegisterSemanticModelAction(AnalyzeSemanticSyntax);
        context.RegisterSyntaxNodeAction(AnalyzeTypeMention, SyntaxKind.IdentifierName,
            SyntaxKind.GenericName, SyntaxKind.QualifiedName, SyntaxKind.AliasQualifiedName);
        context.RegisterOperationAction(AnalyzeOperation,
            OperationKind.Invocation,
            OperationKind.PropertyReference,
            OperationKind.FieldReference,
            OperationKind.ObjectCreation);
        context.RegisterOperationAction(AnalyzeUnsupportedOperationSyntax,
            OperationKind.NameOf, OperationKind.Invocation, OperationKind.Binary,
            OperationKind.Unary, OperationKind.Conversion, OperationKind.Increment,
            OperationKind.Decrement, OperationKind.CompoundAssignment);
        context.RegisterOperationAction(AnalyzeDotnetMath,
            OperationKind.Invocation);
    }

    private static void AnalyzeDotnetMath(OperationAnalysisContext context)
    {
        var method = ((IInvocationOperation)context.Operation).TargetMethod;
        var type = method.ContainingType?.ToDisplayString();
        // MathF は TCS1002 (サブセット外) が出る
        if (type != "System.Math" || !DotnetMathNames.Contains(method.Name))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(DotnetMathRule,
            context.Operation.Syntax.GetLocation(), $"{type}.{method.Name}"));
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

    private static void AnalyzeTypeMention(SyntaxNodeAnalysisContext context)
    {
        if (TinyCsComplianceFacts.TryGetUnsupportedTypeMention(context.Node,
                context.SemanticModel, out var apiName))
            context.ReportDiagnostic(Diagnostic.Create(UnsupportedApiRule,
                context.Node.GetLocation(), apiName));
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

    // 意味を見る規則は transpiler と同じく tree の全 node で判定する (規則ごとの node の種類を
    // analyzer が別に持たない)。値で比べる key の Dictionary (#185) などは transpiler では error、
    // analyzer は他の build を止める診断と同じく warning
    private static void AnalyzeSemanticSyntax(SemanticModelAnalysisContext context)
    {
        foreach (var node in context.SemanticModel.SyntaxTree.GetRoot(context.CancellationToken)
                     .DescendantNodes())
        {
            if (TinyCsComplianceFacts.TryGetUnsupportedSemanticSyntax(node,
                    context.SemanticModel, null, out var syntaxName))
                context.ReportDiagnostic(Diagnostic.Create(UnsupportedSyntaxRule,
                    node.GetLocation(), syntaxName));
        }
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
        if (TinyCsComplianceFacts.IsWithinNameOf(context.Operation)
            || TinyCsComplianceFacts.IsInAttribute(context.Operation.Syntax)) return;

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
